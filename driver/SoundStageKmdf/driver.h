#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <wdmsec.h>
#include <ntstrsafe.h>
#include <bthdef.h>
#include <bthddi.h>
#include <bthioctl.h>

#include "../shared/librepods_driver_protocol.h"

#define LIBREPODS_DRIVER_POOL_TAG 'pLbL'
#define LIBREPODS_SESSION_POOL_TAG 'sLbL'
#define LIBREPODS_PACKET_QUEUE_DEPTH 32u

#define LIBREPODS_INIT_FLAG_PROFILE_INTERFACE 0x00000001u
#define LIBREPODS_INIT_FLAG_REMOTE_DEVICE_INFO 0x00000002u
#define LIBREPODS_INIT_FLAG_ENUMERATOR_INFO 0x00000004u
#define LIBREPODS_INIT_FLAG_BRB_INITIALIZER 0x00000008u
#define LIBREPODS_INIT_FLAG_PSM_FALLBACK 0x00000010u

typedef struct _LIBREPODS_SESSION_ENTRY {
    LIST_ENTRY ListEntry;
    UINT64 SessionId;
    WDFFILEOBJECT OwnerFile;
    CHAR BluetoothAddress[LIBREPODS_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH];
    BTH_ADDR RemoteAddress;
    L2CAP_CHANNEL_HANDLE ChannelHandle;
    ULONG CapabilityFlags;
    ULONG Mtu;
    ULONG LastPacketLength;
    UCHAR LastPacket[LIBREPODS_DRIVER_MAX_AACP_PACKET_LENGTH];
    WDFREQUEST ReaderRequest;
    WDFMEMORY ReaderBufferMemory;
    WDFMEMORY ReaderBrbMemory;
    BRB ReaderBrb;
    KDPC ReaderResubmitDpc;
    KEVENT ReaderStoppedEvent;
    volatile LONG ReaderStopping;
    volatile LONG ReaderStarted;
    NTSTATUS ReaderStatus;
    KSPIN_LOCK PacketLock;
    ULONG PacketQueueHead;
    ULONG PacketQueueTail;
    ULONG PacketQueueCount;
    ULONG PacketQueueLengths[LIBREPODS_PACKET_QUEUE_DEPTH];
    UCHAR PacketQueue[LIBREPODS_PACKET_QUEUE_DEPTH][LIBREPODS_DRIVER_MAX_AACP_PACKET_LENGTH];
} LIBREPODS_SESSION_ENTRY, *PLIBREPODS_SESSION_ENTRY;

typedef struct _LIBREPODS_FILE_CONTEXT {
    BOOLEAN CleanupStarted;
} LIBREPODS_FILE_CONTEXT, *PLIBREPODS_FILE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(LIBREPODS_FILE_CONTEXT, LibrepodsGetFileContext)

typedef struct _LIBREPODS_DEVICE_CONTEXT {
    WDFWAITLOCK SessionLock;
    LIST_ENTRY Sessions;
    UINT64 NextSessionId;
    BOOLEAN HasProfileDriverInterface;
    BOOLEAN HasRemoteDeviceInfo;
    BOOLEAN HasAacpEnumeratorInfo;
    BTH_PROFILE_DRIVER_INTERFACE ProfileDriverInterface;
    BTH_DEVICE_INFO RemoteDeviceInfo;
    ULONG AacpPort;
    ULONG CapabilityFlags;
    ULONG LastError;
    ULONG MaxPacketSize;
    ULONG InitializationFlags;
    ULONG InitializationFailureStage;
    NTSTATUS InitializationStatus;
} LIBREPODS_DEVICE_CONTEXT, *PLIBREPODS_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(LIBREPODS_DEVICE_CONTEXT, LibrepodsGetDeviceContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD LibrepodsEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL LibrepodsEvtIoDeviceControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP LibrepodsEvtDeviceContextCleanup;
EVT_WDF_DEVICE_PREPARE_HARDWARE LibrepodsEvtDevicePrepareHardware;
EVT_WDF_DEVICE_RELEASE_HARDWARE LibrepodsEvtDeviceReleaseHardware;
EVT_WDF_FILE_CLEANUP LibrepodsEvtFileCleanup;

NTSTATUS
LibrepodsCreateDevice(
    _Inout_ PWDFDEVICE_INIT DeviceInit
    );

VOID
LibrepodsSetLastError(
    _In_ WDFDEVICE Device,
    _In_ ULONG LastError
    );

PLIBREPODS_SESSION_ENTRY
LibrepodsFindSessionLocked(
    _In_ PLIBREPODS_DEVICE_CONTEXT DeviceContext,
    _In_ UINT64 SessionId
    );
