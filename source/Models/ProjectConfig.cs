namespace Wsync.Models;

public class ProjectConfig
{
    public string Name { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string FtpRemotePath { get; set; } = string.Empty;
}

public class FtpConnectionConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool PassiveMode { get; set; } = true;
    public bool Secure { get; set; } = false;
}

public class AppConfig
{
    public List<ProjectConfig> Projects { get; set; } = new();
    public FtpConnectionConfig Ftp { get; set; } = new();
}
