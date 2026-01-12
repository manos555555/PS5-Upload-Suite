# PS5 Upload Protocol Specification

## Overview
Custom binary protocol for high-speed file transfers between PC and PS5.
Port: 9113

## Message Format

All messages follow this structure:
```
[Command (1 byte)][Data Length (4 bytes)][Data (variable)]
```

## Commands

### Client → Server

| Command | Value | Description | Data Format |
|---------|-------|-------------|-------------|
| PING | 0x01 | Test connection | None |
| LIST_STORAGE | 0x02 | Get storage info | None |
| LIST_DIR | 0x03 | List directory | Path (null-terminated string) |
| CREATE_DIR | 0x04 | Create directory | Path (null-terminated string) |
| DELETE_FILE | 0x05 | Delete file | Path (null-terminated string) |
| DELETE_DIR | 0x06 | Delete directory | Path (null-terminated string) |
| START_UPLOAD | 0x10 | Start file upload | Path (null-terminated) + File size (8 bytes) |
| UPLOAD_CHUNK | 0x11 | Upload file chunk | Chunk data (raw bytes) |
| END_UPLOAD | 0x12 | Finish upload | None |
| SHUTDOWN | 0xFF | Shutdown server | None |

### Server → Client

| Response | Value | Description | Data Format |
|----------|-------|-------------|-------------|
| OK | 0x01 | Success | Optional message |
| ERROR | 0x02 | Error occurred | Error message (null-terminated) |
| DATA | 0x03 | Response data | Variable format |
| READY | 0x04 | Ready for next chunk | None |
| PROGRESS | 0x05 | Upload progress | Bytes written (8 bytes) |

## Data Formats

### LIST_STORAGE Response
```
[Drive count (4 bytes)]
For each drive:
  [Path length (2 bytes)][Path][Total space (8 bytes)][Free space (8 bytes)]
```

### LIST_DIR Response
```
[Entry count (4 bytes)]
For each entry:
  [Type (1 byte: 0=file, 1=dir)][Name length (2 bytes)][Name][Size (8 bytes)][Timestamp (8 bytes)]
```

## Flow Examples

### Upload File
```
Client: START_UPLOAD "/data/test.bin" size=1048576
Server: READY
Client: UPLOAD_CHUNK [4MB data]
Server: PROGRESS bytes=4194304
Client: UPLOAD_CHUNK [remaining data]
Server: PROGRESS bytes=1048576
Client: END_UPLOAD
Server: OK
```

### List Directory
```
Client: LIST_DIR "/data"
Server: DATA [directory listing]
```

## Performance Optimizations

- 4MB socket buffers
- SO_NOSIGPIPE enabled
- TCP_NODELAY for low latency
- Direct disk writes (no temp files)
- Streaming chunks (no full file buffering)
