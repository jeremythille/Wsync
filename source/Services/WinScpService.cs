using System.Diagnostics;
using System.IO;
using System.Text;
using Wsync.Models;

namespace Wsync.Services;

/// <summary>
/// Service for generating and executing WinSCP synchronization scripts
/// </summary>
public class WinScpService
{
    private readonly FtpConnectionConfig _ftpConfig;
    private readonly string _localPath;
    private readonly string _remotePath;
    private readonly List<string> _excludedExtensions;
    private readonly List<string> _excludedFoldersFromSync;
    private readonly string? _winscpPath;
    private Action<string>? _statusCallback;

    /// <summary>
    /// Common build artifacts, cache, and IDE folders that should never be synced.
    /// Combined with config excludedFoldersFromSync during initialization.
    /// </summary>
    private readonly string[] _defaultExcludedFolders = new[]
    {
        "node_modules",
        ".svn",
        ".vscode",
        ".github",
        "bin",
        "obj",
        "dist",
        "build",
        ".vs",
        "packages",
        "__pycache__",
        ".pytest_cache",
        "venv",
        "env",
        ".angular",
        ".idea",
        ".DS_Store",
        "Thumbs.db",
        "non-code",
        "test-results",
        "playwright-report"
    };

    public WinScpService(
        FtpConnectionConfig ftpConfig,
        string localPath,
        string remotePath,
        List<string>? excludedExtensions = null,
        List<string>? excludedFoldersFromSync = null,
        string? winscpPath = null)
    {
        _ftpConfig = ftpConfig;
        // Normalize local paths using Windows convention (backslashes)
        // Normalize remote paths using Unix convention (forward slashes)
        _localPath = Path.GetFullPath(localPath);  // Windows format with backslashes
        _remotePath = remotePath.Replace("\\", "/");  // Unix format with forward slashes
        _excludedExtensions = excludedExtensions ?? new List<string>();
        
        // Combine default excluded folders with config-provided folders
        _excludedFoldersFromSync = new List<string>(_defaultExcludedFolders);
        if (excludedFoldersFromSync != null)
        {
            _excludedFoldersFromSync.AddRange(excludedFoldersFromSync);
        }
        
        _winscpPath = winscpPath;
    }

    public void SetStatusCallback(Action<string> callback)
    {
        _statusCallback = callback;
    }

    /// <summary>
    /// Syncs files from local to FTP (upload) using WinSCP
    /// </summary>
    public async Task SyncToFtpAsync(Action<string>? progressCallback = null)
    {
        var script = GenerateScript(isUpload: true);
        await ExecuteScriptAsync(script, progressCallback);
    }

    /// <summary>
    /// Syncs files from FTP to local (download) using WinSCP
    /// </summary>
    public async Task SyncToLocalAsync(Action<string>? progressCallback = null)
    {
        // Before sync, remove ReadOnly attributes from all .git/objects files
        // This allows WinSCP to delete them if needed during sync
        await RemoveReadOnlyAttributesAsync(progressCallback);
        
        var script = GenerateScript(isUpload: false);
        await ExecuteScriptAsync(script, progressCallback);
    }

    /// <summary>
    /// Generates WinSCP script for synchronization
    /// </summary>
    private string GenerateScript(bool isUpload)
    {
        var sb = new StringBuilder();

        // Connection string
        var connectionString = GenerateConnectionString();
        sb.AppendLine($"open {connectionString}");
        sb.AppendLine();
        
        // Set options to continue on errors instead of aborting
        sb.AppendLine("option batch continue");
        sb.AppendLine("option confirm off");
        sb.AppendLine("option echo off");
        sb.AppendLine();

        // Generate filemask for exclusions
        var filemask = GenerateFilemask();
        
        // Use paths as-is: local paths with Windows backslashes, remote paths with Unix forward slashes
        // WinSCP will handle the path separators correctly for each side
        if (isUpload)
        {
            // Sync local to remote: synchronize remote <local_path> <remote_path>
            // -delete: removes files on remote that don't exist locally
            // -verbose: show individual file transfers
            sb.AppendLine($"synchronize remote -delete -criteria=time -verbose -filemask=\"{filemask}\" \"{_localPath}\" \"{_remotePath}\"");
        }
        else
        {
            // Sync remote to local: synchronize local <local_path> <remote_path>
            // -delete: removes files locally that don't exist on remote
            // -verbose: show individual file transfers
            sb.AppendLine($"synchronize local -delete -criteria=time -verbose -filemask=\"{filemask}\" \"{_localPath}\" \"{_remotePath}\"");
        }

        sb.AppendLine();
        sb.AppendLine("exit");

        return sb.ToString();
    }

    /// <summary>
    /// Generates connection string for WinSCP
    /// Format: ftp://username:password@host:port/
    /// </summary>
    private string GenerateConnectionString()
    {
        var protocol = _ftpConfig.Secure ? "sftp" : "ftp";
        var port = _ftpConfig.Port;
        
        // Escape special characters in password
        var escapedPassword = _ftpConfig.Password.Replace("@", "%40").Replace("\\", "%5C");
        
        return $"{protocol}://{_ftpConfig.Username}:{escapedPassword}@{_ftpConfig.Host}:{port}/";
    }

    /// <summary>
    /// Generates WinSCP filemask for exclusions
    /// Format: |excluded1;excluded2;*.ext;*.log
    /// </summary>
    private string GenerateFilemask()
    {
        var exclusions = new List<string>();

        // Add excluded folders - use both folder name and folder/* pattern to ensure they're fully excluded
        foreach (var folder in _excludedFoldersFromSync)
        {
            exclusions.Add($"{folder}");      // Exclude the folder itself
            exclusions.Add($"{folder}/*");    // Exclude all contents
        }

        // Add excluded extensions
        foreach (var ext in _excludedExtensions)
        {
            exclusions.Add($"*.{ext}");
        }

        // WinSCP filemask format: |exclude1;exclude2;exclude3
        if (exclusions.Count == 0)
            return "";

        return "|" + string.Join(";", exclusions);
    }

    /// <summary>
    /// Executes WinSCP script and captures output
    /// </summary>
    private async Task ExecuteScriptAsync(string script, Action<string>? progressCallback = null)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"wsync_{Guid.NewGuid()}.txt");
        
        try
        {
            // Write script to temp file
            Log($"Writing WinSCP script to {scriptPath}");
            await File.WriteAllTextAsync(scriptPath, script);
            Log($"Script content:\n{script}");

            // Execute WinSCP
            Log("Executing WinSCP...");
            await ExecuteWinScpAsync(scriptPath, progressCallback);

            Log("Sync completed successfully!");
            progressCallback?.Invoke("✓ Sync completed successfully!");
        }
        catch (Exception ex)
        {
            Log($"Error during sync: {ex.Message}");
            progressCallback?.Invoke($"❌ Sync failed: {ex.Message}");
            throw;
        }
        finally
        {
            // Clean up script file
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                    Log($"Cleaned up script file");
                }
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not delete script file: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs WinSCP.com with the script and captures output
    /// </summary>
    private async Task ExecuteWinScpAsync(string scriptPath, Action<string>? progressCallback = null)
    {
        // Find WinSCP executable
        var winScpPath = FindWinScpExecutable();
        if (string.IsNullOrEmpty(winScpPath))
        {
            throw new Exception(
                "WinSCP not found. Please install WinSCP or ensure it's in your PATH.\n\n" +
                "Download from: https://winscp.net/download/");
        }

        Log($"Using WinSCP: {winScpPath}");

        var processInfo = new ProcessStartInfo
        {
            FileName = winScpPath,
            Arguments = $"/script=\"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (var process = Process.Start(processInfo))
        {
            if (process == null)
                throw new Exception("Failed to start WinSCP process");

            var output = new StringBuilder();
            var error = new StringBuilder();

            // Capture output asynchronously
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask);

            var stdout = outputTask.Result;
            var stderr = errorTask.Result;
            // Log output - filter out only connection/auth messages, show everything else
            if (!string.IsNullOrEmpty(stdout))
            {
                foreach (var line in stdout.Split('\n'))
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        // Skip only the verbose WinSCP connection/authentication setup messages
                        if (line.Contains("Searching for host") || 
                            line.Contains("Connecting to host") ||
                            line.Contains("Authenticating") ||
                            line.Contains("Using username") ||
                            line.Contains("Authenticating with") ||
                            line.Contains("Authenticated") ||
                            line.Contains("Starting the session") ||
                            line.Contains("Session started") ||
                            line.Contains("Active session") ||
                            line.Contains("batch") ||
                            line.Contains("confirm") ||
                            line.Contains("Using configured") ||
                            line.Contains("echo"))
                        {
                            Log(line); // Still log to file for debugging
                            // But don't show in UI
                        }
                        else
                        {
                            // Show everything else: file transfers, comparison, sync messages
                            Log(line);
                            progressCallback?.Invoke(line);
                        }
                    }
                }
            }

            // Log errors
            if (!string.IsNullOrEmpty(stderr))
            {
                foreach (var line in stderr.Split('\n'))
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        Log($"[ERROR] {line}");
                        progressCallback?.Invoke($"⚠ {line}");
                    }
                }
            }

            // Wait for process to complete
            process.WaitForExit();

            // Log the exit code
            Log($"WinSCP exited with code {process.ExitCode}");

            // Check exit code - WinSCP returns 0 on complete success, non-zero if there were any errors
            // But it still performs the sync, so we report partial success
            if (process.ExitCode != 0)
            {
                // Check if errors are ignorable (access denied on file operations, typically on non-existent or locked files)
                var hasIgnorableErrors = false;
                if (stdout.Contains("Error deleting file") && stdout.Contains("Access is denied"))
                {
                    // Access denied when deleting is typically because:
                    // 1. File doesn't exist anymore (already deleted in previous sync)
                    // 2. File is locked by another process
                    // Both are acceptable in a sync operation
                    hasIgnorableErrors = true;
                    Log("ℹ Note: Some files couldn't be deleted (may already be deleted or locked), but sync proceeded");
                }
                
                // Extract summary from output to determine if sync actually happened
                var hasSyncMessage = stdout.Contains("Synchronizing") || 
                                   stdout.Contains("transferred") ||
                                   stdout.Contains("Local") ||
                                   stdout.Contains("deleted");
                
                if (hasSyncMessage && hasIgnorableErrors)
                {
                    // Sync succeeded with only ignorable errors
                    Log("✓ Sync completed successfully!");
                    progressCallback?.Invoke("✓ Sync completed successfully!");
                }
                else if (hasSyncMessage)
                {
                    Log("⚠ WinSCP completed with some errors, but files were transferred");
                    progressCallback?.Invoke("✓ Sync completed (with warnings - some files may not have been synced)");
                    Log("Note: Check the log above for which files had issues");
                }
                else
                {
                    throw new Exception($"WinSCP exited with code {process.ExitCode}. Check log for details.");
                }
            }
        }
    }

    /// <summary>
    /// Finds WinSCP executable in common locations or uses configured path
    /// Accepts both folder path (e.g., "D:/Progz/dev/WinSCP") and full file path (e.g., "D:/Progz/dev/WinSCP/winscp.com")
    /// </summary>
    private string? FindWinScpExecutable()
    {
        // First, check if a path was provided in config
        if (!string.IsNullOrEmpty(_winscpPath))
        {
            // If it's a folder, append winscp.com
            string exePath = _winscpPath;
            if (Directory.Exists(_winscpPath))
            {
                exePath = Path.Combine(_winscpPath, "winscp.com");
                Log($"Config path is a folder, appending winscp.com: {exePath}");
            }

            if (File.Exists(exePath))
            {
                Log($"Using WinSCP from config path: {exePath}");
                return exePath;
            }
            else
            {
                Log($"Warning: WinSCP path in config does not exist: {exePath}");
            }
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var exePath = Path.Combine(dir, "winscp.com");
                if (File.Exists(exePath))
                {
                    Log($"Found WinSCP in PATH: {exePath}");
                    return exePath;
                }
            }
        }

        // Check common installation paths
        var commonPaths = new[]
        {
            @"C:\Program Files\WinSCP\winscp.com",
            @"C:\Program Files (x86)\WinSCP\winscp.com",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WinSCP\winscp.com"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"WinSCP\winscp.com")
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                Log($"Found WinSCP in common installation path: {path}");
                return path;
            }
        }

        Log("WinSCP executable not found in PATH or common installation locations");
        return null;
    }

    private void Log(string message)
    {
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        var logPath = Path.Combine(exeDir, "wsync.log");

        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [WinScpService] {message}\n");
        }
        catch
        {
            // Ignore logging errors
        }
    }

    /// <summary>
    /// Removes ReadOnly attributes from all .git/objects files to allow deletion during sync
    /// Does NOT delete files - just removes the attribute that prevents deletion
    /// </summary>
    private async Task RemoveReadOnlyAttributesAsync(Action<string>? progressCallback = null)
    {
        try
        {
            var gitObjectsPath = Path.Combine(_localPath, ".git", "objects");
            
            if (!Directory.Exists(gitObjectsPath))
            {
                return; // No .git/objects folder
            }

            var objectFiles = Directory.GetFiles(gitObjectsPath, "*", SearchOption.AllDirectories);
            var modifiedCount = 0;
            
            foreach (var filePath in objectFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if ((fileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        fileInfo.Attributes &= ~FileAttributes.ReadOnly;
                        modifiedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Could not remove ReadOnly from {filePath}: {ex.Message}");
                }
            }
            
            if (modifiedCount > 0)
            {
                Log($"Removed ReadOnly attribute from {modifiedCount} .git/objects files");
            }
        }
        catch (Exception ex)
        {
            Log($"Error removing ReadOnly attributes: {ex.Message}");
        }
    }
}
