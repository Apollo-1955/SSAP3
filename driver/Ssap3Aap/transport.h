#pragma once

#include "driver.h"

NTSTATUS
Ssap3TransportQueryStatus(
    _In_ WDFDEVICE Device,
    _Out_ ULONG* CapabilityFlags,
    _Out_ ULONG* DriverError
    );

NTSTATUS
Ssap3TransportOpenSession(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _Out_ ULONG* CapabilityFlags,
    _Out_ ULONG* Mtu,
    _Out_ ULONG* DriverError
    );

NTSTATUS
Ssap3TransportCloseSession(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _Out_ ULONG* DriverError
    );

NTSTATUS
Ssap3TransportSendPacket(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _In_reads_bytes_(PacketLength) const UCHAR* Packet,
    _In_ ULONG PacketLength,
    _Out_ ULONG* DriverError
    );

NTSTATUS
Ssap3TransportReadPacket(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _Out_writes_bytes_to_(PacketBufferLength, *PacketLength) UCHAR* PacketBuffer,
    _In_ ULONG PacketBufferLength,
    _Out_ ULONG* PacketLength,
    _Out_ ULONG* DriverError
    );
