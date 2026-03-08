using System.Text;
using System.Text.RegularExpressions;

namespace NjuPrepaidStatus.Services;

public sealed class FileLogger
{
    private readonly object _sync = new();
    private readonly string _logsDirectory;
    private bool _hideSecretsInLogs;

    public FileLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _logsDirectory = Path.Combine(appData, "NjuPrepaidStatus", "logs");
        Directory.CreateDirectory(_logsDirectory);
        CleanupPreviousLogs();
        Info("=== DEBUG LOG STARTED ===");
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);
    public void Debug(string message) => Write("DEBUG", message);
    public void SetHideSecretsInLogs(bool enabled) => _hideSecretsInLogs = enabled;

    public void LogHttpRequest(HttpMethod method, string url, IEnumerable<KeyValuePair<string, string>>? headers = null, string? body = null)
    {
        var command = new StringBuilder()
            .Append("xpire-X ")
            .Append(method.Method)
            .Append(" '")
            .Append(EscapeSingleQuotes(url))
            .Append("'");

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                command
                    .Append(" -H '")
                    .Append(EscapeSingleQuotes(header.Key))
                    .Append(": ")
                    .Append(EscapeSingleQuotes(header.Value))
                    .Append("'");
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            command
                .Append(" --data '")
                .Append(EscapeSingleQuotes(body))
                .Append("'");
        }

        Write("HTTP-REQUEST", command.ToString());
    }

    public void LogHttpResponse(string url, int statusCode, IEnumerable<KeyValuePair<string, string>>? headers, string body)
    {
        var builder = new StringBuilder()
            .AppendLine($"URL: {url}")
            .AppendLine($"Status: {statusCode}");

        if (headers is not null)
        {
            builder.AppendLine("Headers:");
            foreach (var header in headers)
            {
                builder.AppendLine($"{header.Key}: {header.Value}");
            }
        }

        builder.AppendLine("Body:")
            .AppendLine(body)
            .Append("---");

        Write("HTTP-RESPONSE", builder.ToString());
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_sync)
            {
                var filePath = Path.Combine(_logsDirectory, $"njuprepaidstatus-{DateTime.Now:yyyyMMdd}.log");
                var safeMessage = _hideSecretsInLogs ? MaskSecrets(message) : message;
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {safeMessage}{Environment.NewLine}";
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // never throw from logger
        }
    }

    private static string EscapeSingleQuotes(string value) => value.Replace("'", "''");

    private void CleanupPreviousLogs()
    {
        try
        {
            var todayName = $"njuprepaidstatus-{DateTime.Now:yyyyMMdd}.log";
            foreach (var file in Directory.GetFiles(_logsDirectory, "njuprepaidstatus-*.log"))
            {
                if (!string.Equals(Path.GetFileName(file), todayName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }

    private static string MaskSecrets(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return Regex.Replace(
            input,
            @"(?i)(^|[&\s'\?])(password-form)=([^&\s']+)",
            m => $"{m.Groups[1].Value}{m.Groups[2].Value}=***");
    }
}
