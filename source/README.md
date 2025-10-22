# Wsync - File Sync Utility

A simple Windows desktop application for synchronizing files between your local desktop and an FTP server.

## Features

- 🖥️ Clean, minimal UI (300x300px)
- 📋 Project-based configuration
- 🔄 Bi-directional file sync (Desktop ↔ FTP)
- 📝 JSON configuration file
- ⚡ Quick, lightweight

## Setup

### Prerequisites
- .NET 8 Runtime or SDK

### Configuration

Create or edit `config.json` in the app directory:

```json
{
  "projects": [
    {
      "name": "My Project",
      "localPath": "C:\\Users\\YourName\\Documents\\MyProject",
      "ftpHost": "ftp.example.com",
      "ftpPort": 21,
      "ftpUsername": "username",
      "ftpPassword": "password",
      "ftpRemotePath": "/public_html/myproject"
    }
  ]
}
```

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
