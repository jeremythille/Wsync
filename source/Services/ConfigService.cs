using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wsync.Models;

namespace Wsync.Services;

public class ConfigService
{
    private const string ConfigFileName = "config.json5";
    private const string LogFileName = "wsync.log";
    private AppConfig _config = new();
    private readonly string _configPath;

    public ConfigService()
    {
        // Look for config in the same directory as the executable
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        _configPath = Path.Combine(exeDir, ConfigFileName);
        
        // Clear log file at startup
        var logPath = Path.Combine(exeDir, LogFileName);
        try
        {
            File.WriteAllText(logPath, "");
        }
        catch { /* Ignore if we can't clear */ }
        
        Log("=== ConfigService initialized ===");
        Log($"Exe directory: {exeDir}");
        Log($"Config path: {_configPath}");
        LoadConfig();
    }

    private void Log(string message)
    {
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        var logPath = Path.Combine(exeDir, LogFileName);
        
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { /* Ignore logging errors */ }
        
        System.Diagnostics.Debug.WriteLine($"[ConfigService] {message}");
    }

    public AppConfig GetConfig() => _config;

    public void LoadConfig()
    {
        try
        {
            Log($"Checking for config at: {_configPath}");
            Log($"File exists: {File.Exists(_configPath)}");
            
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                Log($"Raw JSON5 loaded, length: {json.Length}");
                
                try
                {
                    // Newtonsoft.Json's JObject.Parse handles JSON5 comments and trailing commas
                    var jObject = JObject.Parse(json);
                    Log("JSON5 parsed successfully");
                    
                    // Deserialize the JObject to AppConfig
                    _config = jObject.ToObject<AppConfig>(JsonSerializer.Create()) ?? new AppConfig();
                    Log($"Loaded {_config.Projects.Count} projects");
                    
                    foreach (var proj in _config.Projects)
                    {
                        Log($"Project: {proj.Name}");
                    }
                }
                catch (JsonReaderException jex)
                {
                    Log($"JSON parsing error: {jex.Message} at line {jex.LineNumber}, column {jex.LinePosition}");
                    throw;
                }
            }
            else
            {
                // Create default config if it doesn't exist
                Log("Config file not found, creating default");
                _config = new AppConfig();
                SaveConfig();
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR loading config: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            _config = new AppConfig();
        }
    }

    public void SaveConfig(AppConfig? config = null)
    {
        try
        {
            if (config != null)
            {
                _config = config;
            }
            
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            };
            var json = JsonConvert.SerializeObject(_config, settings);
            File.WriteAllText(_configPath, json);
            Log($"Config saved to {_configPath}");
        }
        catch (Exception ex)
        {
            Log($"Error saving config: {ex.Message}");
        }
    }

    public void AddProject(ProjectConfig project)
    {
        _config.Projects.Add(project);
        SaveConfig();
    }

    public List<ProjectConfig> GetProjects() => _config.Projects;
}

