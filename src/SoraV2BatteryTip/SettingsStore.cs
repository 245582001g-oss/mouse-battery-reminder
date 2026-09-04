using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace SoraV2BatteryTip;

internal sealed class AppPaths
{
    public string DataDirectory { get; }
    public string SoundsDirectory { get; }
    public string ProfilesDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsPath { get; }
    public string HistoryPath { get; }
    public string LogIdentityKeyPath { get; }

    public AppPaths(string? dataDirectory = null)
    {
        var localDocuments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        var documents = Directory.Exists(localDocuments) ? localDocuments : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(documents, "SoraV2BatteryTip")
            : Path.GetFullPath(dataDirectory);
        SoundsDirectory = Path.Combine(DataDirectory, "sounds");
        ProfilesDirectory = Path.Combine(DataDirectory, "profiles");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        HistoryPath = Path.Combine(DataDirectory, "battery-history.jsonl");
        LogIdentityKeyPath = Path.Combine(DataDirectory, ".flight-recorder.key");
    }

    public void Ensure()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SoundsDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(LogsDirectory);

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (!Directory.Exists(assets))
            return;

        foreach (var source in Directory.GetFiles(assets, "*.wav", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(SoundsDirectory, Path.GetFileName(source));
            if (!File.Exists(target))
                File.Copy(source, target, overwrite: false);
        }

        var profileAssets = Path.Combine(assets, "Profiles");
        if (!Directory.Exists(profileAssets))
            return;

        foreach (var source in Directory.GetFiles(profileAssets, "*.json", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(ProfilesDirectory, Path.GetFileName(source));
            if (!File.Exists(target))
                File.Copy(source, target, overwrite: false);
        }
    }

    public void OpenProfilesDirectory()
    {
        Ensure();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProfilesDirectory) { UseShellExecute = true }); }
        catch { }
    }

    public void OpenLogsDirectory()
    {
        Ensure();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogsDirectory) { UseShellExecute = true }); }
        catch { }
    }

    public string CandidatesDirectory => Path.Combine(DataDirectory, "candidates");
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly IAppEventLog? _log;

    public SettingsStore(AppPaths paths, IAppEventLog? log = null)
    {
        _paths = paths;
        _log = log;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsPath))
            {
                var loadedSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsPath));
                if (loadedSettings != null)
                {
                    _log?.Write("info", "settings.load", nameof(SettingsStore), "success", new { source = "existing" });
                    return loadedSettings;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Write("warn", "settings.load", nameof(SettingsStore), "degraded", new { source = "default_after_error" }, ex);
        }

        var settings = CreateDefault();
        Save(settings);
        _log?.Write("info", "settings.load", nameof(SettingsStore), "success", new { source = "default" });
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            _paths.Ensure();
            File.WriteAllText(_paths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            _log?.Write("info", "settings.save", nameof(SettingsStore), "success");
        }
        catch (Exception ex)
        {
            _log?.Write("error", "settings.save", nameof(SettingsStore), "failure", exception: ex);
        }
    }

    private static AppSettings CreateDefault() => new()
    {
        Language = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US"
    };
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppName = "SoraV2BatteryTip";

    public static void SetEnabled(bool enabled, IAppEventLog? log = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
                DeleteStartupApproved(AppName);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                DeleteStartupApproved(AppName);
            }
            log?.Write("info", "startup.registration_changed", nameof(StartupManager), "success", new { enabled });
        }
        catch (Exception ex)
        {
            log?.Write("error", "startup.registration_changed", nameof(StartupManager), "failure", new { enabled }, ex);
        }
    }

    private static void DeleteStartupApproved(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
