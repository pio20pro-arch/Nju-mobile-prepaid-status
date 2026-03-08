namespace NjuPrepaidStatus.Models;

public sealed class AppConfig
{
    public int RefreshIntervalSeconds { get; set; } = 60;
    public bool HideSecretsInLogs { get; set; }
    public Dictionary<string, bool> PerNumberTrayIconEnabled { get; set; } = new();
}
