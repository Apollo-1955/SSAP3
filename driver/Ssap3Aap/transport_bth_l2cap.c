/*
 * SSAP3 AAP profile driver: L2CAP transport for the AACP channel (PSM 0x1001).
 *
 * Adapted from the GPL-3.0 LibrePods Windows profile transport and hardened for
 * SSAP3 (SECURITY_AUDIT.md):
 *   S-06  every BRB has a bounded timeout; the reader stop path cannot hang or
 *         free memory that a late completion or DPC still uses;
 *   S-08  sessions always target the PDO's own remote address;
 *   S-09  outbound packets are limited to the AACP framing SSAP3 uses.
 * The driver never interprets inbound packet content.
 */
#include "transport.h"

#include <bthioctl.h>

/* Declared with their WDK role types so Code Analysis, SDV and Microsoft's
 * driver CodeQL queries (cpp/drivers/role-type-correctly-used) know their
 * IRQL and calling contract. */
static KDEFERRED_ROUTINE Ssap3ReaderResubmitDpc;
static EVT_WDF_REQUEST_COMPLETION_ROUTINE Ssap3ReaderCompletion;

static
BOOLEAN
Ssap3TryParseHexNibble(
    _In_ CHAR Character,
    _Out_ UCHAR* Value
    )
{
    if (Character >= '0' && Character <= '9') {
        *Value = (UCHAR)(Character - '0');
        return TRUE;
    }

    if (Character >= 'a' && Character <= 'f') {
        *Value = (UCHAR)(10 + (Character - 'a'));
        return TRUE;
    }

    if (Character >= 'A' && Character <= 'F') {
        *Value = (UCHAR)(10 + (Character - 'A'));
        return TRUE;
    }

    *Value = 0;
    return FALSE;
}

static
NTSTATUS
Ssap3ParseBluetoothAddress(
    _In_z_ const CHAR* BluetoothAddress,
    _Out_ BTH_ADDR* RemoteAddress
    )
{
    UCHAR octets[6];
    ULONG index;

    *RemoteAddress = 0;
    if (BluetoothAddress == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    if (RtlStringCbLengthA(
            BluetoothAddress,
            SSAP3_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH,
            NULL) != STATUS_SUCCESS) {
        return STATUS_INVALID_PARAMETER;
    }

    for (index = 0; index < RTL_NUMBER_OF(octets); index++) {
        ULONG offset;
        UCHAR highNibble;
        UCHAR lowNibble;

        offset = index * 3;
        if (!Ssap3TryParseHexNibble(BluetoothAddress[offset], &highNibble) ||
            !Ssap3TryParseHexNibble(BluetoothAddress[offset + 1], &lowNibble)) {
            return STATUS_INVALID_PARAMETER;
        }

        if (index < (RTL_NUMBER_OF(octets) - 1) && BluetoothAddress[offset + 2] != ':') {
            return STATUS_INVALID_PARAMETER;
        }

        octets[index] = (UCHAR)((highNibble << 4) | lowNibble);
    }

    if (BluetoothAddress[17] != '\0') {
        return STATUS_INVALID_PARAMETER;
    }

    *RemoteAddress =
        ((BTH_ADDR)octets[0] << 40) |
        ((BTH_ADDR)octets[1] << 32) |
        ((BTH_ADDR)octets[2] << 24) |
        ((BTH_ADDR)octets[3] << 16) |
        ((BTH_ADDR)octets[4] << 8) |
        ((BTH_ADDR)octets[5]);
    return STATUS_SUCCESS;
}

static
ULONG
Ssap3MapNtStatusToDriverError(
    _In_ NTSTATUS Status
    )
{
    switch (Status) {
        case STATUS_SUCCESS:
            return SSAP3_DRIVER_ERROR_NONE;

        case STATUS_NOT_IMPLEMENTED:
        case STATUS_NOT_SUPPORTED:
        case STATUS_INVALID_DEVICE_REQUEST:
            return SSAP3_DRIVER_ERROR_NOT_IMPLEMENTED;

        case STATUS_ACCESS_DENIED:
            return SSAP3_DRIVER_ERROR_ACCESS_DENIED;

        case STATUS_INVALID_PARAMETER:
        case STATUS_INVALID_BUFFER_SIZE:
        case STATUS_BUFFER_TOO_SMALL:
            return SSAP3_DRIVER_ERROR_INVALID_ARGUMENT;

        case STATUS_CONNECTION_DISCONNECTED:
        case STATUS_CONNECTION_RESET:
            return SSAP3_DRIVER_ERROR_DISCONNECTED;

        case STATUS_DEVICE_NOT_CONNECTED:
        case STATUS_DEVICE_NOT_READY:
        case STATUS_INVALID_DEVICE_STATE:
        case STATUS_PROTOCOL_UNREACHABLE:
        case STATUS_CONNECTION_REFUSED:
        case STATUS_TIMEOUT:
        case STATUS_IO_TIMEOUT:
        case STATUS_CANCELLED:
            return SSAP3_DRIVER_ERROR_TRANSPORT_BLOCKED;

        default:
            return SSAP3_DRIVER_ERROR_INTERNAL;
    }
}

static
NTSTATUS
Ssap3SubmitBrbSynchronously(
    _In_ WDFDEVICE Device,
    _Inout_ PBRB Brb,
    _In_ ULONG TimeoutMilliseconds
    )
{
    WDFIOTARGET ioTarget;
    WDF_MEMORY_DESCRIPTOR otherArg1;
    WDF_REQUEST_SEND_OPTIONS sendOptions;
    NTSTATUS status;
    ULONG_PTR bytesReturned;

    ioTarget = WdfDeviceGetIoTarget(Device);
    if (ioTarget == WDF_NO_HANDLE) {
        return STATUS_INVALID_DEVICE_STATE;
    }

    bytesReturned = 0;
    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&otherArg1, Brb, Brb->BrbHeader.Length);
    WDF_REQUEST_SEND_OPTIONS_INIT(&sendOptions, WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&sendOptions, WDF_REL_TIMEOUT_IN_MS(TimeoutMilliseconds));

    status = WdfIoTargetSendInternalIoctlOthersSynchronously(
        ioTarget,
        WDF_NO_HANDLE,
        IOCTL_INTERNAL_BTH_SUBMIT_BRB,
        &otherArg1,
        NULL,
        NULL,
        &sendOptions,
        &bytesReturned);

    if (NT_SUCCESS(status) && !NT_SUCCESS(Brb->BrbHeader.Status)) {
        status = Brb->BrbHeader.Status;
    }

    if (NT_SUCCESS(status) && BTH_ERROR(Brb->BrbHeader.BtStatus)) {
        status = STATUS_UNSUCCESSFUL;
    }

    return status;
}

static
VOID
Ssap3QueueReceivedPacket(
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _In_reads_bytes_(PacketLength) const UCHAR* Packet,
    _In_ ULONG PacketLength
    )
{
    KIRQL oldIrql;
    ULONG slot;

    if (PacketLength == 0 || PacketLength > SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH) {
        return;
    }

    KeAcquireSpinLock(&Session->PacketLock, &oldIrql);

    /* Bounded queue: the oldest packet is dropped when a slow client falls behind. */
    if (Session->PacketQueueCount == SSAP3_PACKET_QUEUE_DEPTH) {
        Session->PacketQueueHead = (Session->PacketQueueHead + 1u) % SSAP3_PACKET_QUEUE_DEPTH;
        Session->PacketQueueCount--;
    }

    slot = Session->PacketQueueTail;
    RtlCopyMemory(Session->PacketQueue[slot], Packet, PacketLength);
    Session->PacketQueueLengths[slot] = PacketLength;
    Session->PacketQueueTimes[slot] = KeQueryInterruptTime();
    Session->PacketQueueTail = (Session->PacketQueueTail + 1u) % SSAP3_PACKET_QUEUE_DEPTH;
    Session->PacketQueueCount++;

    KeReleaseSpinLock(&Session->PacketLock, oldIrql);
}

static
NTSTATUS
Ssap3FormatContinuousRead(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    struct _BRB_L2CA_ACL_TRANSFER* transferBrb;
    PVOID packetBuffer;
    size_t packetBufferLength;

    deviceContext = Ssap3GetDeviceContext(Device);
    deviceContext->ProfileDriverInterface.BthReuseBrb(&Session->ReaderBrb, BRB_L2CA_ACL_TRANSFER);

    packetBuffer = WdfMemoryGetBuffer(Session->ReaderBufferMemory, &packetBufferLength);
    if (packetBuffer == NULL ||
        packetBufferLength == 0 ||
        packetBufferLength > SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH) {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    transferBrb = &Session->ReaderBrb.BrbL2caAclTransfer;
    transferBrb->BtAddress = Session->RemoteAddress;
    transferBrb->ChannelHandle = Session->ChannelHandle;
    transferBrb->TransferFlags = ACL_TRANSFER_DIRECTION_IN | ACL_SHORT_TRANSFER_OK;
    transferBrb->BufferSize = (ULONG)packetBufferLength;
    transferBrb->Buffer = packetBuffer;
    transferBrb->BufferMDL = NULL;
    transferBrb->Timeout = 0;
    transferBrb->RemainingBufferSize = 0;

    return WdfIoTargetFormatRequestForInternalIoctlOthers(
        WdfDeviceGetIoTarget(Device),
        Session->ReaderRequest,
        IOCTL_INTERNAL_BTH_SUBMIT_BRB,
        Session->ReaderBrbMemory,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL);
}

/*
 * Submits the next continuous read. Runs at PASSIVE_LEVEL (first submission,
 * under SessionLock) or DISPATCH_LEVEL (resubmit DPC).
 *
 * Stop protocol: the submitter publishes ReaderInFlight = 1 and then reads
 * ReaderStopping. The stopper publishes ReaderStopping = 1 and then reads
 * ReaderInFlight. Interlocked operations are full barriers, so at least one
 * side sees the other. A stopper that cancels before WdfRequestSend is
 * covered by the post-send re-check below, which cancels the read itself.
 */
static
NTSTATUS
Ssap3SubmitContinuousRead(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session
    )
{
    WDF_REQUEST_REUSE_PARAMS reuseParameters;
    NTSTATUS status;

    WDF_REQUEST_REUSE_PARAMS_INIT(&reuseParameters, WDF_REQUEST_REUSE_NO_FLAGS, STATUS_UNSUCCESSFUL);
    status = WdfRequestReuse(Session->ReaderRequest, &reuseParameters);
    if (NT_SUCCESS(status)) {
        status = Ssap3FormatContinuousRead(Device, Session);
    }

    if (!NT_SUCCESS(status)) {
        Session->ReaderStatus = status;
        KeSetEvent(&Session->ReaderStoppedEvent, IO_NO_INCREMENT, FALSE);
        return status;
    }

    WdfRequestSetCompletionRoutine(Session->ReaderRequest, Ssap3ReaderCompletion, Session);
    KeClearEvent(&Session->ReaderStoppedEvent);
    InterlockedExchange(&Session->ReaderInFlight, 1);

    if (InterlockedCompareExchange(&Session->ReaderStopping, 0, 0) != 0) {
        InterlockedExchange(&Session->ReaderInFlight, 0);
        KeSetEvent(&Session->ReaderStoppedEvent, IO_NO_INCREMENT, FALSE);
        return STATUS_CANCELLED;
    }

    if (!WdfRequestSend(Session->ReaderRequest, WdfDeviceGetIoTarget(Device), WDF_NO_SEND_OPTIONS)) {
        status = WdfRequestGetStatus(Session->ReaderRequest);
        Session->ReaderStatus = status;
        InterlockedExchange(&Session->ReaderInFlight, 0);
        KeSetEvent(&Session->ReaderStoppedEvent, IO_NO_INCREMENT, FALSE);
        return status;
    }

    if (InterlockedCompareExchange(&Session->ReaderStopping, 0, 0) != 0) {
        (VOID)WdfRequestCancelSentRequest(Session->ReaderRequest);
    }

    return STATUS_SUCCESS;
}

static
VOID
Ssap3ReaderResubmitDpc(
    struct _KDPC* Dpc,
    PVOID DeferredContext,
    PVOID SystemArgument1,
    PVOID SystemArgument2
    )
{
    WDFDEVICE device;
    PSSAP3_SESSION_ENTRY session;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument2);

    device = (WDFDEVICE)DeferredContext;
    session = (PSSAP3_SESSION_ENTRY)SystemArgument1;
    if (device != NULL && session != NULL) {
        (VOID)Ssap3SubmitContinuousRead(device, session);
    }
}

static
VOID
Ssap3ReaderCompletion(
    WDFREQUEST Request,
    WDFIOTARGET Target,
    PWDF_REQUEST_COMPLETION_PARAMS Params,
    WDFCONTEXT Context
    )
{
    PSSAP3_SESSION_ENTRY session;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Request);
    UNREFERENCED_PARAMETER(Target);

    session = (PSSAP3_SESSION_ENTRY)Context;
    status = Params->IoStatus.Status;

    if (NT_SUCCESS(status)) {
        ULONG packetLength;
        const UCHAR* packetBuffer;

        packetLength = session->ReaderBrb.BrbL2caAclTransfer.BufferSize;
        packetBuffer = (const UCHAR*)WdfMemoryGetBuffer(session->ReaderBufferMemory, NULL);
        if (packetBuffer != NULL &&
            packetLength != 0 &&
            packetLength <= SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH) {
            Ssap3QueueReceivedPacket(session, packetBuffer, packetLength);
        }
    } else {
        session->ReaderStatus = status;
    }

    InterlockedExchange(&session->ReaderInFlight, 0);

    if (InterlockedCompareExchange(&session->ReaderStopping, 0, 0) != 0 || !NT_SUCCESS(status)) {
        KeSetEvent(&session->ReaderStoppedEvent, IO_NO_INCREMENT, FALSE);
        return;
    }

    /* Resubmit from a DPC to avoid recursion in the completion path. */
    if (!KeInsertQueueDpc(&session->ReaderResubmitDpc, session, NULL)) {
        session->ReaderStatus = STATUS_DEVICE_BUSY;
        KeSetEvent(&session->ReaderStoppedEvent, IO_NO_INCREMENT, FALSE);
    }
}

static
VOID
Ssap3DeleteReader(
    _Inout_ PSSAP3_SESSION_ENTRY Session
    )
{
    if (Session->ReaderRequest != NULL) {
        WdfObjectDelete(Session->ReaderRequest);
    }

    Session->ReaderRequest = NULL;
    Session->ReaderBufferMemory = NULL;
    Session->ReaderBrbMemory = NULL;
}

static
NTSTATUS
Ssap3StartContinuousReader(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session
    )
{
    WDF_OBJECT_ATTRIBUTES attributes;
    NTSTATUS status;

    Session->ReaderRequest = NULL;
    Session->ReaderBufferMemory = NULL;
    Session->ReaderBrbMemory = NULL;
    Session->ReaderStopping = 0;
    Session->ReaderInFlight = 0;
    Session->ReaderStatus = STATUS_SUCCESS;
    Session->PacketQueueHead = 0;
    Session->PacketQueueTail = 0;
    Session->PacketQueueCount = 0;
    KeInitializeSpinLock(&Session->PacketLock);
    KeInitializeEvent(&Session->ReaderStoppedEvent, NotificationEvent, TRUE);
    KeInitializeDpc(&Session->ReaderResubmitDpc, Ssap3ReaderResubmitDpc, Device);

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Device;
    status = WdfRequestCreate(&attributes, WdfDeviceGetIoTarget(Device), &Session->ReaderRequest);
    if (!NT_SUCCESS(status)) {
        Session->ReaderRequest = NULL;
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Session->ReaderRequest;
    status = WdfMemoryCreate(
        &attributes,
        NonPagedPoolNx,
        SSAP3_SESSION_POOL_TAG,
        SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH,
        &Session->ReaderBufferMemory,
        NULL);
    if (!NT_SUCCESS(status)) {
        Ssap3DeleteReader(Session);
        return status;
    }

    RtlZeroMemory(&Session->ReaderBrb, sizeof(Session->ReaderBrb));
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Session->ReaderRequest;
    status = WdfMemoryCreatePreallocated(
        &attributes,
        &Session->ReaderBrb,
        sizeof(Session->ReaderBrb),
        &Session->ReaderBrbMemory);
    if (!NT_SUCCESS(status)) {
        Ssap3DeleteReader(Session);
        return status;
    }

    status = Ssap3SubmitContinuousRead(Device, Session);
    if (!NT_SUCCESS(status)) {
        /* A failed first submission never reached the target: nothing is in flight. */
        Ssap3DeleteReader(Session);
    }

    return status;
}

/*
 * Requests the reader to stop and waits a bounded time for it. Returns TRUE
 * when no read is in flight and no resubmit DPC can still run. PASSIVE_LEVEL.
 */
static
BOOLEAN
Ssap3StopContinuousReader(
    _Inout_ PSSAP3_SESSION_ENTRY Session
    )
{
    LARGE_INTEGER timeout;
    NTSTATUS waitStatus;

    if (Session->ReaderRequest == NULL) {
        return TRUE;
    }

    InterlockedExchange(&Session->ReaderStopping, 1);
    if (InterlockedCompareExchange(&Session->ReaderInFlight, 0, 0) != 0) {
        (VOID)WdfRequestCancelSentRequest(Session->ReaderRequest);
    }

    timeout.QuadPart = WDF_REL_TIMEOUT_IN_MS(SSAP3_READER_STOP_TIMEOUT_MS);
    waitStatus = KeWaitForSingleObject(&Session->ReaderStoppedEvent, Executive, KernelMode, FALSE, &timeout);

    /* A resubmit DPC may be running on another processor: let it finish. */
    KeFlushQueuedDpcs();

    return waitStatus == STATUS_SUCCESS &&
        InterlockedCompareExchange(&Session->ReaderInFlight, 0, 0) == 0;
}

static
BOOLEAN
Ssap3HasProfileInterface(
    _In_ PSSAP3_DEVICE_CONTEXT DeviceContext
    )
{
    return DeviceContext->HasProfileDriverInterface &&
        DeviceContext->ProfileDriverInterface.BthInitializeBrb != NULL &&
        DeviceContext->ProfileDriverInterface.BthReuseBrb != NULL;
}

static
BOOLEAN
Ssap3HasUsableProfileInterface(
    _In_ PSSAP3_DEVICE_CONTEXT DeviceContext
    )
{
    return Ssap3HasProfileInterface(DeviceContext) &&
        DeviceContext->HasRemoteDeviceInfo &&
        DeviceContext->HasAacpEnumeratorInfo &&
        DeviceContext->AacpPort != 0 &&
        DeviceContext->AacpPort <= 0xffffu;
}

static
VOID
Ssap3PopulateDefaultOpenConfig(
    _Out_ struct _BRB_L2CA_OPEN_CHANNEL* OpenChannelBrb
    )
{
    OpenChannelBrb->ChannelFlags = CF_ROLE_EITHER | CF_LINK_NOTHING;

    OpenChannelBrb->ConfigOut.Flags = CFG_MTU;
    OpenChannelBrb->ConfigOut.Mtu.Min = L2CAP_MIN_MTU;
    OpenChannelBrb->ConfigOut.Mtu.Preferred = SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH;
    OpenChannelBrb->ConfigOut.Mtu.Max = SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH;

    OpenChannelBrb->ConfigIn.Flags = CFG_MTU;
    OpenChannelBrb->ConfigIn.Mtu.Min = L2CAP_MIN_MTU;
    OpenChannelBrb->ConfigIn.Mtu.Preferred = SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH;
    OpenChannelBrb->ConfigIn.Mtu.Max = SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH;
    OpenChannelBrb->IncomingQueueDepth = 50;
}

_Use_decl_annotations_
NTSTATUS
Ssap3TransportQueryStatus(
    WDFDEVICE Device,
    ULONG* CapabilityFlags,
    ULONG* DriverError
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;

    deviceContext = Ssap3GetDeviceContext(Device);
    if (Ssap3HasUsableProfileInterface(deviceContext)) {
        *CapabilityFlags =
            SSAP3_DRIVER_CAPABILITY_DRIVER_BACKED_L2CAP |
            SSAP3_DRIVER_CAPABILITY_AACP_CONTROL |
            SSAP3_DRIVER_CAPABILITY_BATTERY_NOTIFICATIONS;
        *DriverError = SSAP3_DRIVER_ERROR_NONE;
        return STATUS_SUCCESS;
    }

    *CapabilityFlags = SSAP3_DRIVER_CAPABILITY_NONE;
    *DriverError = SSAP3_DRIVER_ERROR_TRANSPORT_BLOCKED;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS
Ssap3TransportOpenSession(
    WDFDEVICE Device,
    PSSAP3_SESSION_ENTRY Session,
    ULONG* CapabilityFlags,
    ULONG* Mtu,
    ULONG* DriverError
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    BTH_ADDR requestedAddress;
    BRB brb;
    NTSTATUS status;

    *Mtu = 0;
    *CapabilityFlags = SSAP3_DRIVER_CAPABILITY_NONE;
    Session->ChannelHandle = NULL;
    Session->ReaderRequest = NULL;

    deviceContext = Ssap3GetDeviceContext(Device);
    if (!Ssap3HasUsableProfileInterface(deviceContext)) {
        *DriverError = SSAP3_DRIVER_ERROR_TRANSPORT_BLOCKED;
        return STATUS_INVALID_DEVICE_STATE;
    }

    /* S-08: the channel always targets this PDO's own device. The caller's
     * address only selects between PDOs and must match. */
    status = Ssap3ParseBluetoothAddress(Session->BluetoothAddress, &requestedAddress);
    if (!NT_SUCCESS(status)) {
        *DriverError = SSAP3_DRIVER_ERROR_INVALID_ARGUMENT;
        return status;
    }

    if (requestedAddress != deviceContext->RemoteDeviceInfo.address) {
        *DriverError = SSAP3_DRIVER_ERROR_ADDRESS_MISMATCH;
        return STATUS_INVALID_PARAMETER;
    }

    Session->RemoteAddress = deviceContext->RemoteDeviceInfo.address;

    RtlZeroMemory(&brb, sizeof(brb));
    deviceContext->ProfileDriverInterface.BthInitializeBrb(&brb, BRB_L2CA_OPEN_CHANNEL);
    brb.BrbL2caOpenChannel.ChannelHandle = NULL;
    brb.BrbL2caOpenChannel.Psm = (USHORT)deviceContext->AacpPort;
    brb.BrbL2caOpenChannel.BtAddress = Session->RemoteAddress;
    Ssap3PopulateDefaultOpenConfig(&brb.BrbL2caOpenChannel);

    status = Ssap3SubmitBrbSynchronously(Device, &brb, SSAP3_OPEN_CHANNEL_TIMEOUT_MS);
    if (!NT_SUCCESS(status)) {
        *DriverError = Ssap3MapNtStatusToDriverError(status);
        return status;
    }

    if (brb.BrbL2caOpenChannel.ChannelHandle == NULL) {
        *DriverError = SSAP3_DRIVER_ERROR_INTERNAL;
        return STATUS_UNSUCCESSFUL;
    }

    Session->ChannelHandle = brb.BrbL2caOpenChannel.ChannelHandle;

    status = Ssap3StartContinuousReader(Device, Session);
    if (!NT_SUCCESS(status)) {
        BRB closeBrb;

        RtlZeroMemory(&closeBrb, sizeof(closeBrb));
        deviceContext->ProfileDriverInterface.BthInitializeBrb(&closeBrb, BRB_L2CA_CLOSE_CHANNEL);
        closeBrb.BrbL2caCloseChannel.BtAddress = Session->RemoteAddress;
        closeBrb.BrbL2caCloseChannel.ChannelHandle = Session->ChannelHandle;
        (VOID)Ssap3SubmitBrbSynchronously(Device, &closeBrb, SSAP3_CLOSE_CHANNEL_TIMEOUT_MS);
        Session->ChannelHandle = NULL;
        *DriverError = Ssap3MapNtStatusToDriverError(status);
        return status;
    }

    *Mtu = brb.BrbL2caOpenChannel.OutResults.Params.Mtu != 0
        ? brb.BrbL2caOpenChannel.OutResults.Params.Mtu
        : SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH;
    *CapabilityFlags =
        SSAP3_DRIVER_CAPABILITY_DRIVER_BACKED_L2CAP |
        SSAP3_DRIVER_CAPABILITY_AACP_CONTROL |
        SSAP3_DRIVER_CAPABILITY_BATTERY_NOTIFICATIONS;
    *DriverError = SSAP3_DRIVER_ERROR_NONE;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS
Ssap3TransportCloseSession(
    WDFDEVICE Device,
    PSSAP3_SESSION_ENTRY Session,
    BOOLEAN* CanFree,
    ULONG* DriverError
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    BOOLEAN readerStopped;
    NTSTATUS status;

    *CanFree = FALSE;
    *DriverError = SSAP3_DRIVER_ERROR_NONE;
    status = STATUS_SUCCESS;
    deviceContext = Ssap3GetDeviceContext(Device);

    readerStopped = Ssap3StopContinuousReader(Session);

    if (Session->ChannelHandle != NULL && Ssap3HasProfileInterface(deviceContext)) {
        BRB brb;

        RtlZeroMemory(&brb, sizeof(brb));
        deviceContext->ProfileDriverInterface.BthInitializeBrb(&brb, BRB_L2CA_CLOSE_CHANNEL);
        brb.BrbL2caCloseChannel.BtAddress = Session->RemoteAddress;
        brb.BrbL2caCloseChannel.ChannelHandle = Session->ChannelHandle;
        status = Ssap3SubmitBrbSynchronously(Device, &brb, SSAP3_CLOSE_CHANNEL_TIMEOUT_MS);
        if (!NT_SUCCESS(status)) {
            *DriverError = Ssap3MapNtStatusToDriverError(status);
        }
    }

    Session->ChannelHandle = NULL;

    if (!readerStopped) {
        /* Closing the channel fails any transfer still pending; wait once more. */
        readerStopped = Ssap3StopContinuousReader(Session);
    }

    if (readerStopped) {
        Ssap3DeleteReader(Session);
        *CanFree = TRUE;
    }

    return status;
}

_Use_decl_annotations_
NTSTATUS
Ssap3TransportSendPacket(
    WDFDEVICE Device,
    PSSAP3_SESSION_ENTRY Session,
    const UCHAR* Packet,
    ULONG PacketLength,
    ULONG* DriverError
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    BRB brb;
    NTSTATUS status;

    if (Session->ChannelHandle == NULL || Packet == NULL || PacketLength == 0 ||
        PacketLength > SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH) {
        *DriverError = SSAP3_DRIVER_ERROR_INVALID_ARGUMENT;
        return STATUS_INVALID_PARAMETER;
    }

    deviceContext = Ssap3GetDeviceContext(Device);
    if (!Ssap3HasUsableProfileInterface(deviceContext)) {
        *DriverError = SSAP3_DRIVER_ERROR_TRANSPORT_BLOCKED;
        return STATUS_INVALID_DEVICE_STATE;
    }

    RtlZeroMemory(&brb, sizeof(brb));
    deviceContext->ProfileDriverInterface.BthInitializeBrb(&brb, BRB_L2CA_ACL_TRANSFER);
    brb.BrbL2caAclTransfer.BtAddress = Session->RemoteAddress;
    brb.BrbL2caAclTransfer.ChannelHandle = Session->ChannelHandle;
    brb.BrbL2caAclTransfer.TransferFlags = ACL_TRANSFER_DIRECTION_OUT;
    brb.BrbL2caAclTransfer.BufferSize = PacketLength;
    brb.BrbL2caAclTransfer.Buffer = (PVOID)Packet;
    brb.BrbL2caAclTransfer.BufferMDL = NULL;
    brb.BrbL2caAclTransfer.Timeout = 0;

    status = Ssap3SubmitBrbSynchronously(Device, &brb, SSAP3_SEND_PACKET_TIMEOUT_MS);
    if (!NT_SUCCESS(status)) {
        *DriverError = Ssap3MapNtStatusToDriverError(status);
        return status;
    }

    if (brb.BrbL2caAclTransfer.BufferSize != PacketLength) {
        *DriverError = SSAP3_DRIVER_ERROR_INTERNAL;
        return STATUS_UNSUCCESSFUL;
    }

    *DriverError = SSAP3_DRIVER_ERROR_NONE;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS
Ssap3TransportReadPacket(
    WDFDEVICE Device,
    PSSAP3_SESSION_ENTRY Session,
    UCHAR* PacketBuffer,
    ULONG PacketBufferLength,
    ULONG* PacketLength,
    ULONGLONG* ReceiveInterruptTime,
    ULONG* DriverError
    )
{
    KIRQL oldIrql;
    ULONG availableLength;
    ULONG slot;

    UNREFERENCED_PARAMETER(Device);
    *PacketLength = 0;
    *ReceiveInterruptTime = 0;

    if (Session->ChannelHandle == NULL || PacketBuffer == NULL || PacketBufferLength == 0) {
        *DriverError = SSAP3_DRIVER_ERROR_INVALID_ARGUMENT;
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&Session->PacketLock, &oldIrql);
    if (Session->PacketQueueCount == 0) {
        KeReleaseSpinLock(&Session->PacketLock, oldIrql);

        /* Report a dead channel instead of returning "no data" forever. */
        if (!NT_SUCCESS(Session->ReaderStatus) &&
            InterlockedCompareExchange(&Session->ReaderInFlight, 0, 0) == 0) {
            *DriverError = SSAP3_DRIVER_ERROR_DISCONNECTED;
            return STATUS_CONNECTION_DISCONNECTED;
        }

        *DriverError = SSAP3_DRIVER_ERROR_NONE;
        return STATUS_SUCCESS;
    }

    slot = Session->PacketQueueHead;
    availableLength = Session->PacketQueueLengths[slot];
    if (availableLength > PacketBufferLength || availableLength > SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH) {
        KeReleaseSpinLock(&Session->PacketLock, oldIrql);
        *DriverError = SSAP3_DRIVER_ERROR_INVALID_ARGUMENT;
        return STATUS_BUFFER_TOO_SMALL;
    }

    RtlCopyMemory(PacketBuffer, Session->PacketQueue[slot], availableLength);
    *ReceiveInterruptTime = Session->PacketQueueTimes[slot];
    Session->PacketQueueHead = (Session->PacketQueueHead + 1u) % SSAP3_PACKET_QUEUE_DEPTH;
    Session->PacketQueueCount--;
    KeReleaseSpinLock(&Session->PacketLock, oldIrql);

    *PacketLength = availableLength;
    *DriverError = SSAP3_DRIVER_ERROR_NONE;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
BOOLEAN
Ssap3IsAllowedOutboundPacket(
    const UCHAR* Packet,
    ULONG PacketLength
    )
{
    USHORT opcode;

    if (Packet == NULL || PacketLength < 6 || PacketLength > SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH) {
        return FALSE;
    }

    /* Session handshake: 00 00 04 00 01 00 ... */
    if (Packet[0] == 0x00 && Packet[1] == 0x00 && Packet[2] == 0x04 && Packet[3] == 0x00) {
        return Packet[4] == 0x01 && Packet[5] == 0x00;
    }

    if (Packet[0] != 0x04 || Packet[1] != 0x00 || Packet[2] != 0x04 || Packet[3] != 0x00) {
        return FALSE;
    }

    opcode = (USHORT)(Packet[4] | (Packet[5] << 8));
    switch (opcode) {
        case 0x0009:
            /* Control commands: owns-connection, listening mode, conversational awareness. */
            return PacketLength >= 7 &&
                (Packet[6] == 0x06 || Packet[6] == 0x0D || Packet[6] == 0x28);

        case 0x000F: /* notification request */
        case 0x0017: /* head-tracking stream control */
        case 0x004D: /* feature flags */
            return TRUE;

        default:
            return FALSE;
    }
}
