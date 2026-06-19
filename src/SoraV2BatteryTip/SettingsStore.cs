using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace SoraV2BatteryTip;

internal sealed class AppPaths
{
    public string DataDirectory { get; }
    public string SoundsDirectory { get; }
    public string ProfilesDirectory { get; }
    public string SettingsPath { get; }
    public string HistoryPath { get; }

    public AppPaths()
    {
        var localDocuments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        var documents = Directory.Exists(localDocuments) ? localDocuments : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        DataDirectory = Path.Combine(documents, "SoraV2BatteryTip");
        SoundsDirectory = Path.Combine(DataDirectory, "sounds");
        ProfilesDirectory = Path.Combine(DataDirectory, "profiles");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        HistoryPath = Path.Combine(DataDirectory, "battery-history.jsonl");
    }

    public void Ensure()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SoundsDirectory);
        Directory.CreateDirectory(ProfilesDirectory);

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

    public string CandidatesDirectory => Path.Combine(DataDirectory, "candidates");
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;

    public SettingsStore(AppPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsPath)) ?? CreateDefault();
        }
        catch { }

        var settings = CreateDefault();
        Save(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        _paths.Ensure();
        File.WriteAllText(_paths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
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

    public static void SetEnabled(bool enabled)
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
        }
        catch { }
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
