using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Renci.SshNet;
using Wsync.Models;

namespace Wsync.Services;

/// <summary>
/// Represents the sync direction recommendation
/// </summary>
public enum SyncRecommendation
{
    Unknown,        // Can't determine
    SyncToFtp,      // Local files are newer
    SyncToLocal,    // FTP files are newer
    BothNewer,      // Files on both sides have newer versions
    InSync,         // Files are in sync
}

/// <summary>
/// Holds detailed comparison results
/// </summary>
public class ComparisonResult
{
    public SyncRecommendation Recommendation { get; set; }
    public int NewerLocalCount { get; set; }
    public int NewerRemoteCount { get; set; }
    public int LocalOnlyCount { get; set; }
    public int RemoteOnlyCount { get; set; }
    public string LocalExample { get; set; } = "";
    public string RemoteExample { get; set; } = "";
    public string? Error { get; set; } = null;
    public List<string> NewerLocalFiles { get; set; } = new();
    public List<string> NewerRemoteFiles { get; set; } = new();
    public List<string> LocalOnlyFiles { get; set; } = new();
    public List<string> RemoteOnlyFiles { get; set; } = new();
}

/// <summary>
/// Holds file metadata: timestamp and size for fast comparison
/// </summary>
internal class FileMetadata
{
    public DateTime Timestamp { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// Service for FTP operations and file comparison
/// </summary>
public class FtpService
{
    private readonly FtpConnectionConfig _ftpConfig;
    private readonly string _localPath;
    private readonly string _remotePath;
    private readonly AnalysisMode _analysisMode;
    private Action<string>? _statusCallback;
    public ComparisonResult? LastComparisonResult { get; private set; }
    private SshClient? _sshClientCache; // Reusable SSH client for hash computation
    private readonly List<string> _excludedExtensions;
    
    // Parallel SSH connection pool
    private readonly List<SshClient> _sshConnectionPool = new();
    private readonly SemaphoreSlim _sshPoolSemaphore = new(5); // Max 5 parallel SSH connections
    private readonly SemaphoreSlim _sshCommandSemaphore = new(5); // Limit concurrent SSH commands to 5
    private const int MaxParallelConnections = 5;
    private const int MaxDepthFastMode = 2; // Root level (0) + 1 level deep (1)
    private const int MaxDepthNormalMode = int.MaxValue; // No limit
    private const int DecisionThreshold = 3; // For fast mode: decide after finding 3 differing files

    // Hardcoded folders to ignore when scanning
    private readonly string[] _hardcodedIgnoredFolders = new[]
    {
        "node_modules",
        ".git",
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

    // All ignored folders (hardcoded + from config)
    private readonly List<string> _ignoredFolders;

    public FtpService(FtpConnectionConfig ftpConfig, string localPath, string remotePath, List<string>? excludedExtensions = null, List<string>? excludedFolders = null, AnalysisMode analysisMode = AnalysisMode.Full)
    {
        _ftpConfig = ftpConfig;
        _localPath = localPath;
        _remotePath = remotePath;
        _analysisMode = analysisMode;
        _excludedExtensions = excludedExtensions ?? new List<string>();

        // Merge hardcoded ignored folders with config-provided excluded folders
        _ignoredFolders = new List<string>(_hardcodedIgnoredFolders);
        if (excludedFolders != null)
        {
            _ignoredFolders.AddRange(excludedFolders);
        }
    }

    /// <summary>
    /// Checks if a file should be excluded based on its extension
    /// </summary>
    private bool IsFileExcluded(string filename)
    {
        if (_excludedExtensions.Count == 0)
            return false;

        var extension = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
        return _excludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets a callback for status updates during analysis
    /// </summary>
    public void SetStatusCallback(Action<string> callback)
    {
        _statusCallback = callback;
    }

    /// <summary>
    /// Analyzes files on both local and FTP sides and returns a sync recommendation
    /// </summary>
    public async Task<SyncRecommendation> GetSyncRecommendationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if Git mode is requested but .git folder doesn't exist
            if (_analysisMode == AnalysisMode.Git)
            {
                var gitPath = Path.Combine(_localPath, ".git");
                if (!Directory.Exists(gitPath))
                {
                    var errorMsg = $"Git mode selected but no .git folder found at {gitPath}";
                    Log(errorMsg);
                    LastComparisonResult = new ComparisonResult
                    {
                        Recommendation = SyncRecommendation.Unknown,
                        Error = errorMsg
                    };
                    _statusCallback?.Invoke(errorMsg);
                    return SyncRecommendation.Unknown;
                }
                
                return await CompareGitCommitsAsync(cancellationToken);
            }

            // Get local files
            _statusCallback?.Invoke("Scanning local files...");
            cancellationToken.ThrowIfCancellationRequested();
            
            // Check if local path exists before trying to scan
            if (!Directory.Exists(_localPath))
            {
                var errorMsg = $"Local folder doesn't exist: {_localPath}";
                Log(errorMsg);
                _statusCallback?.Invoke(errorMsg);
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                return SyncRecommendation.Unknown;
            }

            var localFiles = GetLocalFileMetadata();
            if (localFiles.Count == 0)
            {
                Log("No local files found");
                _statusCallback?.Invoke("No local files found");
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = "No local files found"
                };
                return SyncRecommendation.Unknown;
            }

            _statusCallback?.Invoke($"Found {localFiles.Count} local files. Connecting to FTP...");
            cancellationToken.ThrowIfCancellationRequested();

            // Connect to FTP and get remote files
            try
            {
                var remoteFiles = await GetRemoteFileMetadataAsync(cancellationToken);
                
                _statusCallback?.Invoke($"Found {remoteFiles.Count} remote files. Comparing...");
                cancellationToken.ThrowIfCancellationRequested();

                // Compare using timestamp and size
                var result = CompareFileMetadata(localFiles, remoteFiles, cancellationToken);
                LastComparisonResult = result;
                _statusCallback?.Invoke("Analysis complete");
                return result.Recommendation;
            }
            catch (Exception ftpEx)
            {
                Log($"FTP connection error: {ftpEx.Message}");
                var errorMsg = $"Couldn't connect to FTP: {ftpEx.Message}";
                _statusCallback?.Invoke(errorMsg);
                
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                return SyncRecommendation.Unknown;
            }
            finally
            {
                // Clean up SSH connection if it exists
                CleanupSshConnection();
            }
        }
        catch (OperationCanceledException)
        {
            Log("Sync recommendation analysis was canceled");
            throw;
        }
        catch (Exception ex)
        {
            Log($"Error getting sync recommendation: {ex.Message}");
            _statusCallback?.Invoke($"Error: {ex.Message}");
            LastComparisonResult = new ComparisonResult
            {
                Recommendation = SyncRecommendation.Unknown,
                Error = ex.Message
            };
            return SyncRecommendation.Unknown;
        }
    }

    /// <summary>
    /// Compares git commit timestamps between local and remote repositories
    /// </summary>
    private async Task<SyncRecommendation> CompareGitCommitsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _statusCallback?.Invoke("Getting local git commit timestamp...");
            cancellationToken.ThrowIfCancellationRequested();

            // Get local commit timestamp
            var localCommitTime = GetLocalGitCommitTimestamp();
            if (localCommitTime == null)
            {
                var errorMsg = "Failed to read local git commit timestamp";
                Log(errorMsg);
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                _statusCallback?.Invoke(errorMsg);
                return SyncRecommendation.Unknown;
            }

            _statusCallback?.Invoke("Getting remote git commit timestamp...");
            cancellationToken.ThrowIfCancellationRequested();

            // Get remote commit timestamp via SSH
            var remoteCommitTime = await GetRemoteGitCommitTimestampAsync(cancellationToken);
            if (remoteCommitTime == null)
            {
                var errorMsg = "Failed to read remote git commit timestamp.\n\nPossible causes:\n• Git not installed on remote server\n• Remote path is not in a git repository\n• SSH connection failed\n\nCheck the detailed logs for more information.";
                Log(errorMsg);
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                _statusCallback?.Invoke(errorMsg);
                return SyncRecommendation.Unknown;
            }

            Log($"Local commit: {localCommitTime:yyyy-MM-dd HH:mm:ss}");
            Log($"Remote commit: {remoteCommitTime:yyyy-MM-dd HH:mm:ss}");

            var timeDiff = localCommitTime.Value - remoteCommitTime.Value;
            
            var result = new ComparisonResult
            {
                Recommendation = SyncRecommendation.Unknown,
                NewerLocalFiles = new(),
                NewerRemoteFiles = new(),
                LocalOnlyFiles = new(),
                RemoteOnlyFiles = new()
            };

            if (timeDiff > TimeSpan.Zero)
            {
                result.Recommendation = SyncRecommendation.SyncToFtp;
                result.NewerLocalFiles.Add($"Local repository (commit: {localCommitTime:yyyy-MM-dd HH:mm:ss})");
            }
            else if (timeDiff < TimeSpan.Zero)
            {
                result.Recommendation = SyncRecommendation.SyncToLocal;
                result.NewerRemoteFiles.Add($"Remote repository (commit: {remoteCommitTime:yyyy-MM-dd HH:mm:ss})");
            }
            else
            {
                result.Recommendation = SyncRecommendation.InSync;
            }

            LastComparisonResult = result;
            _statusCallback?.Invoke("Git comparison complete");
            return result.Recommendation;
        }
        catch (Exception ex)
        {
            Log($"Error comparing git commits: {ex.Message}");
            _statusCallback?.Invoke($"Error: {ex.Message}");
            LastComparisonResult = new ComparisonResult
            {
                Recommendation = SyncRecommendation.Unknown,
                Error = ex.Message
            };
            return SyncRecommendation.Unknown;
        }
        finally
        {
            CleanupSshConnection();
        }
    }

    /// <summary>
    /// Gets the local git repository's latest commit timestamp
    /// </summary>
    private DateTime? GetLocalGitCommitTimestamp()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "-C \"" + _localPath + "\" log -1 --format=%cI",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(psi))
            {
                if (process == null)
                    return null;

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Log($"Git command failed: {process.StandardError.ReadToEnd()}");
                    return null;
                }

                if (string.IsNullOrEmpty(output))
                    return null;

                // Parse ISO 8601 format (e.g., "2025-10-23T15:30:22+02:00")
                if (DateTime.TryParse(output, out var commitTime))
                {
                    return commitTime.ToUniversalTime();
                }

                return null;
            }
        }
        catch (Exception ex)
        {
            Log($"Error getting local git commit timestamp: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the remote git repository's latest commit timestamp via SSH
    /// </summary>
    private async Task<DateTime?> GetRemoteGitCommitTimestampAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using (var sshClient = new SshClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password))
            {
                await Task.Run(() => sshClient.Connect(), cancellationToken);
                
                try
                {
                    // Try different possible git paths
                    string[] gitPaths = { "git", "/usr/bin/git", "/usr/local/bin/git", "/opt/git/bin/git" };
                    var gitErrors = new List<string>();
                    
                    foreach (var gitPath in gitPaths)
                    {
                        // Get the git root directory (always has .git in root, not in the remote path)
                        var gitRootCmd = $"cd {_remotePath} && {gitPath} rev-parse --show-toplevel";
                        var rootResult = sshClient.RunCommand(gitRootCmd);
                        
                        if (rootResult.ExitStatus == 0)
                        {
                            var gitRoot = rootResult.Result.Trim();
                            
                            // Now get the commit timestamp from the git root
                            var cmd = $"cd {gitRoot} && {gitPath} log -1 --format=%cI";
                            var result = sshClient.RunCommand(cmd);

                            if (result.ExitStatus == 0 && !string.IsNullOrEmpty(result.Result.Trim()))
                            {
                                var output = result.Result.Trim();
                                
                                // Parse ISO 8601 format
                                if (DateTime.TryParse(output, out var commitTime))
                                {
                                    Log($"Remote git commit: {output} (from {gitPath})");
                                    return commitTime.ToUniversalTime();
                                }
                                else
                                {
                                    gitErrors.Add($"{gitPath}: Failed to parse timestamp '{output}'");
                                }
                            }
                            else
                            {
                                gitErrors.Add($"{gitPath}: Command failed with exit code {result.ExitStatus}. Error: {result.Error?.Trim()}");
                            }
                        }
                        else
                        {
                            gitErrors.Add($"{gitPath}: {rootResult.Error?.Trim() ?? "command not found"}");
                        }
                    }

                    var errorDetails = string.Join(" | ", gitErrors);
                    Log($"Git not found at any of the standard paths. Details: {errorDetails}");
                    return null;
                }
                finally
                {
                    sshClient.Disconnect();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error getting remote git commit timestamp: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Cleans up the SSH connection cache
    /// </summary>
    private void CleanupSshConnection()
    {
        if (_sshClientCache != null)
        {
            try
            {
                if (_sshClientCache.IsConnected)
                {
                    Log($"SSH: Disconnecting SSH client");
                    _sshClientCache.Disconnect();
                    Log($"SSH: SSH client disconnected");
                }
                _sshClientCache.Dispose();
                Log($"SSH: SSH client disposed");
            }
            catch (Exception ex)
            {
                Log($"SSH: Error during cleanup - {ex.Message}");
            }
            finally
            {
                _sshClientCache = null;
            }
        }

        // Clean up connection pool
        foreach (var client in _sshConnectionPool)
        {
            try
            {
                if (client.IsConnected)
                    client.Disconnect();
                client.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        _sshConnectionPool.Clear();
        
        // Dispose semaphores
        _sshPoolSemaphore?.Dispose();
        _sshCommandSemaphore?.Dispose();
        
        Log($"SSH: Connection pool cleaned up");
    }

    /// <summary>
    /// Gets or creates an SSH connection from the pool
    /// Semaphore is released immediately - it only controls pool creation, not command execution
    /// </summary>
    private async Task<SshClient> GetSshConnectionAsync()
    {
        SshClient? client = null;
        
        await _sshPoolSemaphore.WaitAsync();
        
        try
        {
            lock (_sshConnectionPool)
            {
                // Try to find an existing connected client
                var existingClient = _sshConnectionPool.FirstOrDefault(c => c.IsConnected);
                if (existingClient != null)
                {
                    // Log($"SSH: Reusing existing connection from pool (pool size: {_sshConnectionPool.Count})");
                    return existingClient;
                }
            }

            // Create new connection
            Log($"SSH: Creating new parallel SSH connection to {_ftpConfig.Host}:{_ftpConfig.Port}");
            client = new SshClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
            
            try
            {
                client.Connect();
                // Log($"SSH: New parallel connection established (pool size: {_sshConnectionPool.Count + 1})");
                
                lock (_sshConnectionPool)
                {
                    _sshConnectionPool.Add(client);
                }
                
                return client;
            }
            catch (Exception connectEx)
            {
                Log($"SSH: Failed to create parallel connection - {connectEx.Message}");
                client.Dispose();
                throw;
            }
        }
        finally
        {
            // Release semaphore immediately so other tasks can get connections
            // The semaphore is just for controlling concurrent connection creation, not command execution
            _sshPoolSemaphore.Release();
        }
    }

    /// <summary>
    /// Returns an SSH connection back to the pool (connection stays open and is reused)
    /// </summary>
    private void ReturnSshConnection(SshClient client)
    {
        // Connection stays in pool for reuse - already released semaphore in GetSshConnectionAsync finally block
        // Log($"SSH: Connection returned to pool for reuse (pool size: {_sshConnectionPool.Count})");
    }

    /// <summary>
    /// Gets local file metadata (hash and timestamp) recursively, excluding ignored folders
    /// </summary>
    private Dictionary<string, FileMetadata> GetLocalFileMetadata()
    {
        var files = new Dictionary<string, FileMetadata>();

        try
        {
            var dir = new DirectoryInfo(_localPath);
            int maxDepth = _analysisMode == AnalysisMode.Quick ? MaxDepthFastMode : MaxDepthNormalMode;
            var fileInfos = GetFilesRecursive(dir, currentDepth: 0, maxDepth: maxDepth);

            if (_analysisMode == AnalysisMode.Quick && fileInfos.Count > 0)
            {
                Log($"Quick mode enabled: Analyzing only {fileInfos.Count} files (root + 1 level deep)");
            }

            // Collect file metadata (size and timestamp only, no hashing)
            foreach (var file in fileInfos)
            {
                // Skip files with excluded extensions
                if (IsFileExcluded(file.Name))
                {
                    continue;
                }

                // Use the relative path from localPath as the key, normalized to forward slashes for comparison
                var relativePath = Path.GetRelativePath(_localPath, file.FullName);
                var normalizedPath = relativePath.Replace('\\', '/');
                
                files[normalizedPath] = new FileMetadata 
                { 
                    Timestamp = file.LastWriteTimeUtc,
                    Size = file.Length
                };
            }

            Log($"Found {files.Count} local files");
        }
        catch (Exception ex)
        {
            Log($"Error reading local files: {ex.Message}");
        }

        return files;
    }

    /// <summary>
    /// Recursively gets files while skipping ignored folders
    /// </summary>
    private List<FileInfo> GetFilesRecursive(DirectoryInfo directory, int currentDepth = 0, int maxDepth = int.MaxValue)
    {
        var files = new List<FileInfo>();

        try
        {
            // Get files in current directory
            files.AddRange(directory.GetFiles());

            // Stop recursing if we've reached max depth
            if (currentDepth >= maxDepth)
            {
                return files;
            }

            // Get subdirectories and recurse, but skip ignored ones
            var subdirs = directory.GetDirectories();
            foreach (var subdir in subdirs)
            {
                if (!_ignoredFolders.Contains(subdir.Name, StringComparer.OrdinalIgnoreCase))
                {
                    files.AddRange(GetFilesRecursive(subdir, currentDepth + 1, maxDepth));
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error scanning directory {directory.FullName}: {ex.Message}");
        }

        return files;
    }

    /// <summary>
    /// Gets remote file metadata (hash and timestamp) recursively via FTP
    /// </summary>
    private async Task<Dictionary<string, FileMetadata>> GetRemoteFileMetadataAsync(CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, FileMetadata>();

        using (var client = new SftpClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password))
        {
            Log($"SFTP: Connecting to {_ftpConfig.Host}:{_ftpConfig.Port} as {_ftpConfig.Username}");
            try
            {
                client.Connect();
                Log($"SFTP: Successfully connected to {_ftpConfig.Host}");
            }
            catch (Exception connectEx)
            {
                Log($"SFTP: Connection failed - {connectEx.GetType().Name}: {connectEx.Message}");
                throw;
            }

            // Recursively get all files from the remote path
            Log($"SFTP: Starting file enumeration from: {_remotePath}");
            if (_analysisMode == AnalysisMode.Quick)
            {
                Log($"SFTP: Quick mode enabled - limiting analysis to root and 1 level deep");
            }
            try
            {
                int maxDepth = _analysisMode == AnalysisMode.Quick ? MaxDepthFastMode : MaxDepthNormalMode;
                await GetRemoteFilesRecursiveAsync(client, _remotePath, "", files, currentDepth: 0, maxDepth: maxDepth, cancellationToken: cancellationToken);
                Log($"SFTP: File enumeration completed. Found {files.Count} files total");
            }
            catch (Exception enumEx)
            {
                Log($"SFTP: Error during file enumeration - {enumEx.Message}");
                throw;
            }

            Log("SFTP: Disconnecting from server");
            try
            {
                client.Disconnect();
                Log($"SFTP: Disconnected successfully");
            }
            catch (Exception disconnectEx)
            {
                Log($"SFTP: Error during disconnect - {disconnectEx.Message}");
            }
            
            Log($"SFTP: Found {files.Count} remote files");
        }

        return files;
    }

    /// <summary>
    /// Recursively gets files with metadata from a remote SFTP directory (for hybrid comparison)
    /// Uses parallel SSH connections for faster hash computation
    /// </summary>
    private async Task GetRemoteFilesRecursiveAsync(SftpClient client, string remotePath, string relativePath, Dictionary<string, FileMetadata> files, int currentDepth = 0, int maxDepth = int.MaxValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        Log($"SFTP: Listing directory: {remotePath}");
        var items = client.ListDirectory(remotePath);
        var itemList = items.ToList();
        var nonDotCount = itemList.Count(x => !x.Name.StartsWith("."));
        Log($"SFTP: Found {nonDotCount} item{(nonDotCount != 1 ? "s" : "")} in {remotePath}");

        foreach (var item in itemList)
        {
            // Skip . and ..
            if (item.Name == "." || item.Name == "..")
                continue;

            try
            {
                if (item.IsDirectory)
                {
                    // Check if folder should be ignored
                    if (!_ignoredFolders.Contains(item.Name))
                    {
                        // Stop recursing if we've reached max depth
                        if (currentDepth >= maxDepth)
                        {
                            Log($"SFTP: Depth limit reached ({currentDepth}), stopping recursion into: {item.Name}");
                            continue;
                        }

                        Log($"SFTP: Recursing into directory: {item.FullName}");
                        // Recurse into subdirectories
                        var subPath = string.IsNullOrEmpty(relativePath) ? item.Name : $"{relativePath}/{item.Name}";
                        await GetRemoteFilesRecursiveAsync(client, item.FullName, subPath, files, currentDepth + 1, maxDepth, cancellationToken);
                    }
                    else
                    {
                        Log($"SFTP: Skipping ignored directory: {item.Name}");
                    }
                }
                else if (item.IsRegularFile)
                {
                    // Skip files with excluded extensions
                    if (IsFileExcluded(item.Name))
                    {
                        continue;
                    }

                    var key = string.IsNullOrEmpty(relativePath) ? item.Name : $"{relativePath}/{item.Name}";
                    // Store metadata: timestamp and size for fast comparison
                    files[key] = new FileMetadata 
                    { 
                        Timestamp = DateTime.SpecifyKind(item.LastWriteTime, DateTimeKind.Utc),
                        Size = item.Attributes.Size
                    };
                }
            }
            catch (Exception ex)
            {
                Log($"SFTP: Error processing item {remotePath}/{item.Name}: {ex.Message}");
                // Continue with next item even if one fails
            }
        }
    }

    /// <summary>
    /// Compares local and remote file timestamps to determine sync direction
    /// </summary>
    /// <summary>

    /// <summary>
    /// Computes the MD5 hash of a file on the local filesystem
    /// </summary>
    /// <summary>
    /// Escapes a file path for use in shell commands
    /// </summary>
    private string EscapeShellPath(string path)
    {
        // Simple escaping: wrap in single quotes and escape single quotes within
        return "'" + path.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Compares local and remote file metadata using timestamp and size:
    /// - Same timestamp + size = files in sync
    /// - Different = compare timestamps to determine newer version
    /// </summary>
    private ComparisonResult CompareFileMetadata(Dictionary<string, FileMetadata> localFiles, Dictionary<string, FileMetadata> remoteFiles, CancellationToken cancellationToken = default)
    {
        const int DecisionThreshold = 3; // If we find 3 files newer on one side, decide direction immediately
        
        var newerLocal = 0;
        var newerRemote = 0;
        var localOnly = 0;
        var remoteOnly = 0;
        string localExample = "";
        string remoteExample = "";

        var newerLocalFiles = new List<string>();
        var newerRemoteFiles = new List<string>();
        var localOnlyFiles = new List<string>();
        var remoteOnlyFiles = new List<string>();
        
        bool decidedEarly = false;
        SyncRecommendation? earlyDecision = null;

        // Check all files that exist locally
        foreach (var localFile in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (remoteFiles.TryGetValue(localFile.Key, out var remoteMetadata))
            {
                // File exists on both sides
                // Compare using timestamp and size
                var timeDiff = localFile.Value.Timestamp - remoteMetadata.Timestamp;
                var sizeDiff = localFile.Value.Size - remoteMetadata.Size;
                
                // If both timestamp and size are identical, file is in sync
                if (timeDiff == TimeSpan.Zero && sizeDiff == 0)
                {
                    // File is in sync
                    continue;
                }
                
                // File differs - determine which is newer based on timestamp
                if (timeDiff > TimeSpan.Zero)
                {
                    // Local is newer
                    newerLocal++;
                    newerLocalFiles.Add(localFile.Key);
                    if (string.IsNullOrEmpty(localExample))
                    {
                        localExample = $"Local: {localFile.Key} {localFile.Value.Timestamp:yyyy-MM-dd HH:mm:ss} ({localFile.Value.Size} bytes), FTP: {remoteMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} ({remoteMetadata.Size} bytes)";
                    }
                    
                    // Quick mode: if we found 3 files newer locally, decide to sync to FTP
                    if (_analysisMode == AnalysisMode.Quick && newerLocal >= DecisionThreshold && newerRemote == 0 && localOnly == 0)
                    {
                        Log($"\n[QUICK MODE] Found {DecisionThreshold} files newer locally - deciding to sync to FTP");
                        decidedEarly = true;
                        earlyDecision = SyncRecommendation.SyncToFtp;
                        break;
                    }
                }
                else if (timeDiff < TimeSpan.Zero)
                {
                    // Remote is newer
                    newerRemote++;
                    newerRemoteFiles.Add(localFile.Key);
                    if (string.IsNullOrEmpty(remoteExample))
                    {
                        remoteExample = $"FTP: {localFile.Key} {remoteMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} ({remoteMetadata.Size} bytes), Local: {localFile.Value.Timestamp:yyyy-MM-dd HH:mm:ss} ({localFile.Value.Size} bytes)";
                    }
                    
                    // Quick mode: if we found 3 files newer remotely, decide to sync to local
                    if (_analysisMode == AnalysisMode.Quick && newerRemote >= DecisionThreshold && newerLocal == 0 && remoteOnly == 0)
                    {
                        Log($"\n[QUICK MODE] Found {DecisionThreshold} files newer on FTP - deciding to sync to local");
                        decidedEarly = true;
                        earlyDecision = SyncRecommendation.SyncToLocal;
                        break;
                    }
                }
                else
                {
                    // Same timestamp but different size - flag as conflict
                    newerLocal++;
                    newerLocalFiles.Add(localFile.Key);
                    if (string.IsNullOrEmpty(localExample))
                    {
                        localExample = $"CONFLICT: {localFile.Key} (same timestamp, size differs: {localFile.Value.Size} vs {remoteMetadata.Size})";
                    }
                }
            }
            else
            {
                // File exists locally but not on remote
                localOnly++;
                localOnlyFiles.Add(localFile.Key);
                if (string.IsNullOrEmpty(localExample))
                {
                    localExample = $"Local only: {localFile.Key} {localFile.Value.Timestamp:yyyy-MM-dd HH:mm}";
                }
            }
        }

        // Check for files that exist only on remote (only if we haven't already decided)
        if (!decidedEarly)
        {
            foreach (var remoteFile in remoteFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                if (!localFiles.ContainsKey(remoteFile.Key))
                {
                    remoteOnly++;
                    remoteOnlyFiles.Add(remoteFile.Key);
                    if (string.IsNullOrEmpty(remoteExample))
                    {
                        remoteExample = $"FTP only: {remoteFile.Key} {remoteFile.Value.Timestamp:yyyy-MM-dd HH:mm}";
                    }
                }
            }
        }

        // Log detailed file lists
        Log("\n=== FILES NEWER LOCALLY ===");
        foreach (var file in newerLocalFiles.Take(20))
        {
            Log($"  - {file}");
        }
        if (newerLocalFiles.Count > 20)
        {
            Log($"  ... and {newerLocalFiles.Count - 20} more");
        }
        
        Log("\n=== FILES NEWER ON FTP ===");
        foreach (var file in newerRemoteFiles.Take(20))
        {
            Log($"  - {file}");
        }
        if (newerRemoteFiles.Count > 20)
        {
            Log($"  ... and {newerRemoteFiles.Count - 20} more");
        }
        
        Log("\n=== FILES ONLY PRESENT LOCALLY ===");
        foreach (var file in localOnlyFiles.Take(20))  // Limit to first 20 for readability
        {
            Log($"  - {file}");
        }
        if (localOnlyFiles.Count > 20)
        {
            Log($"  ... and {localOnlyFiles.Count - 20} more");
        }
        
        Log("\n=== FILES ONLY PRESENT ON FTP ===");
        foreach (var file in remoteOnlyFiles.Take(20))  // Limit to first 20 for readability
        {
            Log($"  - {file}");
        }
        if (remoteOnlyFiles.Count > 20)
        {
            Log($"  ... and {remoteOnlyFiles.Count - 20} more");
        }

        // Total count of files that need syncing from each direction
        var totalLocalNeedsSync = newerLocal + localOnly;
        var totalRemoteNeedsSync = newerRemote + remoteOnly;

        Log($"\nComparison: {newerLocal} files newer locally, {newerRemote} files newer remotely, {localOnly} files local-only, {remoteOnly} files remote-only");

        // Decision logic
        SyncRecommendation recommendation;
        if (decidedEarly && earlyDecision.HasValue)
        {
            // Use early decision from fast mode
            recommendation = earlyDecision.Value;
            Log($"\n[FAST MODE] Using early decision: {recommendation}");
        }
        else if (totalLocalNeedsSync == 0 && totalRemoteNeedsSync == 0)
            recommendation = SyncRecommendation.InSync;
        else if (totalLocalNeedsSync > 0 && totalRemoteNeedsSync == 0)
            recommendation = SyncRecommendation.SyncToFtp;
        else if (totalRemoteNeedsSync > 0 && totalLocalNeedsSync == 0)
            recommendation = SyncRecommendation.SyncToLocal;
        else
            // Both have changes - recommend the direction with more files to sync
            recommendation = totalLocalNeedsSync > totalRemoteNeedsSync ? SyncRecommendation.SyncToFtp : SyncRecommendation.SyncToLocal;

        return new ComparisonResult
        {
            Recommendation = recommendation,
            NewerLocalCount = newerLocal,
            NewerRemoteCount = newerRemote,
            LocalOnlyCount = localOnly,
            RemoteOnlyCount = remoteOnly,
            LocalExample = localExample,
            RemoteExample = remoteExample,
            NewerLocalFiles = newerLocalFiles,
            NewerRemoteFiles = newerRemoteFiles,
            LocalOnlyFiles = localOnlyFiles,
            RemoteOnlyFiles = remoteOnlyFiles
        };
    }

    /// <summary>
    /// Syncs files from local to FTP (upload)
    /// </summary>
    public async Task SyncToFtpAsync(Action<string>? progressCallback = null)
    {
        await Task.Run(() => SyncToFtp(progressCallback));
    }

    private void SyncToFtp(Action<string>? progressCallback = null)
    {
        try
        {
            using (var client = new SftpClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password))
            {
                Log("Connecting to SFTP server for upload...");
                client.Connect();
                Log("Connected to SFTP server");

                var filesToUpload = LastComparisonResult?.NewerLocalFiles ?? new();
                filesToUpload.AddRange(LastComparisonResult?.LocalOnlyFiles ?? new());

                var totalFiles = filesToUpload.Count;
                var uploadedCount = 0;

                Log($"Starting upload of {totalFiles} files to FTP");

                foreach (var relativeFile in filesToUpload)
                {
                    try
                    {
                        var localFile = Path.Combine(_localPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                        var remoteFile = _remotePath + "/" + relativeFile;

                        // Ensure remote directory exists
                        var remoteDir = Path.GetDirectoryName(remoteFile)?.Replace("\\", "/") ?? _remotePath;
                        EnsureRemoteDirectoryExists(client, remoteDir);

                        // Upload the file
                        if (File.Exists(localFile))
                        {
                            using (var fileStream = File.OpenRead(localFile))
                            {
                                client.UploadFile(fileStream, remoteFile, true);
                                uploadedCount++;
                                progressCallback?.Invoke($"Uploaded {uploadedCount}/{totalFiles}: {relativeFile}");
                                Log($"Uploaded: {relativeFile}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error uploading {relativeFile}: {ex.Message}");
                        progressCallback?.Invoke($"Error uploading {relativeFile}: {ex.Message}");
                    }
                }

                client.Disconnect();
                Log($"Upload complete: {uploadedCount}/{totalFiles} files");
                progressCallback?.Invoke($"✓ Upload complete: {uploadedCount}/{totalFiles} files");
            }
        }
        catch (Exception ex)
        {
            Log($"Sync to FTP error: {ex.Message}");
            progressCallback?.Invoke($"❌ Sync to FTP failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Syncs files from FTP to local (download)
    /// </summary>
    public async Task SyncToLocalAsync(Action<string>? progressCallback = null)
    {
        await Task.Run(() => SyncToLocal(progressCallback));
    }

    private void SyncToLocal(Action<string>? progressCallback = null)
    {
        try
        {
            using (var client = new SftpClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password))
            {
                Log("Connecting to SFTP server for download...");
                client.Connect();
                Log("Connected to SFTP server");

                var filesToDownload = LastComparisonResult?.NewerRemoteFiles ?? new();
                filesToDownload.AddRange(LastComparisonResult?.RemoteOnlyFiles ?? new());

                var totalFiles = filesToDownload.Count;
                var downloadedCount = 0;

                Log($"Starting download of {totalFiles} files from FTP");

                foreach (var relativeFile in filesToDownload)
                {
                    try
                    {
                        var localFile = Path.Combine(_localPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                        var remoteFile = _remotePath + "/" + relativeFile;

                        // Ensure local directory exists
                        var localDir = Path.GetDirectoryName(localFile);
                        if (localDir != null && !Directory.Exists(localDir))
                        {
                            Directory.CreateDirectory(localDir);
                        }

                        // Download the file
                        using (var fileStream = File.Create(localFile))
                        {
                            client.DownloadFile(remoteFile, fileStream);
                            downloadedCount++;
                            progressCallback?.Invoke($"Downloaded {downloadedCount}/{totalFiles}: {relativeFile}");
                            Log($"Downloaded: {relativeFile}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error downloading {relativeFile}: {ex.Message}");
                        progressCallback?.Invoke($"Error downloading {relativeFile}: {ex.Message}");
                    }
                }

                client.Disconnect();
                Log($"Download complete: {downloadedCount}/{totalFiles} files");
                progressCallback?.Invoke($"✓ Download complete: {downloadedCount}/{totalFiles} files");
            }
        }
        catch (Exception ex)
        {
            Log($"Sync to local error: {ex.Message}");
            progressCallback?.Invoke($"❌ Sync to local failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Ensures a remote directory exists, creating it if necessary
    /// </summary>
    private void EnsureRemoteDirectoryExists(SftpClient client, string remotePath)
    {
        try
        {
            if (!client.Exists(remotePath))
            {
                client.CreateDirectory(remotePath);
                Log($"Created remote directory: {remotePath}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error ensuring remote directory {remotePath}: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        var logPath = Path.Combine(exeDir, "wsync.log");

        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [FtpService] {message}\n");
        }
        catch { /* Ignore logging errors */ }

        System.Diagnostics.Debug.WriteLine($"[FtpService] {message}");
        
        // Also send to status callback for UI display
        if (!message.StartsWith("[FtpService]") && !message.StartsWith("==="))
        {
            _statusCallback?.Invoke(message);
        }
    }
}
