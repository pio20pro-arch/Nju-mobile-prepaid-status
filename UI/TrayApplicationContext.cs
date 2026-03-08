using NjuPrepaidStatus.Models;
using NjuPrepaidStatus.Services;
using System.Security.Authentication;

namespace NjuPrepaidStatus.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private const int MinRefreshIntervalSeconds = 15;
    private const int FetchRetryCount = 60;
    private static readonly TimeSpan FetchRetryDelay = TimeSpan.FromSeconds(5);

    private readonly CredentialStore _credentialStore;
    private readonly NjuHtmlParser _parser;
    private readonly AutostartService _autostartService;
    private readonly FileLogger _logger;
    private readonly ConfigService _configService;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _addAccountMenuItem;
    private readonly ToolStripMenuItem _clearAccountsMenuItem;
    private readonly ToolStripMenuItem _autostartMenuItem;
    private readonly ToolStripMenuItem _hideSecretsInLogsMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;
    private readonly ToolStripSeparator _numbersSeparator;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _uiContext;

    private readonly Dictionary<string, Credentials> _accounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _persistedAccounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumberTrayIconState> _numberTrayIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _perNumberTrayIconEnabled = new(StringComparer.Ordinal);
    private Dictionary<string, AccountUsage> _lastUsageByPhone = new(StringComparer.Ordinal);
    private Icon? _mainCustomIcon;
    private AppConfig _config;

    public TrayApplicationContext(
        CredentialStore credentialStore,
        NjuHtmlParser parser,
        AutostartService autostartService,
        FileLogger logger,
        ConfigService configService)
    {
        _credentialStore = credentialStore;
        _parser = parser;
        _autostartService = autostartService;
        _logger = logger;
        _configService = configService;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _config = _configService.Load();
        _logger.SetHideSecretsInLogs(_config.HideSecretsInLogs);
        _perNumberTrayIconEnabled.Clear();
        foreach (var pair in _config.PerNumberTrayIconEnabled ?? new Dictionary<string, bool>())
        {
            _perNumberTrayIconEnabled[pair.Key] = pair.Value;
        }

        _contextMenu = new ContextMenuStrip();
        _addAccountMenuItem = new ToolStripMenuItem("Dodaj konto", null, (_, _) => AddAccount());
        _clearAccountsMenuItem = new ToolStripMenuItem("Wyczysc wszystkie konta", null, (_, _) => ClearAllAccounts());
        _autostartMenuItem = new ToolStripMenuItem("Autostart z Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = _autostartService.IsEnabled(),
            CheckOnClick = false
        };
        _hideSecretsInLogsMenuItem = new ToolStripMenuItem("Ukrywaj sekrety w logach", null, (_, _) => ToggleHideSecretsInLogs())
        {
            Checked = _config.HideSecretsInLogs,
            CheckOnClick = false
        };
        _exitMenuItem = new ToolStripMenuItem("Wyjdz", null, (_, _) => ExitApplication());
        _numbersSeparator = new ToolStripSeparator();
        _contextMenu.Items.AddRange([_numbersSeparator, _addAccountMenuItem, _clearAccountsMenuItem, _autostartMenuItem, _hideSecretsInLogsMenuItem, _exitMenuItem]);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = _contextMenu,
            Text = "Nju Web Usage: brak kont"
        };
        _notifyIcon.DoubleClick += (_, _) => AddAccount();
        _logger.Info("Application started.");

        LoadStoredAccounts();
        RebuildNumbersMenuItems([]);
        _ = RefreshAllAccountsAsync();
        StartBackgroundLoop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _mainCustomIcon?.Dispose();
            _cts.Dispose();
            _refreshLock.Dispose();
            ClearPerNumberTrayIcons();
        }

        base.Dispose(disposing);
    }

    private void LoadStoredAccounts()
    {
        try
        {
            if (!_credentialStore.TryLoadAll(out var stored))
            {
                return;
            }

            foreach (var credentials in stored)
            {
                var key = NormalizePhoneNumber(credentials.Username);
                _accounts[key] = credentials;
                _persistedAccounts.Add(key);
            }

            if (_accounts.Count > 0)
            {
                _logger.Info($"Loaded accounts from secure storage: {_accounts.Count}.");
                ShowInfo("Nju Web Usage", $"Wczytano zapisane konta: {_accounts.Count}.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Stored accounts load failed: {ex}");
            ShowError("Nju Web Usage", $"Blad odczytu zapisanych kont: {ex.Message}");
        }
    }

    private void AddAccount()
    {
        var dialogResult = CredentialsDialog.ShowCredentialsDialog(new HiddenOwnerWindow(), rememberDefault: true);
        if (dialogResult is null)
        {
            return;
        }

        var key = NormalizePhoneNumber(dialogResult.Credentials.Username);
        _accounts[key] = dialogResult.Credentials;
        _logger.Info($"Account updated in memory: {key}.");
        if (dialogResult.RememberCredentials)
        {
            _persistedAccounts.Add(key);
        }
        else
        {
            _persistedAccounts.Remove(key);
        }

        SavePersistedAccounts();
        _ = RefreshAllAccountsAsync();
    }

    private void ClearAllAccounts()
    {
        _accounts.Clear();
        _persistedAccounts.Clear();
        _credentialStore.Delete();
        _logger.Info("All accounts cleared.");
        SetNoAccountsState();
    }

    private async Task RefreshAllAccountsAsync()
    {
        await _refreshLock.WaitAsync(_cts.Token);
        try
        {
            if (_accounts.Count == 0)
            {
                SetNoAccountsState();
                return;
            }

            var accounts = _accounts.Values.ToList();
            var tasks = accounts.Select(account => FetchUsageForAccountAsync(account, _cts.Token)).ToArray();
            var results = await Task.WhenAll(tasks);

            var usage = results.Where(r => r is not null).Select(r => r!).ToList();
            if (usage.Count == 0)
            {
                SetWarningState("Brak danych. Sprawdz loginy/hasla.");
                return;
            }

            UpdateTrayUi(usage);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.Error($"Refresh failed: {ex}");
            ShowError("Nju Web Usage", ex.Message);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AccountUsage?> FetchUsageForAccountAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= FetchRetryCount; attempt++)
        {
            try
            {
                using var client = new NjuWebClient(_logger);
                var html = await client.GetBalanceHtmlAsync(credentials, cancellationToken);
                var balance = _parser.ParseBalanceStatus(html);
                var normalizedPhone = NormalizePhoneNumber(credentials.Username);
                return new AccountUsage(
                    normalizedPhone,
                    balance.DomesticUsage.AvailableGb * 1024m,
                    balance.DomesticUsage.AvailableGb,
                    balance.RoamingUsage.AvailableGb * 1024m,
                    balance.RoamingUsage.AvailableGb);
            }
            catch (AuthenticationException ex)
            {
                _logger.Error($"Fetch usage auth failed for {credentials.Username}: {ex.Message}");
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < FetchRetryCount)
            {
                _logger.Error(
                    $"Fetch usage failed for {credentials.Username}: {ex.Message}. Retry {attempt}/{FetchRetryCount} in {FetchRetryDelay.TotalSeconds:0}s.");
                await Task.Delay(FetchRetryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Fetch usage failed for {credentials.Username} after {FetchRetryCount} attempts: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    private void UpdateTrayUi(IReadOnlyCollection<AccountUsage> usage)
    {
        var usageByPhone = usage.ToDictionary(x => x.PhoneNumber, StringComparer.Ordinal);

        _uiContext.Post(_ =>
        {
            var totalMb = ComputeSelectedTotalMb(usage);
            var totalGb = totalMb / 1024m;
            var iconText = FormatIconTextFromGb(totalGb);
            var tooltip = TruncateTooltip($"Total: {totalMb:0.##} MB | {totalGb:0.##} GB");

            _lastUsageByPhone = usageByPhone;
            _mainCustomIcon?.Dispose();
            _mainCustomIcon = TrayIconFactory.CreateNumberIcon(iconText);
            _notifyIcon.Icon = _mainCustomIcon;
            _notifyIcon.Text = tooltip;
            RebuildNumbersMenuItems(usage);
            UpdatePerNumberTrayIcons(usage);
        }, null);
    }

    private void SetNoAccountsState()
    {
        _uiContext.Post(_ =>
        {
            _mainCustomIcon?.Dispose();
            _mainCustomIcon = null;
            _notifyIcon.Icon = SystemIcons.Information;
            _notifyIcon.Text = "Nju Web Usage: brak kont";
            RebuildNumbersMenuItems([]);
            ClearPerNumberTrayIcons();
        }, null);
    }

    private void SetWarningState(string message)
    {
        _uiContext.Post(_ =>
        {
            _mainCustomIcon?.Dispose();
            _mainCustomIcon = null;
            _notifyIcon.Icon = SystemIcons.Warning;
            _notifyIcon.Text = TruncateTooltip($"Nju Web Usage: {message}");
            RebuildNumbersMenuItems([]);
            ClearPerNumberTrayIcons();
        }, null);
    }

    private void RebuildNumbersMenuItems(IReadOnlyCollection<AccountUsage> usage)
    {
        while (_contextMenu.Items.Count > 0 && _contextMenu.Items[0] != _numbersSeparator)
        {
            var item = _contextMenu.Items[0];
            _contextMenu.Items.RemoveAt(0);
            item.Dispose();
        }

        if (usage.Count == 0)
        {
            var emptyItem = new ToolStripMenuItem("Brak danych")
            {
                Enabled = false
            };
            _contextMenu.Items.Insert(0, emptyItem);
            _numbersSeparator.Visible = false;
            return;
        }

        var ordered = usage.OrderBy(x => x.PhoneNumber).ToList();
        var totalMb = ComputeSelectedTotalMb(ordered);
        var totalGb = totalMb / 1024m;
        var totalItem = new ToolStripMenuItem($"Total: {totalMb:0.##} MB | {totalGb:0.##} GB")
        {
            Enabled = false
        };
        _contextMenu.Items.Insert(0, totalItem);

        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var row = ordered[i];
            var phone = row.PhoneNumber;
            var domesticKey = BuildUsageKey(phone, UsageKind.Domestic);
            var domesticItem = new ToolStripMenuItem($"{phone} (kraj): {row.RemainingMb:0.##} MB | {row.RemainingGb:0.##} GB")
            {
                Checked = IsPerNumberIconEnabled(domesticKey),
                CheckOnClick = true
            };
            domesticItem.Click += (_, _) => TogglePerNumberIcon(domesticKey, domesticItem.Checked);
            _contextMenu.Items.Insert(1, domesticItem);

            var roamingKey = BuildUsageKey(phone, UsageKind.Roaming);
            var roamingItem = new ToolStripMenuItem($"{phone} (roaming): {row.RoamingMb:0.##} MB | {row.RoamingGb:0.##} GB")
            {
                Checked = IsPerNumberIconEnabled(roamingKey),
                CheckOnClick = true
            };
            roamingItem.Click += (_, _) => TogglePerNumberIcon(roamingKey, roamingItem.Checked);
            _contextMenu.Items.Insert(2, roamingItem);
        }

        _numbersSeparator.Visible = true;
    }

    private void UpdatePerNumberTrayIcons(IReadOnlyCollection<AccountUsage> usage)
    {
        var requiredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var accountUsage in usage)
        {
            var domesticKey = BuildUsageKey(accountUsage.PhoneNumber, UsageKind.Domestic);
            if (IsPerNumberIconEnabled(domesticKey))
            {
                requiredKeys.Add(domesticKey);
            }

            var roamingKey = BuildUsageKey(accountUsage.PhoneNumber, UsageKind.Roaming);
            if (IsPerNumberIconEnabled(roamingKey))
            {
                requiredKeys.Add(roamingKey);
            }
        }

        foreach (var key in _numberTrayIcons.Keys.ToList())
        {
            if (!requiredKeys.Contains(key))
            {
                RemovePerNumberIcon(key);
            }
        }

        foreach (var accountUsage in usage.OrderBy(u => u.PhoneNumber))
        {
            UpdateSingleUsageTrayIcon(
                BuildUsageKey(accountUsage.PhoneNumber, UsageKind.Domestic),
                $"{accountUsage.PhoneNumber} kraj: {accountUsage.RemainingMb:0.##} MB | {accountUsage.RemainingGb:0.##} GB",
                accountUsage.RemainingGb);
            UpdateSingleUsageTrayIcon(
                BuildUsageKey(accountUsage.PhoneNumber, UsageKind.Roaming),
                $"{accountUsage.PhoneNumber} roaming: {accountUsage.RoamingMb:0.##} MB | {accountUsage.RoamingGb:0.##} GB",
                accountUsage.RoamingGb);
        }
    }

    private void ClearPerNumberTrayIcons()
    {
        foreach (var key in _numberTrayIcons.Keys.ToList())
        {
            RemovePerNumberIcon(key);
        }
    }

    private void RemovePerNumberIcon(string key)
    {
        if (!_numberTrayIcons.TryGetValue(key, out var state))
        {
            return;
        }

        state.NotifyIcon.Visible = false;
        state.NotifyIcon.Dispose();
        state.Icon?.Dispose();
        _numberTrayIcons.Remove(key);
    }

    private bool IsPerNumberIconEnabled(string usageKey)
    {
        if (_perNumberTrayIconEnabled.TryGetValue(usageKey, out var enabled))
        {
            return enabled;
        }

        _perNumberTrayIconEnabled[usageKey] = true;
        return true;
    }

    private void TogglePerNumberIcon(string usageKey, bool enabled)
    {
        _perNumberTrayIconEnabled[usageKey] = enabled;
        SavePerNumberIconPreferences();
        if (_lastUsageByPhone.Count > 0)
        {
            UpdateTrayUi(_lastUsageByPhone.Values.ToList());
        }
        else if (!enabled)
        {
            RemovePerNumberIcon(usageKey);
        }
    }

    private void SavePerNumberIconPreferences()
    {
        _config.PerNumberTrayIconEnabled = new Dictionary<string, bool>(_perNumberTrayIconEnabled, StringComparer.Ordinal);
        _configService.Save(_config);
    }

    private void UpdateSingleUsageTrayIcon(string usageKey, string tooltip, decimal remainingGb)
    {
        if (!IsPerNumberIconEnabled(usageKey))
        {
            RemovePerNumberIcon(usageKey);
            return;
        }

        if (!_numberTrayIcons.TryGetValue(usageKey, out var state))
        {
            var icon = new NotifyIcon
            {
                Visible = true
            };
            state = new NumberTrayIconState(icon);
            _numberTrayIcons[usageKey] = state;
        }

        state.Icon?.Dispose();
        state.Icon = TrayIconFactory.CreateNumberIcon(FormatIconTextFromGb(remainingGb));
        state.NotifyIcon.Icon = state.Icon;
        state.NotifyIcon.Text = TruncateTooltip(tooltip);
    }

    private void SavePersistedAccounts()
    {
        try
        {
            var toSave = _accounts
                .Where(kvp => _persistedAccounts.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            if (toSave.Count == 0)
            {
                _credentialStore.Delete();
            }
            else
            {
                _credentialStore.SaveAll(toSave);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Persist accounts failed: {ex}");
            ShowError("Nju Web Usage", $"Nie mozna zapisac kont: {ex.Message}");
        }
    }

    private void StartBackgroundLoop()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var delaySeconds = Math.Max(MinRefreshIntervalSeconds, _config.RefreshIntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _cts.Token);

                    if (_accounts.Count > 0)
                    {
                        await RefreshAllAccountsAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, _cts.Token);
    }

    private void ExitApplication()
    {
        _cts.Cancel();
        ExitThread();
    }

    private void ToggleAutostart()
    {
        try
        {
            var nextState = !_autostartMenuItem.Checked;
            _autostartService.SetEnabled(nextState);
            _autostartMenuItem.Checked = _autostartService.IsEnabled();
            ShowInfo("Autostart", _autostartMenuItem.Checked ? "Autostart wlaczony." : "Autostart wylaczony.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Autostart toggle failed: {ex}");
            ShowError("Autostart", $"Nie udalo sie zmienic autostartu: {ex.Message}");
        }
    }

    private void ToggleHideSecretsInLogs()
    {
        _config.HideSecretsInLogs = !_config.HideSecretsInLogs;
        _hideSecretsInLogsMenuItem.Checked = _config.HideSecretsInLogs;
        _configService.Save(_config);
        _logger.SetHideSecretsInLogs(_config.HideSecretsInLogs);
        _logger.Info($"HideSecretsInLogs changed: {_config.HideSecretsInLogs}.");
        ShowInfo("Logi", _config.HideSecretsInLogs ? "Ukrywanie sekretow wlaczone." : "Ukrywanie sekretow wylaczone.");
    }

    private static string NormalizePhoneNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("48", StringComparison.Ordinal) && trimmed.Length > 2
            ? trimmed[2..]
            : trimmed;
    }

    private static string FormatIconTextFromGb(decimal gb)
    {
        var roundedGb = Math.Max(0, decimal.Round(gb, 0, MidpointRounding.AwayFromZero));
        if (roundedGb < 1000m)
        {
            return roundedGb.ToString("0");
        }

        var tb = Math.Max(1, (int)decimal.Round(roundedGb / 1024m, 0, MidpointRounding.AwayFromZero));
        return $"{tb}TB";
    }

    private static string TruncateTooltip(string text)
    {
        const int maxLength = 63;
        return text.Length <= maxLength ? text : text[..(maxLength - 1)];
    }

    private void ShowInfo(string title, string text)
    {
        _uiContext.Post(_ =>
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(2000);
        }, null);
    }

    private void ShowError(string title, string text)
    {
        _uiContext.Post(_ =>
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(3000);
        }, null);
    }

    private sealed class HiddenOwnerWindow : IWin32Window
    {
        public IntPtr Handle => IntPtr.Zero;
    }

    private static string BuildUsageKey(string phoneNumber, UsageKind usageKind)
    {
        return $"{phoneNumber}|{usageKind}";
    }

    private decimal ComputeSelectedTotalMb(IEnumerable<AccountUsage> usage)
    {
        decimal totalMb = 0m;
        foreach (var accountUsage in usage)
        {
            if (IsPerNumberIconEnabled(BuildUsageKey(accountUsage.PhoneNumber, UsageKind.Domestic)))
            {
                totalMb += accountUsage.RemainingMb;
            }

            if (IsPerNumberIconEnabled(BuildUsageKey(accountUsage.PhoneNumber, UsageKind.Roaming)))
            {
                totalMb += accountUsage.RoamingMb;
            }
        }

        return totalMb;
    }

    private enum UsageKind
    {
        Domestic,
        Roaming
    }

    private sealed record AccountUsage(
        string PhoneNumber,
        decimal RemainingMb,
        decimal RemainingGb,
        decimal RoamingMb,
        decimal RoamingGb);

    private sealed class NumberTrayIconState(NotifyIcon notifyIcon)
    {
        public NotifyIcon NotifyIcon { get; } = notifyIcon;
        public Icon? Icon { get; set; }
    }
}
