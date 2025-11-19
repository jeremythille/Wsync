# Wsync - File Sync Utility

A tiny, minimal Windows desktop application for synchronizing files between your local desktop and an FTP/SFTP server. Uses **WinSCP** as the sync engine for reliable, efficient file transfers.

## Features

- 🖥️ Clean, minimal UI - quickly see sync status at a glance
- 📋 Easy per-project configuration - manage multiple projects
- 🚀 Portable - no installation needed, can run from a USB key
- 🪶 Super lightweight - less than 2 MB (plus WinSCP)
- 🔄 Bi-directional file sync (Desktop ↔ FTP/SFTP)
- 🎯 Multiple analysis modes:
  - **Full**: Analyzes all files recursively
  - **Quick**: Analyzes only root + 1 level deep (fast decision)
  - **Git**: Compares latest git commits
- ⚡ Fast timestamp-based comparison (no hashing)
- 🔒 Credentials stored securely (local config file)
- 🔧 **WinSCP-powered sync**: Reliable file synchronization with automatic file deletion and proper permission handling

<div align="center">
  <img src="./screenshot.png" alt="Wsync Screenshot" />
</div>

## Requirements

### WinSCP Installation

Wsync uses **WinSCP** for all file synchronization operations. You must have WinSCP installed:

1. **Download WinSCP**: https://winscp.net/
2. **Install** in a standard location (e.g., `C:\Program Files\WinSCP\`) or specify the path in `config.json5`
3. **Verify**: The installation should contain `winscp.com` (the command-line executable)

The application will automatically locate WinSCP if installed in a standard location, or you can specify the path in the `winscpPath` config option.

### .NET Runtime

Wsync requires **Microsoft .NET 8** runtime. If not installed, you'll be prompted to install it when you first run the application.

---

## How to Run

1. **Create a configuration file:**
   - Rename `config.example.json5` to `config.json5` in the `program/` directory
   - Edit it (in a text editor) with your FTP/SFTP server details and project paths (see below)

2. **Launch the application:**
   - Double-click `program/Wsync.exe`
   - Note: the program itself is very light, but it depends on Microsoft .net 8 libraries; if they're not installed on your system yet, you will be prompted to accept their installation.

3. **Sync your files:**
   - Select a project from the dropdown
   - Choose your sync direction (Full/Quick/Git mode)
   - Click the corresponding arrow button to sync in the desired direction

That's it! The app will analyze sync status and sync your files.

## Configuration

### Configuration File Structure

Edit `config.json5`:

```json5
{
  // Optional: Path to WinSCP executable (required for sync to work)
  // Can be either:
  //   - Full path to winscp.com: "C:\\Program Files\\WinSCP\\winscp.com"
  //   - Folder path: "D:\\WinSCP" (will automatically append winscp.com)
  // If not provided, WinSCP will be searched in PATH and common installation locations
  winscpPath: "C:\\Program Files\\WinSCP",

  projects: [
    {
      name: "project 1", // As displayed in the projects list
      localPath: "D:\\dev\\project1", // Full path to your local project folder
      remotePath: "/work/dev/project1", // Remote path on the FTP server
    },
    {
      name: "project 2",
      localPath: "D:\\architecture\\project2",
      remotePath: "/work/project2",
    },
  ],

  ftp: {
    host: "11.22.33.44",
    port: 22, // Use 21 for standard FTP, 22 for SFTP
    username: "user",
    password: "pwd",
    passiveMode: false, // Usually true for FTP connections
    secure: true, // Set to true to use SFTP (SSH) instead of FTP
  },

  // File extensions ignored during both analysis AND sync
  excludedExtensions: ["html", "css", "js"],

  // Folders ignored during ANALYSIS only (they will still be synced)
  excludedFoldersFromAnalysis: ["deployment", "assets", "public"],

  // Folders ignored from analysis AND sync
  excludedFoldersFromSync: ["common-slave", "mongodumps"],
}
```

### Configuration Tips

- **winscpPath**: Path to WinSCP installation (optional if WinSCP is in PATH or default location)
- **LocalPath**: Full path to your local project folder
- **RemotePath**: Remote path on the FTP server
- **Secure**: `true` for SFTP (SSH), `false` for standard FTP
- **ExcludedExtensions**: File types to skip during analysis **and sync** (without dots)
- **ExcludedFoldersFromAnalysis**: Folders to skip during analysis only (speeds up comparison)
- **ExcludedFoldersFromSync**: Folders to completely exclude from both analysis and sync

### Sync Behavior

The sync engine uses **WinSCP's synchronization** with the following behavior:

- **Timestamp-based comparison** (`-criteria=time`): Files are compared by modification timestamp
- **Automatic file deletion** (`-delete` flag): Files deleted locally/remotely are also deleted on the other side
- **Error tolerance** (`batch continue`): Continues syncing even if some files fail
- **Git object handling**: `.git/objects` files have their read-only attribute removed before sync to allow proper deletion

---

## (OPTIONAL) Building the App (for Developers)

### Prerequisites
- .NET 8 SDK
- Visual Studio 2022 or Visual Studio Code

### Build from Source

```powershell
cd source
dotnet build
```

### Run Locally

```powershell
cd source
dotnet run
```

### Publish as Standalone EXE

Create a self-contained, standalone executable:

```powershell
cd source
dotnet publish -c Release -r win-x64 --self-contained
```

The executable will be in `program/Wsync.exe`

### Project Structure

```
source/
  ├── App.xaml / App.xaml.cs                  # Application entry point
  ├── MainWindow.xaml / MainWindow.xaml.cs    # Main UI and sync orchestration
  ├── Wsync.csproj                            # Project file
  ├── Services/
  │   ├── ConfigService.cs                    # Config loading/saving
  │   ├── FtpService.cs                       # SFTP/SSH file comparison (analysis only)
  │   └── WinScpService.cs                    # WinSCP wrapper for file synchronization
  └── Models/
      └── ProjectConfig.cs                    # Data models
```

### Architecture

**Wsync** separates concerns into two main components:

1. **Analysis Phase** (FtpService):
   - Compares files on both sides using SFTP
   - Supports multiple analysis modes (Full, Quick, Git)
   - Determines sync recommendation (which direction to sync)

2. **Sync Phase** (WinScpService):
   - Uses WinSCP command-line tool for actual file synchronization
   - Handles file deletion with proper attribute handling
   - Manages exclusions via filemask patterns
   - Provides real-time progress feedback

---

**DO NOT COMMIT `config.json5`** - This file contains your FTP/SFTP credentials!

The file is already ignored by git (see `.gitignore`), but be extra careful:
- Never push `config.json5` to version control
- Always use `config.example.json5` as a template
- Keep your FTP credentials safe and local

---

## License

MIT
