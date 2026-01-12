# 🚀 PS5 Upload Suite - High-Speed File Transfer

**By Manos**

Custom high-speed file transfer system for PS5 with etaHEN. Achieves **100+ MB/s** transfer speeds using a custom binary protocol.

![PS5 Upload Client](screenshots/screenshot1.png)
![Upload in Progress](screenshots/screenshot2.png)

---

## 📦 What's Included

### 1. PS5 Server Payload
- **File:** `payload/ps5_upload_server.elf`
- **Port:** 9113
- **Protocol:** Custom binary (optimized for speed)
- **Features:**
  - 4MB socket buffers
  - SO_NOSIGPIPE enabled
  - TCP_NODELAY for low latency
  - Direct disk writes (no temp files)
  - Multi-threaded client handling

### 2. Windows GUI Client
- **File:** `client/bin/Release/net6.0-windows/win-x64/publish/PS5Upload.exe`
- **Framework:** .NET 6.0 WPF
- **Features:**
  - Modern dark theme UI
  - Drag & drop file/folder upload
  - Browse PS5 filesystem
  - Real-time upload progress
  - Speed indicator (MB/s)
  - Storage space display
  - Recursive folder upload

---

## 🚀 Quick Start

### Step 1: Load PS5 Payload

1. Copy `payload/ps5_upload_server.elf` to your PS5:
   ```
   /data/etaHEN/payloads/ps5_upload_server.elf
   ```

2. Load the payload with elfldr

3. You should see notification:
   ```
   PS5 Upload Server: 192.168.0.XXX:9113 - By Manos
   ```

### Step 2: Run Windows Client

1. Run `PS5Upload.exe` on your PC

2. Enter your PS5's IP address (e.g., `192.168.0.160`)

3. Click **Connect**

4. Browse files/folders or drag & drop

5. Click **Upload to PS5**

---

## 🎨 Features

### PS5 Server
✅ **High-Speed Protocol** - Custom binary protocol  
✅ **4MB Buffers** - Maximum throughput  
✅ **Direct Disk I/O** - No temp files  
✅ **Multi-threaded** - Handle multiple clients  
✅ **Robust Error Handling** - Graceful failures  

### Windows Client
✅ **Modern UI** - Dark theme, clean design  
✅ **Drag & Drop** - Files and folders  
✅ **Browse PS5** - Navigate filesystem  
✅ **Real-time Progress** - Speed & percentage  
✅ **Folder Upload** - Recursive directory upload  
✅ **Storage Info** - Free space display  

---

## 📊 Performance

| Network | Expected Speed |
|---------|----------------|
| **Gigabit Ethernet** | 100-120 MB/s |
| **WiFi 6 (5GHz)** | 60-80 MB/s |
| **WiFi 5 (5GHz)** | 40-60 MB/s |

**Note:** Speeds depend on network quality and PS5 disk performance.

---

## 🔧 Building from Source

### PS5 Payload

Requirements:
- PS5 SDK (prospero-clang)
- WSL or Linux

```bash
cd payload
bash compile.sh
```

### Windows Client

Requirements:
- .NET 6.0 SDK or later
- Windows 10/11

```cmd
cd client
build.bat
```

Or manually:
```cmd
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## 📋 Protocol Specification

See [PROTOCOL.md](PROTOCOL.md) for detailed protocol documentation.

### Commands
- `PING` - Test connection
- `LIST_STORAGE` - Get storage info
- `LIST_DIR` - List directory contents
- `CREATE_DIR` - Create directory
- `DELETE_FILE` - Delete file
- `DELETE_DIR` - Delete directory
- `START_UPLOAD` - Begin file upload
- `UPLOAD_CHUNK` - Upload file chunk
- `END_UPLOAD` - Finish upload
- `SHUTDOWN` - Shutdown server

---

## 🛠️ Troubleshooting

### Connection Failed
- Make sure PS5 payload is running
- Check PS5 IP address is correct
- Verify both devices are on same network
- Check firewall settings

### Slow Upload Speed
- Use wired Ethernet connection (not WiFi)
- Close other network applications
- Check PS5 disk health

### Upload Fails
- Check PS5 has enough free space
- Verify destination path exists
- Check file permissions

---

## 🔒 Security Notes

- Server only accepts connections from local network
- No authentication required (local network only)
- SHUTDOWN command only works from localhost

---

## 📝 License

MIT License - Free to use and modify

---

## 👤 Author

**Manos**

Created with ❤️ for the PS5 homebrew community

---

## 🙏 Credits

- PS5 SDK
- etaHEN
- Inspired by ps5upload by PhantomPtr
- PS5 homebrew community

---

## 📞 Support

For issues or questions, please open an issue on GitHub.

---

**Enjoy blazing-fast file transfers on your PS5!** 🚀
