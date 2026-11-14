/*
 * SSAP3 AAP transport bridge: user-mode <-> kernel IOCTL contract.
 *
 * Adapted from the GPL-3.0 LibrePods Windows bridge protocol. Version 2 is
 * the SSAP3 hardening revision (SECURITY_AUDIT.md, S-05/S-06/S-08/S-09):
 *   - a new device-interface GUID, so it never binds to SoundStage/LibrePods drivers;
 *   - the status reply carries the PDO's remote Bluetooth address;
 *   - session limits, address binding and AACP framing checks.
 * Version 3 separates the inbound and outbound packet limits. Inbound packets
 * may be up to the L2CAP receive MTU iOS uses (2582): AirPods only announce
 * the sensor services whose descriptors fit the receiver's MTU, so a small MTU
 * hides head tracking. Outbound packets stay capped at 512 bytes.
 * Version 4 makes the I/O manager check the handle's access for each IOCTL:
 * status and read need FILE_READ_ACCESS; open, close and send need
 * FILE_WRITE_ACCESS. The device SDDL grants read and write only to
 * interactive users, SYSTEM and Administrators, so a read-only handle (for
 * example from a low-integrity process) can no longer drive the AirPods.
 *
 * Every structure is validated by size in the driver; all IOCTLs are
 * METHOD_BUFFERED.
 */
#ifndef SSAP3_DRIVER_PROTOCOL_H
#define SSAP3_DRIVER_PROTOCOL_H

typedef unsigned char ssap3_u8;
typedef unsigned int ssap3_u32;
typedef unsigned __int64 ssap3_u64;

#ifndef FILE_DEVICE_UNKNOWN
#define FILE_DEVICE_UNKNOWN 0x00000022u
#endif

#ifndef FILE_READ_ACCESS
#define FILE_READ_ACCESS 0x0001u
#endif

#ifndef FILE_WRITE_ACCESS
#define FILE_WRITE_ACCESS 0x0002u
#endif

#ifndef METHOD_BUFFERED
#define METHOD_BUFFERED 0u
#endif

#ifndef CTL_CODE
#define CTL_CODE(device_type, function, method, access) \
    ((((device_type) << 16) | ((access) << 14) | ((function) << 2) | (method)))
#endif

#define SSAP3_DRIVER_PROTOCOL_VERSION 4u
#define SSAP3_DRIVER_INTERFACE_GUID_STRING L"{1C3A8128-3668-4428-95A4-B1CAD51B7249}"
#define SSAP3_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH 18u
#define SSAP3_DRIVER_MAX_OUTBOUND_PACKET_LENGTH 512u
#define SSAP3_DRIVER_MAX_INBOUND_PACKET_LENGTH 2582u
#define SSAP3_DRIVER_MAX_SESSIONS_PER_HANDLE 1u
#define SSAP3_DRIVER_MAX_SESSIONS_PER_DEVICE 4u

/* Codes as the client sees them: 0x00226000, 0x0022A004, 0x0022A008,
 * 0x0022A00C, 0x00226010 (checked below). */
#define SSAP3_IOCTL_GET_BRIDGE_STATUS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)
#define SSAP3_IOCTL_OPEN_AACP_CHANNEL \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define SSAP3_IOCTL_CLOSE_AACP_CHANNEL \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define SSAP3_IOCTL_SEND_AACP_PACKET \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define SSAP3_IOCTL_READ_AACP_PACKET \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x804, METHOD_BUFFERED, FILE_READ_ACCESS)

enum ssap3_driver_capability_flags {
    SSAP3_DRIVER_CAPABILITY_NONE = 0,
    SSAP3_DRIVER_CAPABILITY_USER_MODE_BRIDGE = 1 << 0,
    SSAP3_DRIVER_CAPABILITY_DRIVER_BACKED_L2CAP = 1 << 1,
    SSAP3_DRIVER_CAPABILITY_AACP_CONTROL = 1 << 2,
    SSAP3_DRIVER_CAPABILITY_BATTERY_NOTIFICATIONS = 1 << 3
};

enum ssap3_driver_error_code {
    SSAP3_DRIVER_ERROR_NONE = 0,
    SSAP3_DRIVER_ERROR_NOT_IMPLEMENTED = 1,
    SSAP3_DRIVER_ERROR_DRIVER_UNAVAILABLE = 2,
    SSAP3_DRIVER_ERROR_ACCESS_DENIED = 3,
    SSAP3_DRIVER_ERROR_TRANSPORT_BLOCKED = 4,
    SSAP3_DRIVER_ERROR_INTERNAL = 5,
    SSAP3_DRIVER_ERROR_INVALID_ARGUMENT = 6,
    SSAP3_DRIVER_ERROR_SESSION_LIMIT = 7,
    SSAP3_DRIVER_ERROR_PACKET_REJECTED = 8,
    SSAP3_DRIVER_ERROR_ADDRESS_MISMATCH = 9,
    SSAP3_DRIVER_ERROR_DISCONNECTED = 10
};

/* 40 bytes. */
typedef struct ssap3_driver_status {
    ssap3_u32 struct_size;
    ssap3_u32 protocol_version;
    ssap3_u32 capability_flags;
    ssap3_u32 max_packet_size;
    ssap3_u32 last_error;
    ssap3_u32 initialization_flags;
    ssap3_u64 initialization_result; /* (failure stage << 32) | NTSTATUS */
    ssap3_u64 remote_address;        /* BTH_ADDR of the bound PDO, 0 if unknown */
} ssap3_driver_status;

typedef struct ssap3_driver_open_session_request {
    ssap3_u32 struct_size;
    char bluetooth_address[SSAP3_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH]; /* "AA:BB:CC:DD:EE:FF\0", must match the PDO */
} ssap3_driver_open_session_request;

typedef struct ssap3_driver_open_session_response {
    ssap3_u32 struct_size;
    ssap3_u32 capability_flags;
    ssap3_u32 mtu;
    ssap3_u32 reserved0;
    ssap3_u64 session_id;
} ssap3_driver_open_session_response;

typedef struct ssap3_driver_close_session_request {
    ssap3_u32 struct_size;
    ssap3_u32 reserved0;
    ssap3_u64 session_id;
} ssap3_driver_close_session_request;

typedef struct ssap3_driver_send_packet_request {
    ssap3_u32 struct_size;
    ssap3_u32 packet_length;
    ssap3_u32 reserved0;
    ssap3_u32 reserved1;
    ssap3_u64 session_id;
    ssap3_u8 packet[SSAP3_DRIVER_MAX_OUTBOUND_PACKET_LENGTH];
} ssap3_driver_send_packet_request;

typedef struct ssap3_driver_read_packet_request {
    ssap3_u32 struct_size;
    ssap3_u32 packet_buffer_length;
    ssap3_u32 reserved0;
    ssap3_u32 reserved1;
    ssap3_u64 session_id;
} ssap3_driver_read_packet_request;

typedef struct ssap3_driver_read_packet_response {
    ssap3_u32 struct_size;
    ssap3_u32 packet_length;
    /* KeQueryInterruptTime() when the packet arrived (100 ns units, system-wide,
     * monotonic; user mode reads the same clock with QueryInterruptTime). */
    ssap3_u64 receive_interrupt_time;
    ssap3_u8 packet[SSAP3_DRIVER_MAX_INBOUND_PACKET_LENGTH];
} ssap3_driver_read_packet_response;

/*
 * Wire sizes the user-mode client (Ssap3DriverAacpClient.cs) hard-codes. The
 * read response is padded to a multiple of 8 (16 + 2582 = 2598 -> 2600).
 * These fail to compile if a structure changes without the client.
 */
typedef char ssap3_check_status_size[(sizeof(ssap3_driver_status) == 40u) ? 1 : -1];
typedef char ssap3_check_send_request_size[(sizeof(ssap3_driver_send_packet_request) == 536u) ? 1 : -1];
typedef char ssap3_check_read_request_size[(sizeof(ssap3_driver_read_packet_request) == 24u) ? 1 : -1];
typedef char ssap3_check_read_response_size[(sizeof(ssap3_driver_read_packet_response) == 2600u) ? 1 : -1];
typedef char ssap3_check_open_request_size[(sizeof(ssap3_driver_open_session_request) == 24u) ? 1 : -1];
typedef char ssap3_check_open_response_size[(sizeof(ssap3_driver_open_session_response) == 24u) ? 1 : -1];
typedef char ssap3_check_close_request_size[(sizeof(ssap3_driver_close_session_request) == 16u) ? 1 : -1];
typedef char ssap3_check_ioctl_codes[(SSAP3_IOCTL_GET_BRIDGE_STATUS == 0x00226000u &&
                                      SSAP3_IOCTL_OPEN_AACP_CHANNEL == 0x0022A004u &&
                                      SSAP3_IOCTL_CLOSE_AACP_CHANNEL == 0x0022A008u &&
                                      SSAP3_IOCTL_SEND_AACP_PACKET == 0x0022A00Cu &&
                                      SSAP3_IOCTL_READ_AACP_PACKET == 0x00226010u) ? 1 : -1];

#endif
