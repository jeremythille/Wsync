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
    
    [Newtonsoft.Json.JsonProperty("ftpRemotePath")]
    public string FtpRemotePath { get; set; } = string.Empty;
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
    
    [Newtonsoft.Json.JsonProperty("excludedExtensions")]
    public List<string> ExcludedExtensions { get; set; } = new();
    
    [Newtonsoft.Json.JsonProperty("excludedFolders")]
    public List<string> ExcludedFolders { get; set; } = new();
}
