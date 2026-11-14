/*
 * SSAP3 AAP profile driver: device setup, PnP, and session lifetime.
 * Adapted from the GPL-3.0 LibrePods Windows KMDF bridge; hardened for SSAP3.
 */
#include "driver.h"
#include "transport.h"

/* {1C3A8128-3668-4428-95A4-B1CAD51B7249}: SSAP3-specific interface (protocol v2). */
static const GUID GUID_DEVINTERFACE_SSAP3_DRIVER = {
    0x1c3a8128, 0x3668, 0x4428, {0x95, 0xa4, 0xb1, 0xca, 0xd5, 0x1b, 0x72, 0x49}
};

static const GUID GUID_BTHDDI_PROFILE_DRIVER_INTERFACE = {
    0x94a59aa8, 0x4383, 0x4286, {0xaa, 0x4f, 0x34, 0xa1, 0x60, 0xf4, 0x00, 0x04}
};

static const GUID GUID_SSAP3_AAP_SERVER = {
    0x74ec2172, 0x0bad, 0x4d01, {0x8f, 0x77, 0x99, 0x7b, 0x2b, 0xe0, 0x72, 0x2a}
};

#define SSAP3_AACP_PSM ((ULONG)0x1001)
#define SSAP3_QUERY_TIMEOUT_MS 5000

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
Ssap3SendQuerySynchronously(
    _In_ WDFDEVICE Device,
    _In_ ULONG IoControlCode,
    _Out_writes_bytes_(OutputLength) PVOID Output,
    _In_ ULONG OutputLength
    )
{
    WDF_MEMORY_DESCRIPTOR outputDescriptor;
    WDF_REQUEST_SEND_OPTIONS sendOptions;

    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(&outputDescriptor, Output, OutputLength);
    WDF_REQUEST_SEND_OPTIONS_INIT(&sendOptions, WDF_REQUEST_SEND_OPTION_TIMEOUT);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&sendOptions, WDF_REL_TIMEOUT_IN_MS(SSAP3_QUERY_TIMEOUT_MS));

    return WdfIoTargetSendInternalIoctlSynchronously(
        WdfDeviceGetIoTarget(Device),
        WDF_NO_HANDLE,
        IoControlCode,
        NULL,
        &outputDescriptor,
        &sendOptions,
        NULL);
}

static
NTSTATUS
Ssap3QueryRemoteDeviceInfo(
    _In_ WDFDEVICE Device
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    BTH_DEVICE_INFO remoteDeviceInfo;
    NTSTATUS status;

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(&remoteDeviceInfo, sizeof(remoteDeviceInfo));
    deviceContext->HasRemoteDeviceInfo = FALSE;
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));

    status = Ssap3SendQuerySynchronously(
        Device,
        IOCTL_INTERNAL_BTHENUM_GET_DEVINFO,
        &remoteDeviceInfo,
        sizeof(remoteDeviceInfo));

    if (NT_SUCCESS(status) && remoteDeviceInfo.address != 0) {
        deviceContext->RemoteDeviceInfo = remoteDeviceInfo;
        deviceContext->HasRemoteDeviceInfo = TRUE;
    } else if (NT_SUCCESS(status)) {
        status = STATUS_DEVICE_DATA_ERROR;
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
    BTH_ENUMERATOR_INFO enumeratorInfo;
    NTSTATUS status;

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(&enumeratorInfo, sizeof(enumeratorInfo));
    deviceContext->HasAacpEnumeratorInfo = FALSE;
    deviceContext->AacpPort = 0;

    status = Ssap3SendQuerySynchronously(
        Device,
        IOCTL_INTERNAL_BTHENUM_GET_ENUMINFO,
        &enumeratorInfo,
        sizeof(enumeratorInfo));

    if (!NT_SUCCESS(status)) {
        Ssap3UseKnownAacpPsm(deviceContext, status);
        return STATUS_SUCCESS;
    }

    /* The UARP PDO reports its own service GUID: fall back to the known AACP PSM. */
    if (RtlCompareMemory(&enumeratorInfo.Guid, &GUID_SSAP3_AAP_SERVER, sizeof(GUID)) != sizeof(GUID)) {
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

_Use_decl_annotations_
NTSTATUS
Ssap3DestroySessionLocked(
    WDFDEVICE Device,
    PSSAP3_SESSION_ENTRY Session
    )
{
    PSSAP3_DEVICE_CONTEXT deviceContext;
    BOOLEAN canFree;
    ULONG driverError;
    NTSTATUS status;

    deviceContext = Ssap3GetDeviceContext(Device);
    canFree = FALSE;
    driverError = SSAP3_DRIVER_ERROR_NONE;
    status = Ssap3TransportCloseSession(Device, Session, &canFree, &driverError);
    deviceContext->LastError = driverError;

    if (deviceContext->SessionCount > 0) {
        deviceContext->SessionCount--;
    }

    if (Session->OwnerFile != NULL) {
        PSSAP3_FILE_CONTEXT fileContext;

        fileContext = Ssap3GetFileContext(Session->OwnerFile);
        if (fileContext->SessionCount > 0) {
            fileContext->SessionCount--;
        }
        Session->OwnerFile = NULL;
    }

    if (canFree) {
        ExFreePoolWithTag(Session, SSAP3_SESSION_POOL_TAG);
    } else {
        /* A read might still complete into this memory: park it until device
         * cleanup. Parked sessions are counted so OPEN can cap them (S-05). */
        InsertTailList(&deviceContext->ZombieSessions, &Session->ListEntry);
        deviceContext->ZombieCount++;
    }

    return status;
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
        PLIST_ENTRY listEntry;

        listEntry = RemoveHeadList(&deviceContext->Sessions);
        session = CONTAINING_RECORD(listEntry, SSAP3_SESSION_ENTRY, ListEntry);
        (VOID)Ssap3DestroySessionLocked(Device, session);
    }
    WdfWaitLockRelease(deviceContext->SessionLock);
}

static
VOID
Ssap3FreeZombieSessions(
    _Inout_ PSSAP3_DEVICE_CONTEXT DeviceContext
    )
{
    /* By device cleanup every request parented to the device has completed. */
    KeFlushQueuedDpcs();
    while (!IsListEmpty(&DeviceContext->ZombieSessions)) {
        PLIST_ENTRY listEntry;

        listEntry = RemoveHeadList(&DeviceContext->ZombieSessions);
        ExFreePoolWithTag(
            CONTAINING_RECORD(listEntry, SSAP3_SESSION_ENTRY, ListEntry),
            SSAP3_SESSION_POOL_TAG);
    }
    DeviceContext->ZombieCount = 0;
}

_Use_decl_annotations_
NTSTATUS
DriverEntry(
    PDRIVER_OBJECT DriverObject,
    PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;

    /* Opt in to non-executable pool for every ExAllocatePool2-style allocation. */
    ExInitializeDriverRuntime(DrvRtPoolNxOptIn);

    WDF_DRIVER_CONFIG_INIT(&config, Ssap3EvtDeviceAdd);
    config.DriverPoolTag = SSAP3_DRIVER_POOL_TAG;

    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
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
    /* FILE_DEVICE_SECURE_OPEN: the device ACL also applies to opens of names
     * inside the device namespace, whatever PnP applies from the INF. */
    WdfDeviceInitSetCharacteristics(DeviceInit, FILE_DEVICE_SECURE_OPEN | FILE_AUTOGENERATED_DEVICE_NAME, FALSE);

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, Ssap3EvtFileCleanup);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&fileAttributes, SSAP3_FILE_CONTEXT);
    fileAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    fileAttributes.SynchronizationScope = WdfSynchronizationScopeNone;
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, &fileAttributes);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDevicePrepareHardware = Ssap3EvtDevicePrepareHardware;
    pnpPowerCallbacks.EvtDeviceReleaseHardware = Ssap3EvtDeviceReleaseHardware;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpPowerCallbacks);

    /* SYSTEM and Administrators: all access; interactive users: read/write
     * (the SSAP3 Control Center runs as the signed-in user). Services,
     * network logons and AppContainers get no access. */
    RtlInitUnicodeString(&securityDescriptor, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)");
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
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));
    deviceContext->HasProfileDriverInterface = FALSE;
    deviceContext->HasRemoteDeviceInfo = FALSE;
    deviceContext->HasAacpEnumeratorInfo = FALSE;
    deviceContext->AacpPort = 0;
    InitializeListHead(&deviceContext->Sessions);
    InitializeListHead(&deviceContext->ZombieSessions);
    deviceContext->ZombieCount = 0;
    deviceContext->SessionCount = 0;
    deviceContext->NextSessionId = 1;
    deviceContext->MaxPacketSize = SSAP3_DRIVER_MAX_INBOUND_PACKET_LENGTH;
    deviceContext->InitializationFlags = 0;
    deviceContext->InitializationFailureStage = 0;
    deviceContext->InitializationStatus = STATUS_SUCCESS;
    status = Ssap3TransportQueryStatus(device, &deviceContext->CapabilityFlags, &deviceContext->LastError);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    /* Parented to the driver, not the device: child objects are cleaned up
     * before the device cleanup callback, which still needs the lock. It is
     * deleted explicitly in Ssap3EvtDeviceContextCleanup. */
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

    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
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

        nextEntry = entry->Flink;
        session = CONTAINING_RECORD(entry, SSAP3_SESSION_ENTRY, ListEntry);
        if (session->OwnerFile == FileObject) {
            RemoveEntryList(entry);
            (VOID)Ssap3DestroySessionLocked(device, session);
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

    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);

    deviceContext = Ssap3GetDeviceContext(Device);
    RtlZeroMemory(&deviceContext->ProfileDriverInterface, sizeof(deviceContext->ProfileDriverInterface));
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));
    deviceContext->HasProfileDriverInterface = FALSE;
    deviceContext->HasRemoteDeviceInfo = FALSE;
    deviceContext->HasAacpEnumeratorInfo = FALSE;
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
        if (deviceContext->ProfileDriverInterface.BthInitializeBrb != NULL &&
            deviceContext->ProfileDriverInterface.BthReuseBrb != NULL) {
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

    return Ssap3TransportQueryStatus(Device, &deviceContext->CapabilityFlags, &deviceContext->LastError);
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
    RtlZeroMemory(&deviceContext->RemoteDeviceInfo, sizeof(deviceContext->RemoteDeviceInfo));
    deviceContext->HasProfileDriverInterface = FALSE;
    deviceContext->HasRemoteDeviceInfo = FALSE;
    deviceContext->HasAacpEnumeratorInfo = FALSE;
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
    Ssap3FreeZombieSessions(deviceContext);

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
    Ssap3GetDeviceContext(Device)->LastError = LastError;
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
