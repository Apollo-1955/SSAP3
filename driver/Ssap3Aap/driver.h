#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <wdmsec.h>
#include <ntstrsafe.h>
#include <bthdef.h>
#include <bthddi.h>
#include <bthioctl.h>

#include "../shared/ssap3_driver_protocol.h"

#define SSAP3_DRIVER_POOL_TAG 'pLbL'
#define SSAP3_SESSION_POOL_TAG 'sLbL'
#define SSAP3_PACKET_QUEUE_DEPTH 32u

#define SSAP3_INIT_FLAG_PROFILE_INTERFACE 0x00000001u
#define SSAP3_INIT_FLAG_REMOTE_DEVICE_INFO 0x00000002u
#define SSAP3_INIT_FLAG_ENUMERATOR_INFO 0x00000004u
#define SSAP3_INIT_FLAG_BRB_INITIALIZER 0x00000008u
#define SSAP3_INIT_FLAG_PSM_FALLBACK 0x00000010u

typedef struct _SSAP3_SESSION_ENTRY {
    LIST_ENTRY ListEntry;
    UINT64 SessionId;
    WDFFILEOBJECT OwnerFile;
    CHAR BluetoothAddress[SSAP3_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH];
    BTH_ADDR RemoteAddress;
    L2CAP_CHANNEL_HANDLE ChannelHandle;
    ULONG CapabilityFlags;
    ULONG Mtu;
    ULONG LastPacketLength;
    UCHAR LastPacket[SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH];
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
    ULONG PacketQueueLengths[SSAP3_PACKET_QUEUE_DEPTH];
    UCHAR PacketQueue[SSAP3_PACKET_QUEUE_DEPTH][SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH];
} SSAP3_SESSION_ENTRY, *PSSAP3_SESSION_ENTRY;

typedef struct _SSAP3_FILE_CONTEXT {
    BOOLEAN CleanupStarted;
} SSAP3_FILE_CONTEXT, *PSSAP3_FILE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SSAP3_FILE_CONTEXT, Ssap3GetFileContext)

typedef struct _SSAP3_DEVICE_CONTEXT {
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
} SSAP3_DEVICE_CONTEXT, *PSSAP3_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SSAP3_DEVICE_CONTEXT, Ssap3GetDeviceContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD Ssap3EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL Ssap3EvtIoDeviceControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP Ssap3EvtDeviceContextCleanup;
EVT_WDF_DEVICE_PREPARE_HARDWARE Ssap3EvtDevicePrepareHardware;
EVT_WDF_DEVICE_RELEASE_HARDWARE Ssap3EvtDeviceReleaseHardware;
EVT_WDF_FILE_CLEANUP Ssap3EvtFileCleanup;

NTSTATUS
Ssap3CreateDevice(
    _Inout_ PWDFDEVICE_INIT DeviceInit
    );

VOID
Ssap3SetLastError(
    _In_ WDFDEVICE Device,
    _In_ ULONG LastError
    );

PSSAP3_SESSION_ENTRY
Ssap3FindSessionLocked(
    _In_ PSSAP3_DEVICE_CONTEXT DeviceContext,
    _In_ UINT64 SessionId
    );
