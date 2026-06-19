using System.Text.Json;
using System.IO.Compression;
using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly Func<object> _stateSnapshot;

    public DiagnosticsExporter(AppPaths paths, Func<AppSettings> settings, Func<object> stateSnapshot)
    {
        _paths = paths;
        _settings = settings;
        _stateSnapshot = stateSnapshot;
    }

    public string Export()
    {
        _paths.Ensure();
        var dir = Path.Combine(_paths.DataDirectory, "diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        WriteJson(Path.Combine(dir, "app-state.json"), _stateSnapshot());
        WriteJson(Path.Combine(dir, "settings.json"), SanitizeSettings(_settings()));
        WriteJson(Path.Combine(dir, "hid-devices.json"), EnumerateHidDevices());
        CopyDirectoryFiles(_paths.ProfilesDirectory, Path.Combine(dir, "profiles"), "*.json");
        CopyFileIfExists(_paths.HistoryPath, Path.Combine(dir, "battery-history.jsonl"));
        TryCreateZip(dir);

        return dir;
    }

    private static object SanitizeSettings(AppSettings settings) => new
    {
        settings.AlertThreshold,
        settings.PollingIntervalMinutes,
        settings.AlertCooldownMinutes,
        settings.StartupWithWindows,
        settings.Language,
        settings.AlertSoundFile,
        settings.AlertVolume
    };

    private static object[] EnumerateHidDevices()
    {
        try
        {
            return DeviceList.Local.GetHidDevices()
                .Select(device => new
                {
                    vendorId = $"0x{device.VendorID:X4}",
                    productId = $"0x{device.ProductID:X4}",
                    productName = Safe(() => device.GetProductName()),
                    manufacturer = Safe(() => device.GetManufacturer()),
                    serialNumber = Safe(() => device.GetSerialNumber()),
                    maxInputReportLength = SafeInt(device.GetMaxInputReportLength),
                    maxOutputReportLength = SafeInt(device.GetMaxOutputReportLength),
                    maxFeatureReportLength = SafeInt(device.GetMaxFeatureReportLength),
                    devicePathHash = HashPath(Safe(() => device.DevicePath))
                })
                .Cast<object>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private static string HashPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes)[..16];
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void CopyDirectoryFiles(string sourceDir, string targetDir, string pattern)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
                return;

            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        catch { }
    }

    private static void CopyFileIfExists(string source, string target)
    {
        try
        {
            if (File.Exists(source))
                File.Copy(source, target, overwrite: true);
        }
        catch { }
    }

    private static void TryCreateZip(string dir)
    {
        try
        {
            var zipPath = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(dir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch { }
    }
}
