using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    public List<string> NewerLocalFiles { get; set; } = new();  // Source filenames for SyncToFtp (local)
    public List<string> NewerRemoteFiles { get; set; } = new();  // Source filenames for SyncToLocal (remote)
    public List<string> LocalOnlyFiles { get; set; } = new();   // Source filenames (local)
    public List<string> RemoteOnlyFiles { get; set; } = new();  // Source filenames (remote)
    
    // Maps for case-insensitive matching: maps lowercase path to actual source filename (for upload/download operations)
    public Dictionary<string, string> LocalFilenameMap { get; set; } = new();  // lowercase -> local filename
    public Dictionary<string, string> RemoteFilenameMap { get; set; } = new();  // lowercase -> remote filename
    public bool IsQuickModeEarlyDecision { get; set; } = false;
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
/// Holds git commit info result with optional error details
/// </summary>
internal class GitCommitInfo
{
    public DateTime? Timestamp { get; set; }
    public string? Hash { get; set; }
    public string? ErrorDetails { get; set; }
}

/// <summary>
/// Service for FTP operations and file comparison
/// </summary>
public class FtpService
{
    private readonly FtpConnectionConfig _ftpConfig;
    private readonly string _localPath;
    private readonly string _remotePath;
    private AnalysisMode _analysisMode;
    private Action<string>? _statusCallback;
    private readonly LinkedList<string> _uiLogBuffer = new();  // Circular buffer for UI logs (max 50)
    private const int MaxUiLogs = 50;
    private DateTime _lastStatusCallbackTime = DateTime.MinValue;  // Throttle status callbacks
    private const int StatusCallbackThrottleMs = 100;  // Max frequency of status callbacks to UI
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

    // Folders to NEVER sync - these are generated/build artifacts and cache folders
    // Combined with config excludedFoldersFromSync during initialization
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

    // Folders ignored only during ANALYSIS (for UI display purposes + user sync exclusions)
    // This combines: _defaultExcludedFolders + config.excludedFoldersFromAnalysis + config.excludedFoldersFromSync
    private List<string> _excludedFoldersFromAnalysis;
    
    // Folders to skip during sync operations
    // This is: _defaultExcludedFolders + config.excludedFoldersFromSync only
    private List<string> _excludedFoldersFromSync;

    public FtpService(FtpConnectionConfig ftpConfig, string localPath, string remotePath, List<string>? excludedExtensions = null, List<string>? excludedFoldersFromAnalysis = null, List<string>? excludedFoldersFromSync = null, AnalysisMode analysisMode = AnalysisMode.Full)
    {
        _ftpConfig = ftpConfig;
        _localPath = localPath;
        _remotePath = remotePath;
        _analysisMode = analysisMode;
        _excludedExtensions = excludedExtensions ?? new List<string>();

        // Build analysis ignore list: default + config analysis + config sync
        _excludedFoldersFromAnalysis = new List<string>(_defaultExcludedFolders);
        if (excludedFoldersFromAnalysis != null)
        {
            _excludedFoldersFromAnalysis.AddRange(excludedFoldersFromAnalysis);
        }
        if (excludedFoldersFromSync != null)
        {
            _excludedFoldersFromAnalysis.AddRange(excludedFoldersFromSync);
        }
        
        // Build sync ignore list: default + config sync only
        _excludedFoldersFromSync = new List<string>(_defaultExcludedFolders);
        if (excludedFoldersFromSync != null)
        {
            _excludedFoldersFromSync.AddRange(excludedFoldersFromSync);
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
                    UpdateStatus(errorMsg);
                    return SyncRecommendation.Unknown;
                }
                
                return await CompareGitCommitsAsync(cancellationToken);
            }

            // Get local files
            UpdateStatus("Scanning local files...");
            cancellationToken.ThrowIfCancellationRequested();
            
            // Check if local path exists before trying to scan
            if (!Directory.Exists(_localPath))
            {
                var errorMsg = $"Local folder doesn't exist: {_localPath}";
                Log(errorMsg);
                UpdateStatus(errorMsg);
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                return SyncRecommendation.Unknown;
            }

            var localFiles = await GetLocalFileMetadataAsync();
            if (localFiles.Count == 0)
            {
                Log("No local files found");
                UpdateStatus("No local files found");
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = "No local files found"
                };
                return SyncRecommendation.Unknown;
            }

            UpdateStatus($"Found {localFiles.Count} local files. Connecting to FTP...");
            cancellationToken.ThrowIfCancellationRequested();

            // Connect to FTP and get remote files
            try
            {
                var remoteFiles = await GetRemoteFileMetadataAsync(cancellationToken);
                
                UpdateStatus($"Found {remoteFiles.Count} remote files. Comparing...");
                cancellationToken.ThrowIfCancellationRequested();

                // Compare using timestamp and size
                var result = await CompareFileMetadataAsync(localFiles, remoteFiles, cancellationToken);
                LastComparisonResult = result;
                UpdateStatus("Analysis complete");
                return result.Recommendation;
            }
            catch (Exception ftpEx)
            {
                Log($"FTP connection error: {ftpEx.Message}");
                var errorMsg = $"Couldn't connect to FTP: {ftpEx.Message}";
                UpdateStatus(errorMsg);
                
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
            UpdateStatus($"Error: {ex.Message}");
            LastComparisonResult = new ComparisonResult
            {
                Recommendation = SyncRecommendation.Unknown,
                Error = ex.Message
            };
            return SyncRecommendation.Unknown;
        }
    }

    /// <summary>
    /// Forces a full analysis regardless of the configured analysis mode
    /// Used by sync operations to ensure all files are scanned
    /// </summary>
    private async Task<SyncRecommendation> GetFullAnalysisAsync(CancellationToken cancellationToken = default, bool includeGit = false)
    {
        try
        {
            // Temporarily override analysis mode to Full to ensure we analyze ALL files
            var savedMode = _analysisMode;
            _analysisMode = AnalysisMode.Full;
            
            // Temporarily remove .git from _excludedFoldersFromAnalysis if includeGit is true
            var savedExcludedFoldersFromAnalysis = _excludedFoldersFromAnalysis;
            if (includeGit)
            {
                _excludedFoldersFromAnalysis = new List<string>(_excludedFoldersFromAnalysis);
                _excludedFoldersFromAnalysis.Remove(".git");
            }
            
            try
            {
                var localFiles = await GetLocalFileMetadataAsync();
                if (localFiles.Count == 0)
                {
                    Log("No local files found for full analysis");
                    return SyncRecommendation.Unknown;
                }

                try
                {
                    var remoteFiles = await GetRemoteFileMetadataAsync(cancellationToken);
                    
                    // Compare using Full mode (all files)
                    var result = await CompareFileMetadataAsync(localFiles, remoteFiles, cancellationToken, AnalysisMode.Full);
                    LastComparisonResult = result;
                    return result.Recommendation;
                }
                catch (Exception ftpEx)
                {
                    Log($"FTP connection error during full analysis: {ftpEx.Message}");
                    return SyncRecommendation.Unknown;
                }
                finally
                {
                    CleanupSshConnection();
                }
            }
            finally
            {
                // Restore the original analysis mode and _excludedFoldersFromAnalysis
                _analysisMode = savedMode;
                if (includeGit)
                {
                    _excludedFoldersFromAnalysis = savedExcludedFoldersFromAnalysis;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error during full analysis: {ex.Message}");
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
            UpdateStatus("Getting local git commit info...");
            cancellationToken.ThrowIfCancellationRequested();

            // Get local commit info
            var (localCommitTime, localHash) = GetLocalGitCommitInfo();
            if (localCommitTime == null || string.IsNullOrEmpty(localHash))
            {
                var errorMsg = "Failed to read local git commit info";
                Log(errorMsg);
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                UpdateStatus(errorMsg);
                return SyncRecommendation.Unknown;
            }

            UpdateStatus("Getting remote git commit info...");
            cancellationToken.ThrowIfCancellationRequested();

            // Get remote commit info via SSH
            var gitInfo = await GetRemoteGitCommitInfoAsync(cancellationToken);
            if (gitInfo.Timestamp == null || string.IsNullOrEmpty(gitInfo.Hash))
            {
                // Build detailed error message
                var errorMsg = "Failed to read remote git commit timestamp.\n\n";
                
                if (!string.IsNullOrEmpty(gitInfo.ErrorDetails) && gitInfo.ErrorDetails.Contains("dubious ownership"))
                {
                    errorMsg += "🔒 GIT SECURITY ISSUE - Dubious Ownership Detected\n\n";
                    errorMsg += "The .git repository on the remote server is owned by a different user,\n";
                    errorMsg += "which is a Git security restriction (CVE-2022-24765).\n\n";
                    errorMsg += "To fix this, run the following command on the remote server:\n\n";
                    errorMsg += $"  git config --global --add safe.directory {_remotePath}\n\n";
                    errorMsg += "Then try syncing again.";
                }
                else
                {
                    errorMsg += "Possible causes:\n";
                    errorMsg += "• Git not installed on remote server\n";
                    errorMsg += "• Remote path is not in a git repository\n";
                    errorMsg += "• SSH connection failed\n";
                    errorMsg += "• Git repository ownership issue (dubious ownership)\n\n";
                    errorMsg += "Check the detailed logs for more information.";
                }
                
                Log(errorMsg);
                LastComparisonResult = new ComparisonResult
                {
                    Recommendation = SyncRecommendation.Unknown,
                    Error = errorMsg
                };
                UpdateStatus(errorMsg);
                return SyncRecommendation.Unknown;
            }

            Log($"Local commit: {localHash} {localCommitTime:yyyy-MM-dd HH:mm:ss}");
            Log($"Remote commit: {gitInfo.Hash} {gitInfo.Timestamp:yyyy-MM-dd HH:mm:ss}");

            var timeDiff = localCommitTime.Value - gitInfo.Timestamp.Value;
            
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
                var localHashShort = !string.IsNullOrEmpty(localHash) ? localHash.Substring(0, Math.Min(7, localHash.Length)) : "unknown";
                var remoteHashShort = !string.IsNullOrEmpty(gitInfo.Hash) ? gitInfo.Hash.Substring(0, Math.Min(7, gitInfo.Hash.Length)) : "unknown";
                result.NewerLocalFiles.Add($"Local (newer):  {localHashShort} {localCommitTime:yyyy-MM-dd HH:mm:ss}");
                result.NewerRemoteFiles.Add($"Remote (older): {remoteHashShort} {gitInfo.Timestamp:yyyy-MM-dd HH:mm:ss}");
            }
            else if (timeDiff < TimeSpan.Zero)
            {
                result.Recommendation = SyncRecommendation.SyncToLocal;
                var localHashShort = !string.IsNullOrEmpty(localHash) ? localHash.Substring(0, Math.Min(7, localHash.Length)) : "unknown";
                var remoteHashShort = !string.IsNullOrEmpty(gitInfo.Hash) ? gitInfo.Hash.Substring(0, Math.Min(7, gitInfo.Hash.Length)) : "unknown";
                result.NewerLocalFiles.Add($"Local (older):  {localHashShort} {localCommitTime:yyyy-MM-dd HH:mm:ss}");
                result.NewerRemoteFiles.Add($"Remote (newer): {remoteHashShort} {gitInfo.Timestamp:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                result.Recommendation = SyncRecommendation.InSync;
                var localHashShort = !string.IsNullOrEmpty(localHash) ? localHash.Substring(0, Math.Min(7, localHash.Length)) : "unknown";
                var remoteHashShort = !string.IsNullOrEmpty(gitInfo.Hash) ? gitInfo.Hash.Substring(0, Math.Min(7, gitInfo.Hash.Length)) : "unknown";
                result.NewerLocalFiles.Add($"Local (same):   {localHashShort} {localCommitTime:yyyy-MM-dd HH:mm:ss}");
                result.NewerRemoteFiles.Add($"Remote (same):  {remoteHashShort} {gitInfo.Timestamp:yyyy-MM-dd HH:mm:ss}");
            }

            LastComparisonResult = result;
            UpdateStatus("Git comparison complete");
            return result.Recommendation;
        }
        catch (Exception ex)
        {
            Log($"Error comparing git commits: {ex.Message}");
            UpdateStatus($"Error: {ex.Message}");
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
    /// Gets the local git repository's latest commit timestamp and hash
    /// </summary>
    private (DateTime? timestamp, string? hash) GetLocalGitCommitInfo()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "-C \"" + _localPath + "\" log -1 --format=%H%n%cI",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = System.Diagnostics.Process.Start(psi))
            {
                if (process == null)
                    return (null, null);

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Log($"Git command failed: {process.StandardError.ReadToEnd()}");
                    return (null, null);
                }

                var lines = output.Split('\n');
                if (lines.Length < 2)
                    return (null, null);

                var hash = lines[0].Trim();
                var timeStr = lines[1].Trim();

                // Parse ISO 8601 format (e.g., "2025-10-23T15:30:22+02:00")
                if (DateTime.TryParse(timeStr, out var commitTime))
                {
                    return (commitTime.ToUniversalTime(), hash);
                }

                return (null, null);
            }
        }
        catch (Exception ex)
        {
            Log($"Error getting local git commit info: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Gets the remote git repository's latest commit timestamp and hash via SSH
    /// </summary>
    private async Task<GitCommitInfo> GetRemoteGitCommitInfoAsync(CancellationToken cancellationToken = default)
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
                            
                            // Now get the commit hash and timestamp from the git root
                            var cmd = $"cd {gitRoot} && {gitPath} log -1 --format=%H%n%cI";
                            var result = sshClient.RunCommand(cmd);

                            if (result.ExitStatus == 0 && !string.IsNullOrEmpty(result.Result.Trim()))
                            {
                                var lines = result.Result.Trim().Split('\n');
                                Log($"Remote git result has {lines.Length} lines");
                                if (lines.Length >= 2)
                                {
                                    var hash = lines[0].Trim();
                                    var output = lines[1].Trim();
                                    
                                    Log($"Remote git hash: '{hash}', timestamp: '{output}'");
                                    
                                    // Parse ISO 8601 format
                                    if (DateTime.TryParse(output, out var commitTime))
                                    {
                                        Log($"Remote git commit: {hash} {output} (from {gitPath})");
                                        return new GitCommitInfo { Timestamp = commitTime.ToUniversalTime(), Hash = hash };
                                    }
                                    else
                                    {
                                        gitErrors.Add($"{gitPath}: Failed to parse timestamp '{output}'");
                                    }
                                }
                                else
                                {
                                    gitErrors.Add($"{gitPath}: Result had only {lines.Length} line(s): '{result.Result.Trim()}'");
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
                    
                    // Check for "dubious ownership" error - this is a specific git security issue
                    if (errorDetails.Contains("dubious ownership"))
                    {
                        Log($"\n⚠️  GIT SECURITY ISSUE: The .git repository is owned by a different user.");
                        Log($"On the remote server, run this command to fix it:");
                        Log($"  git config --global --add safe.directory {_remotePath}");
                    }
                    
                    return new GitCommitInfo { ErrorDetails = errorDetails };
                }
                finally
                {
                    sshClient.Disconnect();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error getting remote git commit info: {ex.Message}");
            return new GitCommitInfo { ErrorDetails = ex.Message };
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
    /// Async version that yields to UI thread while scanning local files
    /// </summary>
    private async Task<Dictionary<string, FileMetadata>> GetLocalFileMetadataAsync()
    {
        var files = new Dictionary<string, FileMetadata>();

        try
        {
            var dir = new DirectoryInfo(_localPath);
            int maxDepth = _analysisMode == AnalysisMode.Quick ? MaxDepthFastMode : MaxDepthNormalMode;
            var fileInfos = await GetFilesRecursiveAsync(dir, currentDepth: 0, maxDepth: maxDepth);

            if (_analysisMode == AnalysisMode.Quick && fileInfos.Count > 0)
            {
                Log($"Quick mode enabled: Analyzing only {fileInfos.Count} files (root + 1 level deep)");
            }

            // Collect file metadata (size and timestamp only, no hashing)
            int filesProcessed = 0;
            foreach (var file in fileInfos)
            {
                // Yield every 100 files
                if (filesProcessed > 0 && filesProcessed % 100 == 0)
                {
                    await Task.Delay(0);
                }
                filesProcessed++;

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
                    Timestamp = file.LastWriteTimeUtc,  // Always use UTC for consistent comparison
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
    /// Recursively gets files while skipping ignored folders - async version with yields
    /// </summary>
    private async Task<List<FileInfo>> GetFilesRecursiveAsync(DirectoryInfo directory, int currentDepth = 0, int maxDepth = int.MaxValue)
    {
        var files = new List<FileInfo>();

        try
        {
            // Yield to thread pool periodically
            if (currentDepth > 0 && currentDepth % 5 == 0)
            {
                await Task.Delay(0);
            }

            // Get files in current directory
            files.AddRange(directory.GetFiles());

            // Stop recursing if we've reached max depth
            if (currentDepth >= maxDepth)
            {
                return files;
            }

            // Get subdirectories and recurse (skip sync-ignored folders)
            var subdirs = directory.GetDirectories();
            foreach (var subdir in subdirs)
            {
                // Skip folders excluded from sync
                if (_excludedFoldersFromSync.Contains(subdir.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                files.AddRange(await GetFilesRecursiveAsync(subdir, currentDepth + 1, maxDepth));
            }
        }
        catch (Exception ex)
        {
            Log($"Error scanning directory {directory.FullName}: {ex.Message}");
        }

        return files;
    }

    /// <summary>
    /// Gets all files with relative paths and metadata for INDEPENDENT sync.
    /// Only excludes: excludedExtensions and excludedFoldersFromSync
    /// Does NOT use analysis-only exclusions (like .git, deployment/, public/)
    /// Returns Dictionary mapping relative paths to file metadata
    /// </summary>
    private async Task<Dictionary<string, (DateTime LastWriteTimeUtc, long Size)>> GetFilesRecursiveAsync(string rootPath, bool onlyExcludeSync = false)
    {
        var result = new Dictionary<string, (DateTime LastWriteTimeUtc, long Size)>();
        
        try
        {
            var rootDir = new DirectoryInfo(rootPath);
            if (!rootDir.Exists)
            {
                Log($"Directory not found: {rootPath}");
                return result;
            }
            
            Log($"Scanning local directory: {rootPath}");
            Log($"Excluded extensions: {string.Join(", ", _excludedExtensions)}");
            Log($"Excluded folders (sync): {string.Join(", ", _excludedFoldersFromSync)}");
            
            await ScanDirectoryRecursiveAsync(rootDir, "", result);
            
            Log($"Local scan complete: Found {result.Count} files");
        }
        catch (Exception ex)
        {
            Log($"Error scanning {rootPath}: {ex.Message}");
        }
        
        return result;
    }

    private async Task ScanDirectoryRecursiveAsync(DirectoryInfo dir, string relativePath, Dictionary<string, (DateTime, long)> result)
    {
        try
        {
            // Yield periodically
            await Task.Delay(0);
            
            // Get all files in this directory
            foreach (var file in dir.GetFiles())
            {
                // Skip excluded extensions
                if (_excludedExtensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                var relPath = string.IsNullOrEmpty(relativePath)
                    ? file.Name
                    : relativePath + "/" + file.Name;
                    
                result[relPath] = (file.LastWriteTimeUtc, file.Length);
            }
            
            // Recurse into subdirectories
            foreach (var subdir in dir.GetDirectories())
            {
                // Skip folders excluded from sync
                if (_excludedFoldersFromSync.Contains(subdir.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                var relPath = string.IsNullOrEmpty(relativePath)
                    ? subdir.Name
                    : relativePath + "/" + subdir.Name;
                    
                await ScanDirectoryRecursiveAsync(subdir, relPath, result);
            }
        }
        catch (Exception ex)
        {
            Log($"Error scanning directory {dir.FullName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets remote files for INDEPENDENT sync.
    /// Returns Dictionary mapping relative paths to file metadata
    /// </summary>
    private async Task<Dictionary<string, (DateTime LastWriteTimeUtc, long Size)>> GetRemoteFilesRecursiveAsync(string remotePath, bool onlyExcludeSync = false)
    {
        var result = new Dictionary<string, (DateTime LastWriteTimeUtc, long Size)>();
        
        try
        {
            var connectionInfo = new Renci.SshNet.ConnectionInfo(
                _ftpConfig.Host,
                _ftpConfig.Port,
                _ftpConfig.Username,
                new Renci.SshNet.PasswordAuthenticationMethod(_ftpConfig.Username, _ftpConfig.Password))
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            
            using (var client = new SftpClient(connectionInfo))
            {
                Log($"Scanning remote directory: {remotePath}");
                Log($"Excluded folders (sync): {string.Join(", ", _excludedFoldersFromSync)}");
                
                client.Connect();
                await ScanRemoteDirectoryRecursiveAsync(client, remotePath, "", result);
                client.Disconnect();
                
                Log($"Remote scan complete: Found {result.Count} files");
            }
        }
        catch (Exception ex)
        {
            Log($"Error scanning remote {remotePath}: {ex.Message}");
        }
        
        return result;
    }

    private async Task ScanRemoteDirectoryRecursiveAsync(SftpClient client, string remotePath, string relativePath, Dictionary<string, (DateTime, long)> result)
    {
        try
        {
            // Yield periodically
            await Task.Delay(0);
            
            var files = client.ListDirectory(remotePath);
            
            foreach (var fileAttr in files)
            {
                if (fileAttr.Name.StartsWith("."))
                    continue; // Skip . and ..
                    
                var itemPath = remotePath + "/" + fileAttr.Name;
                var relPath = string.IsNullOrEmpty(relativePath)
                    ? fileAttr.Name
                    : relativePath + "/" + fileAttr.Name;
                
                if (fileAttr.IsDirectory)
                {
                    // Skip folders excluded from sync
                    if (_excludedFoldersFromSync.Contains(fileAttr.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    await ScanRemoteDirectoryRecursiveAsync(client, itemPath, relPath, result);
                }
                else
                {
                    // Skip excluded extensions
                    var ext = Path.GetExtension(fileAttr.Name).TrimStart('.');
                    if (_excludedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    var mtime = fileAttr.LastWriteTime.ToUniversalTime();
                    result[relPath] = (mtime, fileAttr.Length);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error scanning remote directory {remotePath}: {ex.Message}");
        }
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
                    Timestamp = file.LastWriteTimeUtc,  // Always use UTC for consistent comparison
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

            // Get subdirectories and recurse, but skip _excludedFoldersFromAnalysis
            var subdirs = directory.GetDirectories();
            foreach (var subdir in subdirs)
            {
                if (!_excludedFoldersFromAnalysis.Contains(subdir.Name, StringComparer.OrdinalIgnoreCase))
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

        // Create connection info with optimized settings for packet handling
        var connectionInfo = new Renci.SshNet.ConnectionInfo(
            _ftpConfig.Host,
            _ftpConfig.Port,
            _ftpConfig.Username,
            new Renci.SshNet.PasswordAuthenticationMethod(_ftpConfig.Username, _ftpConfig.Password))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        using (var client = new SftpClient(connectionInfo))
        {
            // Note: The "Packet too big (68KB limit)" error was from SSH command scripts with 1000+ files.
            // Fixed by chunking SSH stat commands into 50-file batches below.
            // BufferSize here is for SFTP file transfers, not the issue.
            
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

        // Now get accurate UTC timestamps via SSH using chunked batch commands
        // Split into chunks to avoid exceeding server's 68KB packet limit
        Log("SSH: Getting accurate UTC timestamps for remote files via stat");
        using (var sshClient = new SshClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password))
        {
            await Task.Run(() => sshClient.Connect(), cancellationToken);
            try
            {
                const int FilesPerBatch = 50;  // Conservative limit to stay well under 68KB
                var fileList = files.Keys.ToList();
                int updated = 0;
                
                // Process files in batches to avoid packet size limit
                for (int batchStart = 0; batchStart < fileList.Count; batchStart += FilesPerBatch)
                {
                    var batchEnd = Math.Min(batchStart + FilesPerBatch, fileList.Count);
                    var batchFiles = fileList.GetRange(batchStart, batchEnd - batchStart);
                    
                    // Create script for this batch
                    var scriptLines = new List<string> { "cd " + _remotePath };
                    foreach (var fileKey in batchFiles)
                    {
                        var filePath = Path.Combine(_remotePath, fileKey).Replace('\\', '/').Replace("\"", "\\\"");
                        scriptLines.Add($"stat -c '%Y' \"{filePath}\" 2>/dev/null || stat -f '%m' \"{filePath}\" 2>/dev/null || echo 0");
                    }
                    
                    var script = string.Join("; ", scriptLines);
                    Log($"SSH: Processing batch {(batchStart / FilesPerBatch) + 1}: {batchFiles.Count} files, script size {script.Length} bytes");
                    
                    var result = sshClient.RunCommand(script);
                    
                    if (result.ExitStatus == 0)
                    {
                        var timestamps = result.Result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        int index = 0;
                        
                        foreach (var fileKey in batchFiles)
                        {
                            if (index < timestamps.Length && long.TryParse(timestamps[index].Trim(), out var unixTimestamp) && unixTimestamp > 0)
                            {
                                var utcTime = DateTime.UnixEpoch.AddSeconds(unixTimestamp);
                                files[fileKey].Timestamp = utcTime;
                                updated++;
                            }
                            index++;
                        }
                    }
                    else
                    {
                        Log($"SSH: Batch {(batchStart / FilesPerBatch) + 1} failed (exit {result.ExitStatus}), falling back to per-file queries for this batch");
                        // Fallback to per-file for this batch only
                        foreach (var fileKey in batchFiles)
                        {
                            var filePath = Path.Combine(_remotePath, fileKey).Replace('\\', '/');
                            var cmd = $"stat -c %Y \"{filePath}\" 2>/dev/null || stat -f %m \"{filePath}\" 2>/dev/null";
                            var singleResult = sshClient.RunCommand(cmd);
                            
                            if (singleResult.ExitStatus == 0 && long.TryParse(singleResult.Result.Trim(), out var unixTimestamp))
                            {
                                var utcTime = DateTime.UnixEpoch.AddSeconds(unixTimestamp);
                                files[fileKey].Timestamp = utcTime;
                                updated++;
                            }
                        }
                    }
                }
                
                Log($"SSH: Updated {updated}/{files.Count} file timestamps to UTC");
            }
            finally
            {
                sshClient.Disconnect();
            }
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
                    // Stop recursing if we've reached max depth
                    if (currentDepth >= maxDepth)
                    {
                        Log($"SFTP: Depth limit reached ({currentDepth}), stopping recursion into: {item.Name}");
                        continue;
                    }

                    // Skip sync-ignored folders
                    if (_excludedFoldersFromSync.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        Log($"SFTP: Skipping sync-ignored directory: {item.Name}");
                        continue;
                    }

                    Log($"SFTP: Recursing into directory: {item.FullName}");
                    // Recurse into subdirectories (skip sync-ignored folders)
                    var subPath = string.IsNullOrEmpty(relativePath) ? item.Name : $"{relativePath}/{item.Name}";
                    await GetRemoteFilesRecursiveAsync(client, item.FullName, subPath, files, currentDepth + 1, maxDepth, cancellationToken);
                }
                else if (item.IsRegularFile)
                {
                    // Skip files with excluded extensions
                    if (IsFileExcluded(item.Name))
                    {
                        continue;
                    }

                    var key = string.IsNullOrEmpty(relativePath) ? item.Name : $"{relativePath}/{item.Name}";
                    
                    // Store metadata with SFTP's local timestamp temporarily
                    // Will be corrected to UTC via SSH stat command
                    files[key] = new FileMetadata 
                    { 
                        Timestamp = item.LastWriteTime,  // Temporary, will be corrected via SSH
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
    /// Async wrapper for comparing file metadata - yields to UI thread periodically
    /// </summary>
    private async Task<ComparisonResult> CompareFileMetadataAsync(Dictionary<string, FileMetadata> localFiles, Dictionary<string, FileMetadata> remoteFiles, CancellationToken cancellationToken = default)
    {
        return await CompareFileMetadataAsync(localFiles, remoteFiles, cancellationToken, _analysisMode);
    }

    /// <summary>
    /// Async version that yields every 100 files to allow UI updates
    /// </summary>
    private async Task<ComparisonResult> CompareFileMetadataAsync(Dictionary<string, FileMetadata> localFiles, Dictionary<string, FileMetadata> remoteFiles, CancellationToken cancellationToken, AnalysisMode analysisMode)
    {
        const int DecisionThreshold = 3;
        const int YieldIntervalFiles = 100; // Yield to UI after comparing N files
        
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
        
        // Maps for case-sensitive filename preservation: lowercase key -> actual filename from source
        var localFilenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remoteFilenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        bool decidedEarly = false;
        SyncRecommendation? earlyDecision = null;
        int filesProcessed = 0;

        // Check all files that exist locally
        foreach (var localFile in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Yield to UI thread periodically
            if (filesProcessed > 0 && filesProcessed % YieldIntervalFiles == 0)
            {
                await Task.Delay(0);  // Yield to thread pool / UI scheduler
            }
            filesProcessed++;
            
            // Add to local filename map (preserve local case)
            var localNormalized = localFile.Key.ToLowerInvariant();
            if (!localFilenameMap.ContainsKey(localNormalized))
            {
                localFilenameMap[localNormalized] = localFile.Key;
            }
            
            // Try case-sensitive match first, then case-insensitive
            var remoteMetadata = null as FileMetadata;
            var remoteKey = localFile.Key;
            
            if (!remoteFiles.TryGetValue(localFile.Key, out remoteMetadata))
            {
                // Try case-insensitive match
                remoteKey = remoteFiles.Keys.FirstOrDefault(k => k.Equals(localFile.Key, StringComparison.OrdinalIgnoreCase));
                if (remoteKey != null)
                {
                    remoteFiles.TryGetValue(remoteKey, out remoteMetadata);
                    // Add to remote filename map (preserve remote case)
                    var remoteNormalized = remoteKey.ToLowerInvariant();
                    if (!remoteFilenameMap.ContainsKey(remoteNormalized))
                    {
                        remoteFilenameMap[remoteNormalized] = remoteKey;
                    }
                }
            }
            else
            {
                // Exact case match on remote - still add to map
                var remoteNormalized = localFile.Key.ToLowerInvariant();
                if (!remoteFilenameMap.ContainsKey(remoteNormalized))
                {
                    remoteFilenameMap[remoteNormalized] = localFile.Key;
                }
            }
            
            if (remoteMetadata != null)
            {
                // File exists on both sides (case-insensitive match)
                var sizeDiff = localFile.Value.Size - remoteMetadata.Size;
                var timeDiff = localFile.Value.Timestamp - remoteMetadata.Timestamp;
                
                LogDetailedToFileOnly($"\n{remoteKey}:");  // Use remote key to preserve correct case
                LogDetailedToFileOnly($"  - Local: Size {localFile.Value.Size:N0} bytes ({localFile.Value.Timestamp:yyyy-MM-dd HH:mm:ss})");
                LogDetailedToFileOnly($"  - Remote: Size {remoteMetadata.Size:N0} bytes ({remoteMetadata.Timestamp:yyyy-MM-dd HH:mm:ss})");
                
                // If sizes differ, files are definitely different
                if (sizeDiff != 0)
                {
                    LogDetailedToFileOnly($"  Status: DIFFERENT SIZE ({Math.Abs(sizeDiff):N0} bytes difference) - will be synced");
                    
                    // For size-different files, determine which side is newer based on timestamp
                    // and add to only the appropriate list to avoid confusing UI
                    if (timeDiff > TimeSpan.Zero)
                    {
                        // Local is newer (modified more recently)
                        newerLocal++;
                        newerLocalFiles.Add(remoteKey);
                        LogDetailedToFileOnly($"  → Local file is newer (modified {Math.Abs(timeDiff.TotalMinutes):F0}min after remote)");
                    }
                    else if (timeDiff < TimeSpan.Zero)
                    {
                        // Remote is newer (modified more recently)
                        newerRemote++;
                        newerRemoteFiles.Add(remoteKey);
                        LogDetailedToFileOnly($"  → Remote file is newer (modified {Math.Abs(timeDiff.TotalMinutes):F0}min after local)");
                    }
                    else
                    {
                        // Same timestamp, different size - unusual, but default to remote (safer)
                        newerRemote++;
                        newerRemoteFiles.Add(remoteKey);
                        LogDetailedToFileOnly($"  → Same timestamp but different size - favoring remote");
                    }
                    
                    if (string.IsNullOrEmpty(localExample))
                    {
                        localExample = $"Local: {remoteKey} - Size {localFile.Value.Size:N0} bytes, FTP: {remoteMetadata.Size:N0} bytes";
                    }
                    
                    if (analysisMode == AnalysisMode.Quick && newerLocal >= DecisionThreshold && localOnly == 0 && !decidedEarly)
                    {
                        Log($"\n[QUICK MODE] Found at least {DecisionThreshold} differing files - will sync");
                        decidedEarly = true;
                        // For Quick mode with different files, recommend based on git timestamps or default to FTP
                        earlyDecision = SyncRecommendation.SyncToFtp;
                        break;
                    }
                    continue;
                }
                
                // Sizes are identical - check if timestamp difference is just a timezone shift
                // Timezone differences are typically round numbers (1-4 hours)
                // Real file changes have irregular timestamps (3 minutes, 5 seconds, etc)
                var absTimeDiffHours = Math.Abs(timeDiff.TotalHours);
                var isLikelyTimezoneShift = IsRoundHourDifference(absTimeDiffHours);
                
                if (isLikelyTimezoneShift)
                {
                    LogDetailedToFileOnly($"  Status: IN SYNC (identical size, {absTimeDiffHours:F1}h timestamp difference - timezone shift)");
                    continue;
                }
                
                // Check if timestamp difference is within recent-sync tolerance
                // Files synced seconds apart should be treated as identical, not modified
                var absTimeDiffSeconds = Math.Abs(timeDiff.TotalSeconds);
                const double recentSyncToleranceSeconds = 5.0;  // 5 second tolerance for sync operations
                
                if (absTimeDiffSeconds <= recentSyncToleranceSeconds)
                {
                    LogDetailedToFileOnly($"  Status: IN SYNC (identical size, {absTimeDiffSeconds:F1}s timestamp difference - recent sync)");
                    continue;
                }
                
                // Sizes identical but timestamp differs by more than sync tolerance - file was modified
                if (timeDiff != TimeSpan.Zero)
                {
                    LogDetailedToFileOnly($"  Status: MODIFIED (identical size, {timeDiff.TotalMinutes:F0}min timestamp difference) - will be synced");
                    
                    // For modified files, add to the list corresponding to which version is newer
                    if (timeDiff > TimeSpan.Zero)
                    {
                        // Local is newer (modified more recently)
                        newerLocal++;
                        newerLocalFiles.Add(remoteKey);
                        LogDetailedToFileOnly($"  → Local version is newer ({timeDiff.TotalMinutes:F0}min more recent)");
                    }
                    else
                    {
                        // Remote is newer (modified more recently)
                        newerRemote++;
                        newerRemoteFiles.Add(remoteKey);
                        LogDetailedToFileOnly($"  → Remote version is newer ({Math.Abs(timeDiff.TotalMinutes):F0}min more recent)");
                    }
                    
                    if (string.IsNullOrEmpty(localExample))
                    {
                        localExample = $"Local: {remoteKey} - Modified {Math.Abs(timeDiff.TotalMinutes):F0}min ago vs remote";
                    }
                    
                    if (analysisMode == AnalysisMode.Quick && newerLocal >= DecisionThreshold && localOnly == 0 && !decidedEarly)
                    {
                        Log($"\n[QUICK MODE] Found at least {DecisionThreshold} modified files - will sync");
                        decidedEarly = true;
                        earlyDecision = SyncRecommendation.SyncToFtp;
                        break;
                    }
                    continue;
                }
                
                // Same size, same timestamp - definitely in sync
                LogDetailedToFileOnly($"  Status: IN SYNC (identical size and timestamp)");
            }
            else
            {
                localOnly++;
                localOnlyFiles.Add(localFile.Key);  // Use local key since file doesn't exist on remote
                if (string.IsNullOrEmpty(localExample))
                {
                    localExample = $"Local only: {localFile.Key} {localFile.Value.Timestamp:yyyy-MM-dd HH:mm}";
                }
            }
        }

        // Check for files that exist only on remote
        if (!decidedEarly)
        {
            foreach (var remoteFile in remoteFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                if (filesProcessed > 0 && filesProcessed % YieldIntervalFiles == 0)
                {
                    await Task.Delay(0);
                }
                filesProcessed++;
                
                // Add to remote filename map (preserve remote case)
                var remoteNormalized = remoteFile.Key.ToLowerInvariant();
                if (!remoteFilenameMap.ContainsKey(remoteNormalized))
                {
                    remoteFilenameMap[remoteNormalized] = remoteFile.Key;
                }
                
                // Check case-sensitive first, then case-insensitive
                var existsLocally = localFiles.ContainsKey(remoteFile.Key) ||
                    localFiles.Keys.Any(k => k.Equals(remoteFile.Key, StringComparison.OrdinalIgnoreCase));
                
                if (!existsLocally)
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

        // Log file lists
        Log("\n=== FILES DIFFERENT (SIZE MISMATCH) ===");
        // Files that have different sizes appear in both newerLocalFiles and newerRemoteFiles
        var differentSizeFiles = newerLocalFiles.Intersect(newerRemoteFiles).ToList();
        foreach (var file in differentSizeFiles.Take(20))
        {
            Log($"  • {file}");
        }
        if (differentSizeFiles.Count > 20)
        {
            Log($"  ... and {differentSizeFiles.Count - 20} more");
        }

        var uniqueLocalFiles = newerLocalFiles.Except(newerRemoteFiles).ToList();
        var uniqueRemoteFiles = newerRemoteFiles.Except(newerLocalFiles).ToList();
        
        Log("\n=== FILES NEWER LOCALLY (MODIFIED) ===");
        foreach (var file in uniqueLocalFiles.Take(20))
        {
            Log($"  • {file}");
        }
        if (uniqueLocalFiles.Count > 20)
        {
            Log($"  ... and {uniqueLocalFiles.Count - 20} more");
        }
        
        Log("\n=== FILES NEWER ON FTP (MODIFIED) ===");
        foreach (var file in uniqueRemoteFiles.Take(20))
        {
            Log($"  • {file}");
        }
        if (uniqueRemoteFiles.Count > 20)
        {
            Log($"  ... and {uniqueRemoteFiles.Count - 20} more");
        }
        
        Log("\n=== FILES ONLY PRESENT LOCALLY ===");
        foreach (var file in localOnlyFiles.Take(20))
        {
            Log($"  - {file}");
        }
        if (localOnlyFiles.Count > 20)
        {
            Log($"  ... and {localOnlyFiles.Count - 20} more");
        }
        
        Log("\n=== FILES ONLY PRESENT ON FTP ===");
        foreach (var file in remoteOnlyFiles.Take(20))
        {
            Log($"  - {file}");
        }
        if (remoteOnlyFiles.Count > 20)
        {
            Log($"  ... and {remoteOnlyFiles.Count - 20} more");
        }

        var totalLocalNeedsSync = newerLocal + localOnly;
        var totalRemoteNeedsSync = newerRemote + remoteOnly;

        Log($"\nComparison: {newerLocal} files newer locally, {newerRemote} files newer remotely, {localOnly} files local-only, {remoteOnly} files remote-only");

        SyncRecommendation recommendation;
        if (decidedEarly && earlyDecision.HasValue)
        {
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
            RemoteOnlyFiles = remoteOnlyFiles,
            LocalFilenameMap = localFilenameMap,
            RemoteFilenameMap = remoteFilenameMap
        };
    }

    /// <summary>
    /// Compares local and remote file metadata using timestamp and size:
    /// - Same timestamp + size = files in sync
    /// - Different = compare timestamps to determine newer version
    /// </summary>
    private ComparisonResult CompareFileMetadata(Dictionary<string, FileMetadata> localFiles, Dictionary<string, FileMetadata> remoteFiles, CancellationToken cancellationToken = default)
    {
        return CompareFileMetadata(localFiles, remoteFiles, cancellationToken, _analysisMode);
    }

    private ComparisonResult CompareFileMetadata(Dictionary<string, FileMetadata> localFiles, Dictionary<string, FileMetadata> remoteFiles, CancellationToken cancellationToken, AnalysisMode analysisMode)
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
                
                // Log detailed information for each file (only to file, not UI)
                LogDetailedToFileOnly($"\n{localFile.Key}:");
                LogDetailedToFileOnly($"  - Local: {localFile.Value.Timestamp:yyyy-MM-dd HH:mm:ss} - Size {localFile.Value.Size:N0} bytes");
                LogDetailedToFileOnly($"  - Remote: {remoteMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} - Size {remoteMetadata.Size:N0} bytes");
                
                // If both timestamp and size are identical, file is in sync
                if (timeDiff == TimeSpan.Zero && sizeDiff == 0)
                {
                    // File is in sync
                    LogDetailedToFileOnly($"  Status: IN SYNC");
                    continue;
                }
                
                // Same timestamp but different size - log as potential issue instead of conflict
                if (timeDiff == TimeSpan.Zero && sizeDiff != 0)
                {
                    LogDetailedToFileOnly($"  Status: POTENTIAL ISSUE - same timestamp but different sizes");
                    continue;
                }
                
                // File differs - determine which is newer based on timestamp
                if (timeDiff > TimeSpan.Zero)
                {
                    // Local is newer
                    LogDetailedToFileOnly($"  Status: LOCAL IS NEWER");
                    newerLocal++;
                    newerLocalFiles.Add(localFile.Key);
                    if (string.IsNullOrEmpty(localExample))
                    {
                        localExample = $"Local: {localFile.Key} {localFile.Value.Timestamp:yyyy-MM-dd HH:mm:ss} ({localFile.Value.Size} bytes), FTP: {remoteMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} ({remoteMetadata.Size} bytes)";
                    }
                    
                    // Quick mode: if we found 3 files newer locally, decide to sync to FTP
                    // NOTE: Don't break here - we need to continue analyzing all files for syncing
                    if (analysisMode == AnalysisMode.Quick && newerLocal >= DecisionThreshold && newerRemote == 0 && localOnly == 0 && !decidedEarly)
                    {
                        Log($"\n[QUICK MODE] Found at least {DecisionThreshold} files newer locally - will sync to FTP");
                        decidedEarly = true;
                        earlyDecision = SyncRecommendation.SyncToFtp;
                        break;
                    }
                }
                else if (timeDiff < TimeSpan.Zero)
                {
                    // Remote is newer
                    LogDetailedToFileOnly($"  Status: REMOTE IS NEWER");
                    newerRemote++;
                    newerRemoteFiles.Add(localFile.Key);
                    if (string.IsNullOrEmpty(remoteExample))
                    {
                        remoteExample = $"FTP: {localFile.Key} {remoteMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} ({remoteMetadata.Size} bytes), Local: {localFile.Value.Timestamp:yyyy-MM-dd HH:mm:ss} ({localFile.Value.Size} bytes)";
                    }
                    
                    // Quick mode: if we found 3 files newer remotely, decide to sync to local
                    // NOTE: Don't break here - we need to continue analyzing all files for syncing
                    if (analysisMode == AnalysisMode.Quick && newerRemote >= DecisionThreshold && newerLocal == 0 && remoteOnly == 0 && !decidedEarly)
                    {
                        Log($"\n[QUICK MODE] Found at least {DecisionThreshold} files newer on FTP - will sync to local");
                        decidedEarly = true;
                        earlyDecision = SyncRecommendation.SyncToLocal;
                        break;
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
            RemoteOnlyFiles = remoteOnlyFiles,
            IsQuickModeEarlyDecision = decidedEarly
        };
    }

    /// <summary>
    /// Syncs files from local to FTP (upload)
    /// </summary>
    public async Task SyncToFtpAsync(Action<string>? progressCallback = null)
    {
        await SyncToFtp(progressCallback);
    }

    private async Task SyncToFtp(Action<string>? progressCallback = null)
    {
        Log("SyncToFtp method called - INDEPENDENT sync (not using analysis results)");
        try
        {
            // Run SFTP operations on thread pool to avoid blocking UI thread
            await Task.Run(async () =>
            {
                // INDEPENDENT: Scan local and remote filesystems directly
                // Only exclude: extensions from excludedExtensions + folders from excludedFoldersFromSync
                Log("Scanning local filesystem for files to upload...");
                var localFiles = await GetFilesRecursiveAsync(_localPath, onlyExcludeSync: true);
                
                Log("Scanning remote filesystem...");
                var remoteFiles = await GetRemoteFilesRecursiveAsync(_remotePath, onlyExcludeSync: true);
                
                // Determine what to upload: files that are newer locally or only exist locally
                var filesToUpload = new List<string>();
                var remoteOnlyFiles = new List<string>();
                
                Log("Comparing local and remote files...");
                var remoteSet = new HashSet<string>(remoteFiles.Keys);
                
                foreach (var localFile in localFiles)
                {
                    var relPath = localFile.Key;
                    var localInfo = localFile.Value;
                    
                    if (remoteSet.Contains(relPath))
                    {
                        // File exists on both sides - check if local is newer
                        var remoteInfo = remoteFiles[relPath];
                        if (localInfo.LastWriteTimeUtc > remoteInfo.LastWriteTimeUtc)
                        {
                            filesToUpload.Add(relPath);
                        }
                    }
                    else
                    {
                        // File only exists locally
                        filesToUpload.Add(relPath);
                    }
                }
                
                // Remote-only files should be deleted to mirror local
                foreach (var remoteFile in remoteFiles)
                {
                    if (!localFiles.ContainsKey(remoteFile.Key))
                    {
                        remoteOnlyFiles.Add(remoteFile.Key);
                    }
                }
                
                Log($"Upload plan: {filesToUpload.Count} files to upload, {remoteOnlyFiles.Count} files to delete on remote");

                // Create connection info with optimized buffer sizes
                var connectionInfo = new Renci.SshNet.ConnectionInfo(
                    _ftpConfig.Host,
                    _ftpConfig.Port,
                    _ftpConfig.Username,
                    new Renci.SshNet.PasswordAuthenticationMethod(_ftpConfig.Username, _ftpConfig.Password))
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                using (var client = new SftpClient(connectionInfo))
                {
                    Log("Connecting to SFTP server for upload...");
                    client.Connect();
                    Log("Connected to SFTP server");

                    var totalFiles = filesToUpload.Count;
                    var uploadedCount = 0;
                    var uploadedFileTimestamps = new List<(string remotePath, DateTime utcTime)>();

                    Log($"Starting upload of {totalFiles} files to FTP");
                    
                    // Log which files are being uploaded, especially .git files
                    var gitFiles = filesToUpload.Where(f => f.Contains(".git")).ToList();
                    if (gitFiles.Count > 0)
                    {
                        Log($"  - Found {gitFiles.Count} .git files to upload");
                        foreach (var gitFile in gitFiles.Take(10))
                        {
                            Log($"    • {gitFile}");
                        }
                        if (gitFiles.Count > 10)
                        {
                            Log($"    ... and {gitFiles.Count - 10} more .git files");
                        }
                    }

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
                                var localFileInfo = new FileInfo(localFile);
                                using (var fileStream = File.OpenRead(localFile))
                                {
                                    client.UploadFile(fileStream, remoteFile, true);
                                    uploadedFileTimestamps.Add((remoteFile, localFileInfo.LastWriteTimeUtc));
                                    uploadedCount++;
                                    progressCallback?.Invoke($"Uploaded {uploadedCount}/{totalFiles}: {relativeFile}");
                                    Log($"Uploaded: {relativeFile}");
                                }
                            }
                            else
                            {
                                Log($"Warning: Local file not found: {localFile} (relative: {relativeFile})");
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
                    
                    // Set timestamps via SSH batch command for uploaded files
                    if (uploadedFileTimestamps.Count > 0)
                    {
                        Log("Setting remote file timestamps via SSH...");
                        var sshConnectionInfo = new Renci.SshNet.ConnectionInfo(
                            _ftpConfig.Host,
                            _ftpConfig.Port,
                            _ftpConfig.Username,
                            new Renci.SshNet.PasswordAuthenticationMethod(_ftpConfig.Username, _ftpConfig.Password))
                        {
                            Timeout = TimeSpan.FromSeconds(30)
                        };
                        
                        using (var sshClient = new Renci.SshNet.SshClient(sshConnectionInfo))
                        {
                            sshClient.Connect();
                            
                            // Set timestamps using touch command with -t flag for precision
                            // Format: touch -t [[CC]YY]MMDDhhmm[.ss] file
                            int successCount = 0;
                            foreach (var (remotePath, utcTime) in uploadedFileTimestamps)
                            {
                                try
                                {
                                    // Use touch -t with UTC time formatted as YYYYMMDDhhmm.ss
                                    var touchTimeFormat = utcTime.ToString("yyyyMMddHHmm.ss");
                                    var cmd = sshClient.CreateCommand($"touch -t {touchTimeFormat} \"{remotePath}\" 2>/dev/null || stat \"{remotePath}\" > /dev/null");
                                    var result = cmd.Execute();
                                    
                                    if (cmd.ExitStatus == 0)
                                    {
                                        successCount++;
                                        Log($"Set timestamp for {Path.GetFileName(remotePath)}: {touchTimeFormat}");
                                    }
                                    else
                                    {
                                        Log($"Warning: Failed to set timestamp for {remotePath}: exit code {cmd.ExitStatus}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"Warning: Could not set timestamp for {remotePath}: {ex.Message}");
                                }
                            }
                            
                            Log($"Successfully set timestamps for {successCount}/{uploadedFileTimestamps.Count} files");
                            sshClient.Disconnect();
                        }
                    }
                }

                // Delete files that only exist remotely (mirror sync)
                if (remoteOnlyFiles.Count > 0)
                {
                    Log($"Deleting {remoteOnlyFiles.Count} remote-only files to mirror local");
                    var connectionInfo2 = new Renci.SshNet.ConnectionInfo(
                        _ftpConfig.Host,
                        _ftpConfig.Port,
                        _ftpConfig.Username,
                        new Renci.SshNet.PasswordAuthenticationMethod(_ftpConfig.Username, _ftpConfig.Password))
                    {
                        Timeout = TimeSpan.FromSeconds(30)
                    };
                    
                    using (var client2 = new SftpClient(connectionInfo2))
                    {
                        client2.Connect();
                        var deletedCount = 0;
                        foreach (var relativeFile in remoteOnlyFiles)
                        {
                            try
                            {
                                var remoteFile = _remotePath + "/" + relativeFile;
                                if (client2.Exists(remoteFile))
                                {
                                    client2.DeleteFile(remoteFile);
                                    deletedCount++;
                                    progressCallback?.Invoke($"Deleted {deletedCount}/{remoteOnlyFiles.Count}: {relativeFile}");
                                    Log($"Deleted: {relativeFile}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Error deleting {relativeFile}: {ex.Message}");
                                progressCallback?.Invoke($"Error deleting {relativeFile}: {ex.Message}");
                            }
                        }
                        client2.Disconnect();
                        Log($"Deletion complete: {deletedCount}/{remoteOnlyFiles.Count} files deleted");
                        progressCallback?.Invoke($"✓ Deleted {deletedCount}/{remoteOnlyFiles.Count} remote-only files");
                    }
                }
            });
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
        await SyncToLocal(progressCallback);
    }

    private async Task SyncToLocal(Action<string>? progressCallback = null)
    {
        Log("SyncToLocal method called - INDEPENDENT sync (not using analysis results)");
        try
        {
            // Run SFTP operations on thread pool to avoid blocking UI thread
            await Task.Run(async () =>
            {
                // INDEPENDENT: Scan local and remote filesystems directly
                // Only exclude: extensions from excludedExtensions + folders from excludedFoldersFromSync
                Log("Scanning remote filesystem for files to download...");
                var remoteFiles = await GetRemoteFilesRecursiveAsync(_remotePath, onlyExcludeSync: true);
                
                Log("Scanning local filesystem...");
                var localFiles = await GetFilesRecursiveAsync(_localPath, onlyExcludeSync: true);
                
                // Determine what to download: files that are newer remotely or only exist remotely
                var filesToDownload = new List<string>();
                var localOnlyFiles = new List<string>();
                
                Log("Comparing remote and local files...");
                var localSet = new HashSet<string>(localFiles.Keys);
                
                foreach (var remoteFile in remoteFiles)
                {
                    var relPath = remoteFile.Key;
                    var remoteInfo = remoteFile.Value;
                    
                    if (localSet.Contains(relPath))
                    {
                        // File exists on both sides - check if remote is newer
                        var localInfo = localFiles[relPath];
                        if (remoteInfo.LastWriteTimeUtc > localInfo.LastWriteTimeUtc)
                        {
                            filesToDownload.Add(relPath);
                        }
                    }
                    else
                    {
                        // File only exists remotely
                        filesToDownload.Add(relPath);
                    }
                }
                
                // Local-only files should be deleted to mirror remote
                foreach (var localFile in localFiles)
                {
                    if (!remoteFiles.ContainsKey(localFile.Key))
                    {
                        localOnlyFiles.Add(localFile.Key);
                    }
                }
                
                Log($"Download plan: {filesToDownload.Count} files to download, {localOnlyFiles.Count} files to delete locally");

                // Create connection info with optimized buffer sizes
                var connectionInfo = new Renci.SshNet.ConnectionInfo(
                    _ftpConfig.Host,
                    _ftpConfig.Port,
                    _ftpConfig.Username,
                    new Renci.SshNet.PasswordAuthenticationMethod(_ftpConfig.Username, _ftpConfig.Password))
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                using (var client = new SftpClient(connectionInfo))
                {
                    Log("Connecting to SFTP server for download...");
                    client.Connect();
                    Log("Connected to SFTP server");

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

                            // Get remote file info before downloading to preserve timestamp
                            var remoteFileInfo = client.GetAttributes(remoteFile);
                            
                            // Download the file
                            using (var fileStream = File.Create(localFile))
                            {
                                client.DownloadFile(remoteFile, fileStream);
                            }
                            
                            // Preserve the remote file's modification timestamp on the local file AFTER stream is closed
                            if (remoteFileInfo != null && remoteFileInfo.LastWriteTime != DateTime.MinValue)
                            {
                                File.SetLastWriteTimeUtc(localFile, remoteFileInfo.LastWriteTimeUtc);
                            }
                            
                            downloadedCount++;
                            progressCallback?.Invoke($"Downloaded {downloadedCount}/{totalFiles}: {relativeFile}");
                            Log($"Downloaded: {relativeFile}");
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
                    
                    // Now query the actual remote timestamps via SSH and set local files to match
                    // This ensures timestamps match exactly what the next analysis will see
                    Log("Synchronizing timestamps via SSH for downloaded files...");
                    using (var sshClient = new SshClient(_ftpConfig.Host, _ftpConfig.Port, _ftpConfig.Username, _ftpConfig.Password))
                    {
                        sshClient.Connect();
                        try
                        {
                            const int FilesPerBatch = 50;
                            var fileList = filesToDownload.ToList();
                            
                            for (int batchStart = 0; batchStart < fileList.Count; batchStart += FilesPerBatch)
                            {
                                var batchEnd = Math.Min(batchStart + FilesPerBatch, fileList.Count);
                                var batchFiles = fileList.GetRange(batchStart, batchEnd - batchStart);
                                
                                // Create script to get timestamps
                                var scriptLines = new List<string> { "cd " + _remotePath };
                                foreach (var fileKey in batchFiles)
                                {
                                    var filePath = Path.Combine(_remotePath, fileKey).Replace('\\', '/').Replace("\"", "\\\"");
                                    scriptLines.Add($"stat -c '%Y' \"{filePath}\" 2>/dev/null || stat -f '%m' \"{filePath}\" 2>/dev/null || echo 0");
                                }
                                
                                var script = string.Join("; ", scriptLines);
                                var result = sshClient.RunCommand(script);
                                
                                if (result.ExitStatus == 0)
                                {
                                    var timestamps = result.Result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                    int index = 0;
                                    
                                    foreach (var fileKey in batchFiles)
                                    {
                                        if (index < timestamps.Length && long.TryParse(timestamps[index].Trim(), out var unixTimestamp) && unixTimestamp > 0)
                                        {
                                            var utcTime = DateTime.UnixEpoch.AddSeconds(unixTimestamp);
                                            var localFile = Path.Combine(_localPath, fileKey.Replace('/', Path.DirectorySeparatorChar));
                                            try
                                            {
                                                File.SetLastWriteTimeUtc(localFile, utcTime);
                                            }
                                            catch (Exception ex)
                                            {
                                                Log($"Warning: Could not set timestamp for {fileKey}: {ex.Message}");
                                            }
                                        }
                                        index++;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            sshClient.Disconnect();
                        }
                    }
                    Log("Timestamp synchronization complete");
                }

                // Delete files that only exist locally (mirror sync)
                if (localOnlyFiles.Count > 0)
                {
                    Log($"Deleting {localOnlyFiles.Count} local-only files to mirror remote");
                    var deletedCount = 0;
                    foreach (var relativeFile in localOnlyFiles)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(relativeFile))
                            {
                                Log($"Warning: Skipping empty filename in local-only files list");
                                continue;
                            }
                            
                            var localFile = Path.Combine(_localPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(localFile))
                            {
                                File.Delete(localFile);
                                deletedCount++;
                                progressCallback?.Invoke($"Deleted {deletedCount}/{localOnlyFiles.Count}: {relativeFile}");
                                Log($"Deleted: {relativeFile}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error deleting {relativeFile}: {ex.Message}");
                            progressCallback?.Invoke($"Error deleting {relativeFile}: {ex.Message}");
                        }
                    }
                    Log($"Deletion complete: {deletedCount}/{localOnlyFiles.Count} files deleted");
                    progressCallback?.Invoke($"✓ Deleted {deletedCount}/{localOnlyFiles.Count} local-only files");
                }
            });
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
        
        // Send to UI with a circular buffer (max 50 logs)
        // BUT only for important messages (not detailed file comparisons)
        if (!message.StartsWith("  -") && !message.StartsWith("\n") && !message.Contains("Status: "))
        {
            SendToUiLimitedLogs($"[{DateTime.Now:HH:mm:ss.fff}] [FtpService] {message}");
        }
    }

    /// <summary>
    /// Determines if a time difference (in hours) is likely just a timezone shift.
    /// Timezone shifts are typically round numbers (0, 1, 2, 3, 4 hours).
    /// Real file modifications have irregular timestamps (3 minutes, 5 seconds, etc).
    /// </summary>
    private bool IsRoundHourDifference(double absTimeDiffHours)
    {
        // Allow 0 hours (exact match) and 1-4 hour round numbers (common timezone ranges)
        // Also allow 5.5 hours (India/Nepal)
        // With small tolerance for floating point: ±5 minutes acceptable
        const double tolerance = 5.0 / 60.0; // 5 minutes in hours
        
        var roundHours = new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 5.5, 6.0 };
        
        foreach (var roundHour in roundHours)
        {
            if (Math.Abs(absTimeDiffHours - roundHour) < tolerance)
            {
                return true;
            }
        }
        
        return false;
    }

    private void LogDetailedToFileOnly(string message)
    {
        // Only log to file, not to UI (for detailed file-by-file comparisons)
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        var logPath = Path.Combine(exeDir, "wsync.log");

        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [FtpService] {message}\n");
        }
        catch { /* Ignore logging errors */ }
    }

    private void SendToUiLimitedLogs(string message)
    {
        // Add to circular buffer
        _uiLogBuffer.AddLast(message);
        
        // Keep only the last MaxUiLogs entries
        while (_uiLogBuffer.Count > MaxUiLogs)
        {
            _uiLogBuffer.RemoveFirst();
        }
        
        // Throttle callbacks to UI to avoid overwhelming the dispatcher
        var now = DateTime.Now;
        if ((now - _lastStatusCallbackTime).TotalMilliseconds >= StatusCallbackThrottleMs)
        {
            _lastStatusCallbackTime = now;
            _statusCallback?.Invoke(message);
        }
    }

    private void UpdateStatus(string message)
    {
        // Centralized method for all UI status updates
        // Uses the limited logs buffer
        SendToUiLimitedLogs(message);
    }
}
