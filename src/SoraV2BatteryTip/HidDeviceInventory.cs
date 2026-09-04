using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class HidDeviceInventory
{
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private HidInventorySnapshot? _snapshot;
    private int _generation;

    public HidInventorySnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (_snapshot != null && DateTime.UtcNow - _snapshot.CreatedUtc < MaximumSnapshotAge)
                return _snapshot;

            try
            {
                _snapshot = new HidInventorySnapshot
                {
                    Devices = DeviceList.Local.GetHidDevices().ToArray(),
                    Generation = _generation,
                    CreatedUtc = DateTime.UtcNow,
                    IsReliable = true
                };
            }
            catch (Exception ex)
            {
                _snapshot = new HidInventorySnapshot
                {
                    Generation = _generation,
                    CreatedUtc = DateTime.UtcNow,
                    IsReliable = false,
                    Error = ex.GetType().Name
                };
            }

            return _snapshot;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _generation++;
            _snapshot = null;
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
