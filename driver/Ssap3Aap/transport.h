#pragma once

#include "driver.h"

NTSTATUS
Ssap3TransportQueryStatus(
    _In_ WDFDEVICE Device,
    _Out_ ULONG* CapabilityFlags,
    _Out_ ULONG* DriverError
    );

/* Open, close and send block on synchronous BRBs: PASSIVE_LEVEL only. */
_IRQL_requires_(PASSIVE_LEVEL)
NTSTATUS
Ssap3TransportOpenSession(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _Out_ ULONG* CapabilityFlags,
    _Out_ ULONG* Mtu,
    _Out_ ULONG* DriverError
    );

/*
 * Stops the reader and closes the L2CAP channel with bounded waits.
 * *CanFree is TRUE only when no read can still complete into the session.
 */
_IRQL_requires_(PASSIVE_LEVEL)
NTSTATUS
Ssap3TransportCloseSession(
    _In_ WDFDEVICE Device,
    _Inout_ PSSAP3_SESSION_ENTRY Session,
    _Out_ BOOLEAN* CanFree,
    _Out_ ULONG* DriverError
    );

_IRQL_requires_(PASSIVE_LEVEL)
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
    _Out_ ULONGLONG* ReceiveInterruptTime,
    _Out_ ULONG* DriverError
    );

/*
 * Least-privilege AACP framing check for outbound packets (S-09): only the
 * handshake and the opcodes/control identifiers SSAP3 actually uses.
 */
BOOLEAN
Ssap3IsAllowedOutboundPacket(
    _In_reads_bytes_(PacketLength) const UCHAR* Packet,
    _In_ ULONG PacketLength
    );
