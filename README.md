# Wsync - File Sync Utility

A simple Windows desktop application for synchronizing files between your local desktop and an FTP server.

## Features

- 🖥️ Clean, minimal UI (300x600px)
- 📋 Project-based configuration
- 🔄 Bi-directional file sync (Desktop ↔ FTP)
- 📝 JSON configuration file
- ⚡ Quick, lightweight

## Setup

### Prerequisites
- .NET 8 Runtime or SDK

### Configuration

Create a `config.json5` file in the same directory as `Wsync.exe`. This file will contain your projects and FTP credentials.

**⚠️ Important:** The `config.json5` file is ignored by git (see `.gitignore`). This is intentional to protect your credentials - never commit this file!

#### Default Configuration Template

Create `config.json5` with the following structure:

```json5
{
  // List of projects to sync
  "Projects": [
    {
      "Name": "My Project",
      "LocalPath": "C:\\Users\\YourName\\Documents\\MyProject",
      "FtpRemotePath": "/www/my-project"
    },
    {
      "Name": "Another Project",
      "LocalPath": "D:\\dev\\another-project",
      "FtpRemotePath": "/www/another-project"
    }
  ],

  // FTP connection settings (used for all projects)
  "Ftp": {
    "Host": "ftp.example.com",
    "Port": 21,
    "Username": "your-username",
    "Password": "your-password",
    "PassiveMode": true,
    "Secure": false  // Set to true for SFTP (port 22 recommended)
  }
}
```

#### Configuration Tips

- **LocalPath**: Full path to your local project folder
- **FtpRemotePath**: Remote path on the FTP server
- **Port**: Use 21 for standard FTP, 22 for SFTP
- **PassiveMode**: Usually `true` for FTP connections
- **Secure**: Set to `true` to use SFTP (SSH) instead of FTP

### Building

```powershell
dotnet build
```

### Running

```powershell
dotnet run
```

### Publishing to EXE

Create a self-contained, standalone executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained
```

The executable will be in `bin/Release/net8.0-windows/win-x64/publish/Wsync.exe`

## UI Layout

- **Top**: Project selector dropdown
- **Middle**: Desktop (💻) and FTP Server (☁) icons
- **Center**: Two sync buttons
  - Left arrow (🟢 Green): Sync from FTP → Desktop
  - Right arrow (🔵 Blue): Sync from Desktop → FTP

## TODO

- [ ] Implement FTP sync logic
- [ ] Add progress indicators
- [ ] Error handling and logging
- [ ] File filtering/exclusion patterns
- [ ] Drag-and-drop support
- [ ] Settings dialog for credentials

## License

MIT
