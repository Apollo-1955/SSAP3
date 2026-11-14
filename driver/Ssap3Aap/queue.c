/*
 * SSAP3 AAP profile driver: IOCTL dispatch.
 *
 * All IOCTLs are METHOD_BUFFERED. Input and output can alias the same system
 * buffer, so every input field needed after the output is written is copied
 * first. Every length and identifier is validated before use.
 */
#include "driver.h"
#include "transport.h"

static
VOID
Ssap3CompleteRequest(
    _In_ WDFREQUEST Request,
    _In_ NTSTATUS Status,
    _In_ size_t Information
    )
{
    WdfRequestCompleteWithInformation(Request, Status, Information);
}

static
NTSTATUS
Ssap3HandleGetBridgeStatus(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    ssap3_driver_status* output;
    NTSTATUS status;

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ssap3_driver_status), (PVOID*)&output, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(output, sizeof(*output));
    output->struct_size = sizeof(*output);
    output->protocol_version = SSAP3_DRIVER_PROTOCOL_VERSION;
    output->capability_flags = deviceContext->CapabilityFlags;
    output->max_packet_size = deviceContext->MaxPacketSize;
    output->last_error = deviceContext->LastError;
    output->initialization_flags = deviceContext->InitializationFlags;
    output->initialization_result =
        ((ssap3_u64)deviceContext->InitializationFailureStage << 32) |
        (ssap3_u32)deviceContext->InitializationStatus;
    output->remote_address = deviceContext->HasRemoteDeviceInfo
        ? (ssap3_u64)deviceContext->RemoteDeviceInfo.address
        : 0;

    Ssap3CompleteRequest(Request, STATUS_SUCCESS, sizeof(*output));
    return STATUS_SUCCESS;
}

static
NTSTATUS
Ssap3HandleOpenSession(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    ssap3_driver_open_session_request* input;
    ssap3_driver_open_session_response* output;
    PSSAP3_SESSION_ENTRY session;
    PSSAP3_FILE_CONTEXT fileContext;
    WDFFILEOBJECT ownerFile;
    CHAR requestedAddress[SSAP3_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH];
    NTSTATUS status;

    deviceContext = Ssap3GetDeviceContext(Device);
    status = WdfRequestRetrieveInputBuffer(Request, sizeof(ssap3_driver_open_session_request), (PVOID*)&input, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (input->struct_size < sizeof(*input) ||
        input->bluetooth_address[0] == '\0' ||
        input->bluetooth_address[sizeof(input->bluetooth_address) - 1] != '\0') {
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    /* Copy before the (possibly aliased) output buffer is written. */
    RtlCopyMemory(requestedAddress, input->bluetooth_address, sizeof(requestedAddress));

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ssap3_driver_open_session_response), (PVOID*)&output, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    fileContext = Ssap3GetFileContext(ownerFile);

    session = (PSSAP3_SESSION_ENTRY)ExAllocatePoolZero(NonPagedPoolNx, sizeof(SSAP3_SESSION_ENTRY), SSAP3_SESSION_POOL_TAG);
    if (session == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = RtlStringCbCopyA(session->BluetoothAddress, sizeof(session->BluetoothAddress), requestedAddress);
    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(session, SSAP3_SESSION_POOL_TAG);
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    session->OwnerFile = ownerFile;

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    if (fileContext->CleanupStarted) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        ExFreePoolWithTag(session, SSAP3_SESSION_POOL_TAG);
        return STATUS_FILE_CLOSED;
    }

    /* S-05: bounded sessions per handle and per device, and bounded parked
     * (zombie) sessions, so a Bluetooth stack that ignores cancels cannot make
     * repeated open/close cycles exhaust non-paged pool. */
    if (fileContext->SessionCount >= SSAP3_DRIVER_MAX_SESSIONS_PER_HANDLE ||
        deviceContext->SessionCount >= SSAP3_DRIVER_MAX_SESSIONS_PER_DEVICE ||
        deviceContext->ZombieCount >= SSAP3_MAX_ZOMBIE_SESSIONS) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        ExFreePoolWithTag(session, SSAP3_SESSION_POOL_TAG);
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_SESSION_LIMIT);
        return STATUS_QUOTA_EXCEEDED;
    }

    session->SessionId = deviceContext->NextSessionId++;
    status = Ssap3TransportOpenSession(Device, session, &session->CapabilityFlags, &session->Mtu, &deviceContext->LastError);
    if (NT_SUCCESS(status)) {
        InsertTailList(&deviceContext->Sessions, &session->ListEntry);
        deviceContext->SessionCount++;
        fileContext->SessionCount++;
        RtlZeroMemory(output, sizeof(*output));
        output->struct_size = sizeof(*output);
        output->capability_flags = session->CapabilityFlags;
        output->mtu = session->Mtu;
        output->session_id = session->SessionId;
    }
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (!NT_SUCCESS(status)) {
        /* A failed open never left a read in flight (see Ssap3StartContinuousReader). */
        ExFreePoolWithTag(session, SSAP3_SESSION_POOL_TAG);
        return status;
    }

    Ssap3CompleteRequest(Request, STATUS_SUCCESS, sizeof(*output));
    return STATUS_SUCCESS;
}

static
NTSTATUS
Ssap3HandleCloseSession(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    ssap3_driver_close_session_request* input;
    PSSAP3_SESSION_ENTRY session;
    WDFFILEOBJECT ownerFile;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(ssap3_driver_close_session_request), (PVOID*)&input, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (input->struct_size < sizeof(*input) || input->session_id == 0) {
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    deviceContext = Ssap3GetDeviceContext(Device);
    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    session = Ssap3FindSessionLocked(deviceContext, input->session_id);
    if (session == NULL || session->OwnerFile != ownerFile) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_NOT_FOUND;
    }

    RemoveEntryList(&session->ListEntry);
    /* The session is destroyed and its slot freed even when the close-channel
     * request fails (that error stays in LastError), so report success: a
     * client must not believe the session is still open and retry. */
    (VOID)Ssap3DestroySessionLocked(Device, session);
    WdfWaitLockRelease(deviceContext->SessionLock);

    Ssap3CompleteRequest(Request, STATUS_SUCCESS, 0);
    return STATUS_SUCCESS;
}

static
NTSTATUS
Ssap3HandleSendPacket(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    ssap3_driver_send_packet_request* input;
    PSSAP3_SESSION_ENTRY session;
    WDFFILEOBJECT ownerFile;
    size_t inputLength;
    size_t requiredLength;
    ULONG packetLength;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(
        Request,
        FIELD_OFFSET(ssap3_driver_send_packet_request, packet),
        (PVOID*)&input,
        &inputLength);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    packetLength = input->packet_length;
    /* FIELD_OFFSET is a signed LONG; compare as unsigned (C4018). */
    if (input->struct_size < (ULONG)FIELD_OFFSET(ssap3_driver_send_packet_request, packet) ||
        input->session_id == 0 ||
        packetLength == 0 ||
        packetLength > SSAP3_DRIVER_MAX_OUTBOUND_PACKET_LENGTH) {
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    requiredLength = (size_t)FIELD_OFFSET(ssap3_driver_send_packet_request, packet) + (size_t)packetLength;
    if (inputLength < requiredLength) {
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_BUFFER_TOO_SMALL;
    }

    /* S-09: only the AACP framing SSAP3 itself sends is forwarded. */
    if (!Ssap3IsAllowedOutboundPacket(input->packet, packetLength)) {
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_PACKET_REJECTED);
        return STATUS_ACCESS_DENIED;
    }

    deviceContext = Ssap3GetDeviceContext(Device);
    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    session = Ssap3FindSessionLocked(deviceContext, input->session_id);
    if (session == NULL || session->OwnerFile != ownerFile) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_NOT_FOUND;
    }

    status = Ssap3TransportSendPacket(Device, session, input->packet, packetLength, &deviceContext->LastError);
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    /* Normalise informational codes so the dispatcher never completes twice. */
    Ssap3CompleteRequest(Request, STATUS_SUCCESS, 0);
    return STATUS_SUCCESS;
}

static
NTSTATUS
Ssap3HandleReadPacket(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    ssap3_driver_read_packet_request* input;
    ssap3_driver_read_packet_response* output;
    PSSAP3_SESSION_ENTRY session;
    WDFFILEOBJECT ownerFile;
    ssap3_u64 sessionId;
    ULONG packetBufferLength;
    ULONG packetLength;
    ULONGLONG receiveTime;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(ssap3_driver_read_packet_request), (PVOID*)&input, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    /* The buffer must hold the largest inbound packet, so a long packet can
     * never sit at the head of the queue, unreadable by a small buffer. */
    if (input->struct_size < sizeof(*input) ||
        input->session_id == 0 ||
        input->packet_buffer_length != SSAP3_DRIVER_MAX_INBOUND_PACKET_LENGTH) {
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    /* Input and output can alias: keep the fields needed after clearing output. */
    sessionId = input->session_id;
    packetBufferLength = (ULONG)input->packet_buffer_length;

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ssap3_driver_read_packet_response), (PVOID*)&output, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    deviceContext = Ssap3GetDeviceContext(Device);
    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    session = Ssap3FindSessionLocked(deviceContext, sessionId);
    if (session == NULL || session->OwnerFile != ownerFile) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        Ssap3SetLastError(Device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_NOT_FOUND;
    }

    RtlZeroMemory(output, sizeof(*output));
    output->struct_size = sizeof(*output);
    status = Ssap3TransportReadPacket(
        Device,
        session,
        output->packet,
        packetBufferLength,
        &packetLength,
        &receiveTime,
        &deviceContext->LastError);
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    output->packet_length = packetLength;
    output->receive_interrupt_time = receiveTime;
    Ssap3CompleteRequest(
        Request,
        STATUS_SUCCESS,
        (size_t)FIELD_OFFSET(ssap3_driver_read_packet_response, packet) + (size_t)packetLength);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
Ssap3EvtIoDeviceControl(
    WDFQUEUE Queue,
    WDFREQUEST Request,
    size_t OutputBufferLength,
    size_t InputBufferLength,
    ULONG IoControlCode
    )
{
    WDFDEVICE device;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    device = WdfIoQueueGetDevice(Queue);

    switch (IoControlCode) {
        case SSAP3_IOCTL_GET_BRIDGE_STATUS:
            status = Ssap3HandleGetBridgeStatus(device, Request);
            break;

        case SSAP3_IOCTL_OPEN_AACP_CHANNEL:
            status = Ssap3HandleOpenSession(device, Request);
            break;

        case SSAP3_IOCTL_CLOSE_AACP_CHANNEL:
            status = Ssap3HandleCloseSession(device, Request);
            break;

        case SSAP3_IOCTL_SEND_AACP_PACKET:
            status = Ssap3HandleSendPacket(device, Request);
            break;

        case SSAP3_IOCTL_READ_AACP_PACKET:
            status = Ssap3HandleReadPacket(device, Request);
            break;

        default:
            Ssap3SetLastError(device, SSAP3_DRIVER_ERROR_INVALID_ARGUMENT);
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    if (status != STATUS_SUCCESS) {
        Ssap3CompleteRequest(Request, status, 0);
    }
}
