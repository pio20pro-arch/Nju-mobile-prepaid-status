using System.Text.Json;
using NjuPrepaidStatus.Models;

namespace NjuPrepaidStatus.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly FileLogger _logger;
    private readonly string _configPath;

    public ConfigService(FileLogger logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "NjuPrepaidStatus");
        Directory.CreateDirectory(directory);
        _configPath = Path.Combine(directory, "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var config = new AppConfig();
                Save(config);
                return config;
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            _logger.Error($"Config load failed: {ex}");
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.Error($"Config save failed: {ex}");
        }
    }

}
