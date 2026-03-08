using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NjuPrepaidStatus.Models;

namespace NjuPrepaidStatus.Services;

[SupportedOSPlatform("windows")]
public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NjuPrepaidStatus.Credentials.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _filePath;

    public CredentialStore(string? filePath = null)
    {
        if (filePath is not null)
        {
            _filePath = filePath;
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "NjuPrepaidStatus");
        _filePath = Path.Combine(directory, "credentials.sec");
    }

    public bool TryLoadAll(out List<Credentials> credentials)
    {
        credentials = [];
        try
        {
            if (!File.Exists(_filePath))
            {
                return false;
            }

            var encryptedBytes = File.ReadAllBytes(_filePath);
            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decryptedBytes);
            var payload = JsonSerializer.Deserialize<PersistedCredentialsData>(json, JsonOptions);
            if (payload?.Accounts is null)
            {
                return false;
            }

            credentials = payload.Accounts
                .Where(c => !string.IsNullOrWhiteSpace(c.Username) && !string.IsNullOrWhiteSpace(c.Password))
                .ToList();
            return credentials.Count > 0;
        }
        catch
        {
            credentials = [];
            return false;
        }
    }

    public void SaveAll(IReadOnlyCollection<Credentials> credentials)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var payload = new PersistedCredentialsData { Accounts = credentials.ToList() };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, encryptedBytes);
        }
        catch
        {
            // Keep behavior non-throwing like the secure store pattern from NjuTrayApp.
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch
        {
            // Keep behavior non-throwing like the secure store pattern from NjuTrayApp.
        }
    }

}
