/*
 * SSAP3 AAP transport bridge: user-mode <-> kernel IOCTL contract.
 *
 * Adapted from the GPL-3.0 LibrePods Windows bridge protocol. Version 2 is
 * the SSAP3 hardening revision (SECURITY_AUDIT.md, S-05/S-06/S-08/S-09):
 *   - a new device-interface GUID, so it never binds to SoundStage/LibrePods drivers;
 *   - the status reply carries the PDO's remote Bluetooth address;
 *   - session limits, address binding and AACP framing checks.
 *
 * Every structure is validated by size in the driver; all IOCTLs are
 * METHOD_BUFFERED and FILE_ANY_ACCESS (access is enforced by the device SDDL).
 */
#ifndef SSAP3_DRIVER_PROTOCOL_H
#define SSAP3_DRIVER_PROTOCOL_H

typedef unsigned char ssap3_u8;
typedef unsigned int ssap3_u32;
typedef unsigned __int64 ssap3_u64;

#ifndef FILE_DEVICE_UNKNOWN
#define FILE_DEVICE_UNKNOWN 0x00000022u
#endif

#ifndef FILE_ANY_ACCESS
#define FILE_ANY_ACCESS 0u
#endif

#ifndef METHOD_BUFFERED
#define METHOD_BUFFERED 0u
#endif

#ifndef CTL_CODE
#define CTL_CODE(device_type, function, method, access) \
    ((((device_type) << 16) | ((access) << 14) | ((function) << 2) | (method)))
#endif

#define SSAP3_DRIVER_PROTOCOL_VERSION 2u
#define SSAP3_DRIVER_INTERFACE_GUID_STRING L"{1C3A8128-3668-4428-95A4-B1CAD51B7249}"
#define SSAP3_DRIVER_MAX_BLUETOOTH_ADDRESS_LENGTH 18u
#define SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH 512u
#define SSAP3_DRIVER_MAX_SESSIONS_PER_HANDLE 1u
#define SSAP3_DRIVER_MAX_SESSIONS_PER_DEVICE 4u

#define SSAP3_IOCTL_GET_BRIDGE_STATUS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define SSAP3_IOCTL_OPEN_AACP_CHANNEL \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define SSAP3_IOCTL_CLOSE_AACP_CHANNEL \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define SSAP3_IOCTL_SEND_AACP_PACKET \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define SSAP3_IOCTL_READ_AACP_PACKET \
    CTL_CODE(FILE_DEVICE_UNKNOWN, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS)

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
    ssap3_u8 packet[SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH];
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
    ssap3_u8 packet[SSAP3_DRIVER_MAX_AACP_PACKET_LENGTH];
} ssap3_driver_read_packet_response;

#endif
