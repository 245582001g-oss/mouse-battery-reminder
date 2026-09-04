using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly Func<object> _stateSnapshot;
    private readonly HidDeviceInventory _inventory;
    private readonly IAppEventLog _log;

    public DiagnosticsExporter(
        AppPaths paths,
        Func<AppSettings> settings,
        Func<object> stateSnapshot,
        HidDeviceInventory inventory,
        IAppEventLog log)
    {
        _paths = paths;
        _settings = settings;
        _stateSnapshot = stateSnapshot;
        _inventory = inventory;
        _log = log;
    }

    public string Export()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var operation = _log.BeginOperation("diagnostics");
        _log.Write("info", "diagnostics.export_started", nameof(DiagnosticsExporter), "success");
        try
        {
            _paths.Ensure();
            var directory = Path.Combine(
                _paths.DataDirectory,
                "diagnostics",
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..28]);
            Directory.CreateDirectory(directory);

            var errors = new List<DiagnosticExportError>();
            TryWriteJson(Path.Combine(directory, "app-state.json"), _stateSnapshot, "app-state", errors);
            TryWriteJson(Path.Combine(directory, "settings.json"), _settings, "settings", errors);
            TryWriteJson(
                Path.Combine(directory, "hid-devices.json"),
                () => EnumerateHidDevices(errors),
                "hid-devices",
                errors);
            var profileCount = CopyProfilesShared(Path.Combine(directory, "profiles"), errors);
            var historyEntries = WriteHistorySnapshot(Path.Combine(directory, "battery-history.jsonl"), errors);
            var logBytes = _log.SnapshotRecentLogs(
                Path.Combine(directory, "flight-recorder.jsonl"),
                5L * 1024 * 1024);

            WriteJson(Path.Combine(directory, "diagnostics-manifest.json"), new
            {
                schema = "sora.diagnostics.v1",
                created_utc = DateTime.UtcNow,
                fidelity = "local full-fidelity snapshot; device paths, serials, settings and profile extension fields are retained",
                profile_count = profileCount,
                history_entry_count = historyEntries,
                flight_log_bytes = logBytes,
                errors
            });
            var zipCreated = TryCreateZip(directory);
            _log.Write("info", "diagnostics.export_completed", nameof(DiagnosticsExporter), "success", new
            {
                duration_ms = started.ElapsedMilliseconds,
                profile_count = profileCount,
                history_entry_count = historyEntries,
                flight_log_bytes = logBytes,
                error_count = errors.Count,
                zip_created = zipCreated
            });
            return directory;
        }
        catch (Exception ex)
        {
            _log.Write("error", "diagnostics.export_failed", nameof(DiagnosticsExporter), "failure", new
            {
                duration_ms = started.ElapsedMilliseconds
            }, ex);
            throw;
        }
    }

    private object[] EnumerateHidDevices(ICollection<DiagnosticExportError> errors)
    {
        try
        {
            var snapshot = _inventory.GetSnapshot();
            if (!snapshot.IsReliable)
            {
                errors.Add(new DiagnosticExportError
                {
                    Component = "hid-inventory",
                    ErrorType = "UnreliableSnapshot",
                    Message = snapshot.Error ?? string.Empty
                });
            }

            return snapshot.Devices
                .Select(device => new
                {
                    vendorId = $"0x{device.VendorID:X4}",
                    productId = $"0x{device.ProductID:X4}",
                    productName = Safe(() => device.GetProductName()),
                    manufacturer = Safe(() => device.GetManufacturer()),
                    serialNumber = Safe(() => device.GetSerialNumber()),
                    devicePath = Safe(() => device.DevicePath),
                    fileSystemName = Safe(() => device.GetFileSystemName()),
                    maxInputReportLength = SafeInt(device.GetMaxInputReportLength),
                    maxOutputReportLength = SafeInt(device.GetMaxOutputReportLength),
                    maxFeatureReportLength = SafeInt(device.GetMaxFeatureReportLength)
                })
                .Cast<object>()
                .ToArray();
        }
        catch (Exception ex)
        {
            AddError(errors, "hid-inventory", ex);
            return Array.Empty<object>();
        }
    }

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int SafeInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private int CopyProfilesShared(string targetDirectory, ICollection<DiagnosticExportError> errors)
    {
        if (!Directory.Exists(_paths.ProfilesDirectory))
            return 0;

        Directory.CreateDirectory(targetDirectory);
        var count = 0;
        foreach (var sourcePath in Directory.GetFiles(_paths.ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var bytes = ReadAllBytesShared(sourcePath);
                using (JsonDocument.Parse(bytes))
                {
                    // Parsing before writing guarantees every exported .json profile is complete JSON.
                }
                File.WriteAllBytes(Path.Combine(targetDirectory, Path.GetFileName(sourcePath)), bytes);
                count++;
            }
            catch (Exception ex)
            {
                AddError(errors, $"profile:{Path.GetFileName(sourcePath)}", ex);
            }
        }
        return count;
    }

    private int WriteHistorySnapshot(string target, ICollection<DiagnosticExportError> errors)
    {
        if (!File.Exists(_paths.HistoryPath))
            return 0;

        var count = 0;
        try
        {
            using var source = new FileStream(
                _paths.HistoryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var targetStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(targetStream, new UTF8Encoding(false)) { NewLine = "\n" };
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    writer.WriteLine(document.RootElement.GetRawText());
                    count++;
                }
                catch (Exception ex)
                {
                    AddError(errors, "battery-history-line", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AddError(errors, "battery-history", ex);
        }
        return count;
    }

    private static void TryWriteJson(
        string path,
        Func<object> valueFactory,
        string component,
        ICollection<DiagnosticExportError> errors)
    {
        try
        {
            WriteJson(path, valueFactory());
        }
        catch (Exception ex)
        {
            AddError(errors, component, ex);
            WriteJson(path, new
            {
                snapshot_available = false,
                error_type = ex.GetType().FullName,
                error_message = ex.Message
            });
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AddError(
        ICollection<DiagnosticExportError> errors,
        string component,
        Exception exception)
    {
        errors.Add(new DiagnosticExportError
        {
            Component = component,
            ErrorType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message
        });
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static bool TryCreateZip(string directory)
    {
        try
        {
            var zipPath = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(directory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return true;
        }
        catch { return false; }
    }

    private sealed class DiagnosticExportError
    {
        public string Component { get; init; } = string.Empty;
        public string ErrorType { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }
}
