#include "driver.h"
#include "transport.h"

static const GUID GUID_DEVINTERFACE_SSAP3_DRIVER = {
    0x3e98d13a, 0xb2a5, 0x4c3d, {0xae, 0x1e, 0x8d, 0xb4, 0xf3, 0xd8, 0xc1, 0xf0}
};

static const GUID GUID_BTHDDI_PROFILE_DRIVER_INTERFACE = {
    0x94a59aa8, 0x4383, 0x4286, {0xaa, 0x4f, 0x34, 0xa1, 0x60, 0xf4, 0x00, 0x04}
};

static const GUID GUID_SSAP3_AAP_SERVER = {
    0x74ec2172, 0x0bad, 0x4d01, {0x8f, 0x77, 0x99, 0x7b, 0x2b, 0xe0, 0x72, 0x2a}
};

#define SSAP3_AACP_PSM ((ULONG)0x1001)

static
VOID
Ssap3UseKnownAacpPsm(
    _Inout_ PSSAP3_DEVICE_CONTEXT DeviceContext,
    _In_ NTSTATUS QueryStatus
    )
{
    DeviceContext->AacpPort = SSAP3_AACP_PSM;
    DeviceContext->HasAacpEnumeratorInfo = TRUE;
    DeviceContext->InitializationFlags |= SSAP3_INIT_FLAG_PSM_FALLBACK;

    if (DeviceContext->InitializationFailureStage == 0) {
        DeviceContext->InitializationFailureStage = 3;
        DeviceContext->InitializationStatus = QueryStatus;
    }
}

static
NTSTATUS
Ssap3QueryRemoteDeviceInfo(
    _In_ WDFDEVICE Device
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    WDF_MEMORY_DESCRIPTOR outputDescriptor;
    BTH_DEVICE_INFO remoteDeviceInfo;
    NTSTATUS status;

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(&remoteDeviceInfo, sizeof(remoteDeviceInfo));
    deviceContext->HasRemoteDeviceInfo = FALSE;
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));

    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(
        &outputDescriptor,
        &remoteDeviceInfo,
        sizeof(remoteDeviceInfo));

    status = WdfIoTargetSendInternalIoctlSynchronously(
        WdfDeviceGetIoTarget(Device),
        WDF_NO_HANDLE,
        IOCTL_INTERNAL_BTHENUM_GET_DEVINFO,
        NULL,
        &outputDescriptor,
        NULL,
        NULL);

    if (NT_SUCCESS(status)) {
        deviceContext->RemoteDeviceInfo = remoteDeviceInfo;
        deviceContext->HasRemoteDeviceInfo = TRUE;
    }

    return status;
}

static
NTSTATUS
Ssap3QueryAacpEnumeratorInfo(
    _In_ WDFDEVICE Device
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    WDF_MEMORY_DESCRIPTOR outputDescriptor;
    BTH_ENUMERATOR_INFO enumeratorInfo;
    NTSTATUS status;

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(&enumeratorInfo, sizeof(enumeratorInfo));
    deviceContext->HasAacpEnumeratorInfo = FALSE;
    deviceContext->AacpPort = 0;

    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(
        &outputDescriptor,
        &enumeratorInfo,
        sizeof(enumeratorInfo));

    status = WdfIoTargetSendInternalIoctlSynchronously(
        WdfDeviceGetIoTarget(Device),
        WDF_NO_HANDLE,
        IOCTL_INTERNAL_BTHENUM_GET_ENUMINFO,
        NULL,
        &outputDescriptor,
        NULL,
        NULL);

    if (!NT_SUCCESS(status)) {
        Ssap3UseKnownAacpPsm(deviceContext, status);
        return STATUS_SUCCESS;
    }

    if (RtlCompareMemory(
            &enumeratorInfo.Guid,
            &GUID_SSAP3_AAP_SERVER,
            sizeof(GUID)) != sizeof(GUID)) {
        Ssap3UseKnownAacpPsm(deviceContext, STATUS_NOT_SUPPORTED);
        return STATUS_SUCCESS;
    }

    if (enumeratorInfo.Port == 0 || enumeratorInfo.Port > 0xffffu) {
        Ssap3UseKnownAacpPsm(deviceContext, STATUS_DEVICE_DATA_ERROR);
        return STATUS_SUCCESS;
    }

    deviceContext->AacpPort = enumeratorInfo.Port;
    deviceContext->HasAacpEnumeratorInfo = TRUE;
    return STATUS_SUCCESS;
}

static
VOID
Ssap3CloseAllSessions(
    _In_ WDFDEVICE Device
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;

    deviceContext = Ssap3GetDeviceContext(Device);

    if (deviceContext->SessionLock == NULL) {
        return;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    while (!IsListEmpty(&deviceContext->Sessions)) {
        PSSAP3_SESSION_ENTRY session;
        ULONG driverError;
        PLIST_ENTRY listEntry;

        listEntry = RemoveHeadList(&deviceContext->Sessions);
        session = CONTAINING_RECORD(listEntry, SSAP3_SESSION_ENTRY, ListEntry);
        driverError = SSAP3_DRIVER_ERROR_NONE;
        Ssap3TransportCloseSession(Device, session, &driverError);
        ExFreePoolWithTag(session, SSAP3_SESSION_POOL_TAG);
    }
    WdfWaitLockRelease(deviceContext->SessionLock);
}

_Use_decl_annotations_
NTSTATUS
DriverEntry(
    PDRIVER_OBJECT DriverObject,
    PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    NTSTATUS status;

    WDF_DRIVER_CONFIG_INIT(&config, Ssap3EvtDeviceAdd);
    config.DriverPoolTag = SSAP3_DRIVER_POOL_TAG;

    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);

    return status;
}

_Use_decl_annotations_
NTSTATUS
Ssap3EvtDeviceAdd(
    WDFDRIVER Driver,
    PWDFDEVICE_INIT DeviceInit
    )
{
    UNREFERENCED_PARAMETER(Driver);
    return Ssap3CreateDevice(DeviceInit);
}

_Use_decl_annotations_
NTSTATUS
Ssap3CreateDevice(
    PWDFDEVICE_INIT DeviceInit
    )
{
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_OBJECT_ATTRIBUTES fileAttributes;
    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;
    WDFDEVICE device;
    PSSAP3_DEVICE_CONTEXT deviceContext;
    UNICODE_STRING securityDescriptor;
    NTSTATUS status;

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetExclusive(DeviceInit, FALSE);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);
    WdfDeviceInitSetCharacteristics(DeviceInit, FILE_AUTOGENERATED_DEVICE_NAME, FALSE);

    WDF_FILEOBJECT_CONFIG_INIT(
        &fileConfig,
        WDF_NO_EVENT_CALLBACK,
        WDF_NO_EVENT_CALLBACK,
        Ssap3EvtFileCleanup);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&fileAttributes, SSAP3_FILE_CONTEXT);
    fileAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    fileAttributes.SynchronizationScope = WdfSynchronizationScopeNone;
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, &fileAttributes);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDevicePrepareHardware = Ssap3EvtDevicePrepareHardware;
    pnpPowerCallbacks.EvtDeviceReleaseHardware = Ssap3EvtDeviceReleaseHardware;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpPowerCallbacks);

    RtlInitUnicodeString(&securityDescriptor, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;BU)");
    status = WdfDeviceInitAssignSDDLString(DeviceInit, &securityDescriptor);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, SSAP3_DEVICE_CONTEXT);
    deviceAttributes.EvtCleanupCallback = Ssap3EvtDeviceContextCleanup;
    deviceAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    deviceContext = Ssap3GetDeviceContext(device);
    RtlZeroMemory(&deviceContext->ProfileDriverInterface, sizeof(deviceContext->ProfileDriverInterface));
    deviceContext->HasProfileDriverInterface = FALSE;
    deviceContext->HasRemoteDeviceInfo = FALSE;
    deviceContext->HasAacpEnumeratorInfo = FALSE;
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));
    deviceContext->AacpPort = 0;
    InitializeListHead(&deviceContext->Sessions);
    deviceContext->NextSessionId = 1;
    deviceContext->MaxPacketSize = SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH;
    deviceContext->InitializationFlags = 0;
    deviceContext->InitializationFailureStage = 0;
    deviceContext->InitializationStatus = STATUS_SUCCESS;
    status = Ssap3TransportQueryStatus(device, &deviceContext->CapabilityFlags, &deviceContext->LastError);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = WdfDeviceGetDriver(device);

    status = WdfWaitLockCreate(&lockAttributes, &deviceContext->SessionLock);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_SSAP3_DRIVER, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = Ssap3EvtIoDeviceControl;

    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    return status;
}

_Use_decl_annotations_
VOID
Ssap3EvtFileCleanup(
    WDFFILEOBJECT FileObject
    )
{
    WDFDEVICE device;
    PSSAP3_DEVICE_CONTEXT deviceContext;
    PSSAP3_FILE_CONTEXT fileContext;
    PLIST_ENTRY entry;

    device = WdfFileObjectGetDevice(FileObject);
    deviceContext = Ssap3GetDeviceContext(device);
    fileContext = Ssap3GetFileContext(FileObject);

    if (deviceContext->SessionLock == NULL) {
        fileContext->CleanupStarted = TRUE;
        return;
    }

    WdfWaitLockAcquire(deviceContext->SessionLock, NULL);
    fileContext->CleanupStarted = TRUE;

    entry = deviceContext->Sessions.Flink;
    while (entry != &deviceContext->Sessions) {
        PSSAP3_SESSION_ENTRY session;
        PLIST_ENTRY nextEntry;
        ULONG driverError;

        nextEntry = entry->Flink;
        session = CONTAINING_RECORD(entry, SSAP3_SESSION_ENTRY, ListEntry);

        if (session->OwnerFile == FileObject) {
            RemoveEntryList(entry);
            driverError = SSAP3_DRIVER_ERROR_NONE;
            Ssap3TransportCloseSession(device, session, &driverError);
            ExFreePoolWithTag(session, SSAP3_SESSION_POOL_TAG);
        }

        entry = nextEntry;
    }

    WdfWaitLockRelease(deviceContext->SessionLock);
}

_Use_decl_annotations_
NTSTATUS
Ssap3EvtDevicePrepareHardware(
    WDFDEVICE Device,
    WDFCMRESLIST ResourcesRaw,
    WDFCMRESLIST ResourcesTranslated
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    NTSTATUS enumeratorInfoStatus;
    NTSTATUS profileInterfaceStatus;
    NTSTATUS remoteDeviceInfoStatus;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(&deviceContext->ProfileDriverInterface, sizeof(deviceContext->ProfileDriverInterface));
    deviceContext->HasProfileDriverInterface = FALSE;
    deviceContext->HasRemoteDeviceInfo = FALSE;
    deviceContext->HasAacpEnumeratorInfo = FALSE;
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));
    deviceContext->AacpPort = 0;
    deviceContext->InitializationFlags = 0;
    deviceContext->InitializationFailureStage = 0;
    deviceContext->InitializationStatus = STATUS_SUCCESS;

    profileInterfaceStatus = WdfFdoQueryForInterface(
        Device,
        &GUID_BTHDDI_PROFILE_DRIVER_INTERFACE,
        (PINTERFACE)&deviceContext->ProfileDriverInterface,
        sizeof(deviceContext->ProfileDriverInterface),
        BTHDDI_PROFILE_DRIVER_INTERFACE_VERSION_FOR_QI,
        NULL);

    if (NT_SUCCESS(profileInterfaceStatus)) {
        deviceContext->HasProfileDriverInterface = TRUE;
        deviceContext->InitializationFlags |= SSAP3_INIT_FLAG_PROFILE_INTERFACE;
        if (deviceContext->ProfileDriverInterface.BthInitializeBrb != NULL) {
            deviceContext->InitializationFlags |= SSAP3_INIT_FLAG_BRB_INITIALIZER;
        } else {
            deviceContext->InitializationFailureStage = 4;
            deviceContext->InitializationStatus = STATUS_NOINTERFACE;
        }
    } else {
        deviceContext->InitializationFailureStage = 1;
        deviceContext->InitializationStatus = profileInterfaceStatus;
    }

    remoteDeviceInfoStatus = Ssap3QueryRemoteDeviceInfo(Device);
    if (NT_SUCCESS(remoteDeviceInfoStatus)) {
        deviceContext->InitializationFlags |= SSAP3_INIT_FLAG_REMOTE_DEVICE_INFO;
    } else if (deviceContext->InitializationFailureStage == 0) {
        deviceContext->InitializationFailureStage = 2;
        deviceContext->InitializationStatus = remoteDeviceInfoStatus;
    }

    enumeratorInfoStatus = Ssap3QueryAacpEnumeratorInfo(Device);
    if (NT_SUCCESS(enumeratorInfoStatus)) {
        deviceContext->InitializationFlags |= SSAP3_INIT_FLAG_ENUMERATOR_INFO;
    } else if (deviceContext->InitializationFailureStage == 0) {
        deviceContext->InitializationFailureStage = 3;
        deviceContext->InitializationStatus = enumeratorInfoStatus;
    }

    status = Ssap3TransportQueryStatus(Device, &deviceContext->CapabilityFlags, &deviceContext->LastError);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS
Ssap3EvtDeviceReleaseHardware(
    WDFDEVICE Device,
    WDFCMRESLIST ResourcesTranslated
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;

    UNREFERENCED_PARAMETER(ResourcesTranslated);

    deviceContext = Ssap3GetDeviceContext(Device);
    Ssap3CloseAllSessions(Device);

    if (deviceContext->HasProfileDriverInterface &&
        deviceContext->ProfileDriverInterface.Interface.InterfaceDereference != NULL) {
        deviceContext->ProfileDriverInterface.Interface.InterfaceDereference(
            deviceContext->ProfileDriverInterface.Interface.Context);
    }

    RtlZeroMemory(&deviceContext->ProfileDriverInterface, sizeof(deviceContext->ProfileDriverInterface));
    deviceContext->HasProfileDriverInterface = FALSE;
    deviceContext->HasRemoteDeviceInfo = FALSE;
    deviceContext->HasAacpEnumeratorInfo = FALSE;
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));
    deviceContext->AacpPort = 0;
    deviceContext->InitializationFlags = 0;
    deviceContext->InitializationFailureStage = 0;
    deviceContext->InitializationStatus = STATUS_SUCCESS;
    (VOID)Ssap3TransportQueryStatus(Device, &deviceContext->CapabilityFlags, &deviceContext->LastError);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
Ssap3EvtDeviceContextCleanup(
    WDFOBJECT DeviceObject
    )
{
    WDFDEVICE device;
    PSSAP3_DEVICE_CONTEXT deviceContext;

    device = (WDFDEVICE)DeviceObject;
    deviceContext = Ssap3GetDeviceContext(device);
    Ssap3CloseAllSessions(device);

    if (deviceContext->SessionLock != NULL) {
        WDFWAITLOCK sessionLock;

        sessionLock = deviceContext->SessionLock;
        deviceContext->SessionLock = NULL;
        WdfObjectDelete(sessionLock);
    }
}

_Use_decl_annotations_
VOID
Ssap3SetLastError(
    WDFDEVICE Device,
    ULONG LastError
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;

    deviceContext = Ssap3GetDeviceContext(Device);
    deviceContext->LastError = LastError;
}

_Use_decl_annotations_
PSSAP3_SESSION_ENTRY
Ssap3FindSessionLocked(
    PSSAP3_DEVICE_CONTEXT DeviceContext,
    UINT64 SessionId
    )
{
    PLIST_ENTRY entry;

    entry = DeviceContext->Sessions.Flink;
    while (entry != &DeviceContext->Sessions) {
        PSSAP3_SESSION_ENTRY session;

        session = CONTAINING_RECORD(entry, SSAP3_SESSION_ENTRY, ListEntry);
        if (session->SessionId == SessionId) {
            return session;
        }

        entry = entry->Flink;
    }

    return NULL;
}
