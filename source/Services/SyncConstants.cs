namespace Wsync.Services;

/// <summary>
/// Shared constants for sync operations across all services
/// </summary>
public static class SyncConstants
{
    /// <summary>
    /// Default file extensions excluded from analysis ONLY (not from sync).
    /// These files will be synced, but analysis will skip them.
    /// </summary>
    public static readonly string[] DefaultExcludedExtensionsFromAnalysis = new string[] { };

    /// <summary>
    /// Default file extensions that should never be synced.
    /// These files will be excluded from both analysis and sync.
    /// </summary>
    public static readonly string[] DefaultExcludedExtensionsFromSync = new[]
    {
        "npmrc",
        "lock",
        "log",
        "sql",
        "sqlite",
        "sqlite3",
    };

    /// <summary>
    /// Default filenames excluded from analysis ONLY (not from sync).
    /// These files will be synced, but analysis will skip them.
    /// </summary>
    public static readonly string[] DefaultExcludedFilesFromAnalysis = new[]
    {
        "readme.txt",
        ".ds_store",
        "thumbs.db",
        ".npmrc",
    };

    /// <summary>
    /// Default filenames that should never be synced.
    /// These files will be excluded from both analysis and sync.
    /// </summary>
    public static readonly string[] DefaultExcludedFilesFromSync = new string[] { };

    /// <summary>
    /// Default folders excluded from analysis ONLY (not from sync).
    /// These are analyzed using special modes (like git commit comparison) rather than file-by-file analysis.
    /// </summary>
    public static readonly string[] DefaultExcludedFoldersFromAnalysis = new[]
    {
        ".git",  // Excluded from analysis, but has dedicated git mode for commit comparison
    };

    /// <summary>
    /// Default folders that should never be synced (and therefore there's no need to analyze them).
    /// These are build artifacts, cache, IDE configuration, and temporary files.
    /// Combined with config excludedFoldersFromSync during initialization.
    /// </summary>
    public static readonly string[] DefaultExcludedFoldersFromSync = new[]
    {
        ".DS_Store",
        ".angular",
        ".github",
        ".idea",
        ".npmrc",
        ".pytest_cache",
        ".svn",
        ".vs",
        ".vscode",
        "Thumbs.db",
        "__pycache__",
        "bin",
        "env",
        "node_modules",
        "non-code",
        "obj",
        "packages",
        "playwright-report",
        "test-results",
        "venv",
    };
}
