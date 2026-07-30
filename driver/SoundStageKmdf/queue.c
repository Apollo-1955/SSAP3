#include "driver.h"
#include "transport.h"

static
VOID
LibrepodsCompleteRequest(
    _In_ WDFREQUEST Request,
    _In_ NTSTATUS Status,
    _In_ size_t Information
    )
{
    WdfRequestCompleteWithInformation(Request, Status, Information);
}

static
NTSTATUS
LibrepodsHandleGetBridgeStatus(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PLIBREPODS_DEVICE_CONTEXT deviceContext;
    librepods_driver_status* output;
    NTSTATUS status;

    status = WdfRequestRetrieveOutputBuffer(
        Request,
        sizeof(librepods_driver_status),
        (PVOID*)&output,
        NULL);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    deviceContext = LibrepodsGetDeviceContext(Device);
    RtlZeroMemory(output, sizeof(*output));
    output->struct_size = sizeof(*output);
    output->protocol_version = LIBREPODS_DRIVER_PROTOCOL_VERSION;
    output->capability_flags = deviceContext->CapabilityFlags;
    output->max_packet_size = deviceContext->MaxPacketSize;
    output->last_error = deviceContext->LastError;
    output->reserved0 = deviceContext->InitializationFlags;
    output->reserved1 =
        ((librepods_u64)deviceContext->InitializationFailureStage << 32) |
        (librepods_u32)deviceContext->InitializationStatus;

    LibrepodsCompleteRequest(Request, STATUS_SUCCESS, sizeof(*output));
    return STATUS_SUCCESS;
}

static
NTSTATUS
LibrepodsHandleOpenSession(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PLIBREPODS_DEVICE_CONTEXT deviceContext;
    librepods_driver_open_session_request* input;
    librepods_driver_open_session_response* output;
    PLIBREPODS_SESSION_ENTRY session;
    PLIBREPODS_FILE_CONTEXT fileContext;
    WDFFILEOBJECT ownerFile;
    size_t inputLength;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(
        Request,
        sizeof(librepods_driver_open_session_request),
        (PVOID*)&input,
        &inputLength);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (input->struct_size < sizeof(*input) ||
        input->bluetooth_address[0] == '\0' ||
        input->bluetooth_address[sizeof(input->bluetooth_address) - 1] != '\0') {
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    status = WdfRequestRetrieveOutputBuffer(
        Request,
        sizeof(librepods_driver_open_session_response),
        (PVOID*)&output,
        NULL);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    session = (PLIBREPODS_SESSION_ENTRY)ExAllocatePoolZero(
        NonPagedPoolNx,
        sizeof(LIBREPODS_SESSION_ENTRY),
        LIBREPODS_SESSION_POOL_TAG);

    if (session == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        ExFreePoolWithTag(session, LIBREPODS_SESSION_POOL_TAG);
        return STATUS_FILE_CLOSED;
    }

    status = RtlStringCbCopyA(
        session->BluetoothAddress,
        sizeof(session->BluetoothAddress),
        input->bluetooth_address);
    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(session, LIBREPODS_SESSION_POOL_TAG);
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    deviceContext = LibrepodsGetDeviceContext(Device);
    fileContext = LibrepodsGetFileContext(ownerFile);
    session->OwnerFile = ownerFile;

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    if (fileContext->CleanupStarted) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        ExFreePoolWithTag(session, LIBREPODS_SESSION_POOL_TAG);
        return STATUS_FILE_CLOSED;
    }

    session->SessionId = deviceContext->NextSessionId++;
    status = LibrepodsTransportOpenSession(
        Device,
        session,
        &session->CapabilityFlags,
        &session->Mtu,
        &deviceContext->LastError);

    if (NT_SUCCESS(status)) {
        InsertTailList(&deviceContext->Sessions, &session->ListEntry);
        RtlZeroMemory(output, sizeof(*output));
        output->struct_size = sizeof(*output);
        output->capability_flags = session->CapabilityFlags;
        output->mtu = session->Mtu;
        output->session_id = session->SessionId;
    }
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (!NT_SUCCESS(status)) {
        ExFreePoolWithTag(session, LIBREPODS_SESSION_POOL_TAG);
        return status;
    }

    LibrepodsCompleteRequest(Request, STATUS_SUCCESS, sizeof(*output));
    return STATUS_SUCCESS;
}

static
NTSTATUS
LibrepodsHandleCloseSession(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PLIBREPODS_DEVICE_CONTEXT deviceContext;
    librepods_driver_close_session_request* input;
    PLIBREPODS_SESSION_ENTRY session;
    WDFFILEOBJECT ownerFile;
    BOOLEAN foundSession;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(
        Request,
        sizeof(librepods_driver_close_session_request),
        (PVOID*)&input,
        NULL);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (input->struct_size < sizeof(*input) || input->session_id == 0) {
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    deviceContext = LibrepodsGetDeviceContext(Device);
    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    foundSession = FALSE;
    session = LibrepodsFindSessionLocked(deviceContext, input->session_id);
    if (session != NULL && session->OwnerFile == ownerFile) {
        foundSession = TRUE;
        RemoveEntryList(&session->ListEntry);
        status = LibrepodsTransportCloseSession(Device, session, &deviceContext->LastError);
        ExFreePoolWithTag(session, LIBREPODS_SESSION_POOL_TAG);
    } else {
        status = STATUS_NOT_FOUND;
    }
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (!foundSession) {
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_NOT_FOUND;
    }

    if (!NT_SUCCESS(status)) {
        return status;
    }

    LibrepodsCompleteRequest(Request, STATUS_SUCCESS, 0);
    return STATUS_SUCCESS;
}

static
NTSTATUS
LibrepodsHandleSendPacket(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PLIBREPODS_DEVICE_CONTEXT deviceContext;
    librepods_driver_send_packet_request* input;
    PLIBREPODS_SESSION_ENTRY session;
    WDFFILEOBJECT ownerFile;
    size_t inputLength;
    size_t requiredLength;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(
        Request,
        FIELD_OFFSET(librepods_driver_send_packet_request, packet),
        (PVOID*)&input,
        &inputLength);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (input->struct_size < sizeof(*input) ||
        input->session_id == 0 ||
        input->packet_length == 0 ||
        input->packet_length > LIBREPODS_DRIVER_MAX_AACP_PACKET_LENGTH) {
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    requiredLength = FIELD_OFFSET(librepods_driver_send_packet_request, packet) + input->packet_length;
    if (inputLength < requiredLength) {
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_BUFFER_TOO_SMALL;
    }

    deviceContext = LibrepodsGetDeviceContext(Device);
    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    session = LibrepodsFindSessionLocked(deviceContext, input->session_id);
    if (session == NULL || session->OwnerFile != ownerFile) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_NOT_FOUND;
    }

    status = LibrepodsTransportSendPacket(
        Device,
        session,
        input->packet,
        (ULONG)input->packet_length,
        &deviceContext->LastError);
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (NT_SUCCESS(status)) {
        LibrepodsCompleteRequest(Request, STATUS_SUCCESS, 0);
    }

    return status;
}

static
NTSTATUS
LibrepodsHandleReadPacket(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    PLIBREPODS_DEVICE_CONTEXT deviceContext;
    librepods_driver_read_packet_request* input;
    librepods_driver_read_packet_response* output;
    PLIBREPODS_SESSION_ENTRY session;
    WDFFILEOBJECT ownerFile;
    librepods_u64 sessionId;
    ULONG packetBufferLength;
    ULONG packetLength;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(
        Request,
        sizeof(librepods_driver_read_packet_request),
        (PVOID*)&input,
        NULL);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (input->struct_size < sizeof(*input) ||
        input->session_id == 0 ||
        input->packet_buffer_length == 0 ||
        input->packet_buffer_length > LIBREPODS_DRIVER_MAX_AACP_PACKET_LENGTH) {
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_INVALID_PARAMETER;
    }

    /*
     * These IOCTLs use METHOD_BUFFERED, so the input and output pointers can
     * alias the same system buffer. Preserve every input field needed after
     * the output buffer is cleared.
     */
    sessionId = input->session_id;
    packetBufferLength = (ULONG)input->packet_buffer_length;

    status = WdfRequestRetrieveOutputBuffer(
        Request,
        sizeof(librepods_driver_read_packet_response),
        (PVOID*)&output,
        NULL);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    deviceContext = LibrepodsGetDeviceContext(Device);
    ownerFile = WdfRequestGetFileObject(Request);
    if (ownerFile == NULL) {
        return STATUS_FILE_CLOSED;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    session = LibrepodsFindSessionLocked(deviceContext, sessionId);
    if (session == NULL || session->OwnerFile != ownerFile) {
        WdfWaitLockRelease(deviceContext->SessionLock);
        LibrepodsSetLastError(Device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
        return STATUS_NOT_FOUND;
    }

    RtlZeroMemory(output, sizeof(*output));
    output->struct_size = sizeof(*output);

    status = LibrepodsTransportReadPacket(
        Device,
        session,
        output->packet,
        packetBufferLength,
        &packetLength,
        &deviceContext->LastError);
    WdfWaitLockRelease(deviceContext->SessionLock);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    output->packet_length = packetLength;
    LibrepodsCompleteRequest(
        Request,
        STATUS_SUCCESS,
        FIELD_OFFSET(librepods_driver_read_packet_response, packet) + packetLength);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
LibrepodsEvtIoDeviceControl(
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
    status = STATUS_INVALID_DEVICE_REQUEST;

    switch (IoControlCode) {
        case LIBREPODS_IOCTL_GET_BRIDGE_STATUS:
            status = LibrepodsHandleGetBridgeStatus(device, Request);
            break;

        case LIBREPODS_IOCTL_OPEN_AACP_CHANNEL:
            status = LibrepodsHandleOpenSession(device, Request);
            break;

        case LIBREPODS_IOCTL_CLOSE_AACP_CHANNEL:
            status = LibrepodsHandleCloseSession(device, Request);
            break;

        case LIBREPODS_IOCTL_SEND_AACP_PACKET:
            status = LibrepodsHandleSendPacket(device, Request);
            break;

        case LIBREPODS_IOCTL_READ_AACP_PACKET:
            status = LibrepodsHandleReadPacket(device, Request);
            break;

        default:
            LibrepodsSetLastError(device, LIBREPODS_DRIVER_ERROR_INVALID_ARGUMENT);
            status = STATUS_INVALID_DEVICE_REQUEST;
            break;
    }

    if (status != STATUS_SUCCESS) {
        LibrepodsCompleteRequest(Request, status, 0);
    }
}
