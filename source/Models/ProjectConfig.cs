using System.IO;

namespace Wsync.Models;

public enum AnalysisMode
{
    Full,   // Compare all files by timestamp and size
    Quick,  // Stop after finding 3 differing files
    Git     // Compare git commit timestamps
}

public class ProjectConfig
{
    [Newtonsoft.Json.JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
    
    [Newtonsoft.Json.JsonProperty("localPath")]
    public string LocalPath { get; set; } = string.Empty;
    
    [Newtonsoft.Json.JsonProperty("remotePath")]
    public string FtpRemotePath { get; set; } = string.Empty;

    /// <summary>
    /// Validates that this project configuration has all required fields.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Project name is missing or empty in config.json5 - See full log in wsync.log";
        
        if (string.IsNullOrWhiteSpace(LocalPath))
            return $"Project '{Name}': 'localPath' is missing or empty from config.json5 - See full log in wsync.log";

        if (string.IsNullOrWhiteSpace(FtpRemotePath))
            return $"Project '{Name}': 'remotePath' is missing or empty from config.json5 - See full log in wsync.log";
        
        if (!Directory.Exists(LocalPath))
            return $"Project '{Name}': Local path does not exist: {LocalPath} - See full log in wsync.log";
        
        return null; // Valid
    }
}

public class FtpConnectionConfig
{
    [Newtonsoft.Json.JsonProperty("host")]
    public string Host { get; set; } = string.Empty;
    
    [Newtonsoft.Json.JsonProperty("port")]
    public int Port { get; set; } = 21;
    
    [Newtonsoft.Json.JsonProperty("username")]
    public string Username { get; set; } = string.Empty;
    
    [Newtonsoft.Json.JsonProperty("password")]
    public string Password { get; set; } = string.Empty;
    
    [Newtonsoft.Json.JsonProperty("passiveMode")]
    public bool PassiveMode { get; set; } = true;
    
    [Newtonsoft.Json.JsonProperty("secure")]
    public bool Secure { get; set; } = false;
}

public class AppConfig
{
    [Newtonsoft.Json.JsonProperty("projects")]
    public List<ProjectConfig> Projects { get; set; } = new();
    
    [Newtonsoft.Json.JsonProperty("ftp")]
    public FtpConnectionConfig Ftp { get; set; } = new();
    
    [Newtonsoft.Json.JsonProperty("excludedExtensionsFromAnalysis")]
    public List<string> ExcludedExtensionsFromAnalysis { get; set; } = new();
    
    /// <summary>
    /// Extensions to exclude from SYNC operations.
    /// These files will NOT be synced.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("excludedExtensionsFromSync")]
    public List<string> ExcludedExtensionsFromSync { get; set; } = new();
    
    /// <summary>
    /// Specific filenames to exclude from ANALYSIS display purposes ONLY (case insensitive).
    /// These files WILL still be synced - this only affects the quick-view analysis UI.
    /// Use this to hide specific noisy files from analysis view.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("excludedFilesFromAnalysis")]
    public List<string> ExcludedFilesFromAnalysis { get; set; } = new();
    
    /// <summary>
    /// Specific filenames to exclude from SYNC operations (case insensitive).
    /// These files will NOT be synced.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("excludedFilesFromSync")]
    public List<string> ExcludedFilesFromSync { get; set; } = new();
    
    /// <summary>
    /// Folders to exclude from ANALYSIS display purposes ONLY.
    /// These folders WILL still be synced - this only affects the quick-view analysis UI.
    /// Combined with excludedFoldersFromSync and hardcoded ignored folders during analysis.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("excludedFoldersFromAnalysis")]
    public List<string> ExcludedFoldersFromAnalysis { get; set; } = new();
    
    /// <summary>
    /// Folders to exclude from SYNC operations.
    /// These folders will NOT be synced, but they will appear in analysis display.
    /// Analysis ignores these PLUS excludedFoldersFromAnalysis PLUS hardcoded ignored folders.
    /// Sync ignores these PLUS hardcoded ignored folders only.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("excludedFoldersFromSync")]
    public List<string> ExcludedFoldersFromSync { get; set; } = new();
    
    /// <summary>
    /// Optional path to WinSCP executable (winscp.com).
    /// If not provided, WinSCP will be searched in PATH and common installation locations.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("winscpPath")]
    public string? WinScpPath { get; set; }
}
