using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class HidDeviceInventory
{
    private static readonly TimeSpan MaximumReliableSnapshotAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumFailedSnapshotAge = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<HidDevice>> _enumerateDevices;
    private readonly Func<DateTime> _utcNow;
    private readonly IAppEventLog? _log;
    private HidInventorySnapshot? _snapshot;
    private int _generation;

    public HidDeviceInventory(
        Func<IReadOnlyList<HidDevice>>? enumerateDevices = null,
        Func<DateTime>? utcNow = null,
        IAppEventLog? log = null)
    {
        _enumerateDevices = enumerateDevices ?? (() => DeviceList.Local.GetHidDevices().ToArray());
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    public HidInventorySnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var nowUtc = _utcNow();
            if (_snapshot != null)
            {
                var maximumAge = _snapshot.IsReliable ? MaximumReliableSnapshotAge : MaximumFailedSnapshotAge;
                if (nowUtc - _snapshot.CreatedUtc < maximumAge)
                {
                    _log?.Write("debug", "hid.inventory.cache_hit", nameof(HidDeviceInventory), "success", new
                    {
                        generation = _snapshot.Generation,
                        reliable = _snapshot.IsReliable,
                        device_count = _snapshot.Devices.Count,
                        age_ms = Math.Max(0, (long)(nowUtc - _snapshot.CreatedUtc).TotalMilliseconds)
                    });
                    return _snapshot;
                }
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _snapshot = new HidInventorySnapshot
                {
                    Devices = _enumerateDevices(),
                    Generation = _generation,
                    CreatedUtc = nowUtc,
                    IsReliable = true
                };
                _log?.Write("debug", "hid.inventory.refreshed", nameof(HidDeviceInventory), "success", new
                {
                    generation = _generation,
                    device_count = _snapshot.Devices.Count,
                    duration_ms = started.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                _snapshot = new HidInventorySnapshot
                {
                    Generation = _generation,
                    CreatedUtc = nowUtc,
                    IsReliable = false,
                    Error = ex.GetType().Name
                };
                _log?.Write("warn", "hid.inventory.failed", nameof(HidDeviceInventory), "failure", new
                {
                    generation = _generation,
                    retry_after_ms = (long)MaximumFailedSnapshotAge.TotalMilliseconds,
                    duration_ms = started.ElapsedMilliseconds
                }, ex);
            }

            return _snapshot;
        }
    }

    public void Invalidate(string reason = "unspecified")
    {
        lock (_sync)
        {
            var previousGeneration = _generation;
            _generation++;
            _snapshot = null;
            _log?.Write("debug", "hid.inventory.invalidated", nameof(HidDeviceInventory), "success", new
            {
                reason,
                previous_generation = previousGeneration,
                generation = _generation
            });
        }
    }
}

internal sealed class HidInventorySnapshot
{
    public IReadOnlyList<HidDevice> Devices { get; init; } = Array.Empty<HidDevice>();
    public int Generation { get; init; }
    public DateTime CreatedUtc { get; init; }
    public bool IsReliable { get; init; }
    public string Error { get; init; } = "";

    public IEnumerable<HidDevice> Find(int vendorId, int? productId = null)
    {
        return Devices.Where(device => device.VendorID == vendorId && (!productId.HasValue || device.ProductID == productId.Value));
    }

    public string CreatePresenceSignature()
    {
        if (!IsReliable)
            return string.Empty;

        var identities = new List<string>(Devices.Count);
        foreach (var device in Devices)
        {
            try
            {
                identities.Add($"{device.VendorID:X4}:{device.ProductID:X4}:{device.DevicePath}");
            }
            catch
            {
                identities.Add($"{device.VendorID:X4}:{device.ProductID:X4}");
            }
        }

        identities.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", identities);
    }
}
