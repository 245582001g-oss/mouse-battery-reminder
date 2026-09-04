namespace SoraV2BatteryTip;

internal enum ChargingPollingReason
{
    Normal,
    FastCharging,
    SteadyCharging,
    FullyCharged,
    DisplayOff
}

internal sealed record ChargingPollingDevice(
    string DeviceKey,
    bool IsCharging,
    bool IsFullyCharged = false);

internal sealed class ChargingPollingDecision
{
    public TimeSpan Interval { get; init; }
    public ChargingPollingReason Reason { get; init; }
    public DateTime FastUntilUtc { get; init; }
    public IReadOnlyList<string> ChargingDeviceKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NewlyChargingDeviceKeys { get; init; } = Array.Empty<string>();
}

internal sealed class ChargingPollingPolicy
{
    internal static readonly TimeSpan FastChargingInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SteadyChargingInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan FullyChargedInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan FastChargingWindow = TimeSpan.FromMinutes(1);

    private HashSet<string> _chargingDeviceKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _fastUntilUtc = DateTime.MinValue;

    public bool MoveState(string previousDeviceKey, string currentDeviceKey)
    {
        var previousKey = previousDeviceKey?.Trim() ?? string.Empty;
        var currentKey = currentDeviceKey?.Trim() ?? string.Empty;
        if (previousKey.Length == 0
            || currentKey.Length == 0
            || string.Equals(previousKey, currentKey, StringComparison.OrdinalIgnoreCase)
            || !_chargingDeviceKeys.Remove(previousKey))
            return false;

        _chargingDeviceKeys.Add(currentKey);
        return true;
    }

    public ChargingPollingDecision Evaluate(
        IReadOnlyList<ChargingPollingDevice> devices,
        bool displayActive,
        DateTime nowUtc,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);

        var fullByChargingDevice = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var deviceKey = device.DeviceKey?.Trim() ?? string.Empty;
            if (deviceKey.Length == 0 || (!device.IsCharging && !device.IsFullyCharged))
                continue;

            if (fullByChargingDevice.TryGetValue(deviceKey, out var existingFull))
                fullByChargingDevice[deviceKey] = existingFull && device.IsFullyCharged;
            else
                fullByChargingDevice[deviceKey] = device.IsFullyCharged;
        }

        var currentChargingKeys = fullByChargingDevice.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newlyChargingKeys = currentChargingKeys
            .Where(deviceKey => !_chargingDeviceKeys.Contains(deviceKey))
            .OrderBy(deviceKey => deviceKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (newlyChargingKeys.Length > 0)
        {
            var extendedUntilUtc = nowUtc.Add(FastChargingWindow);
            if (extendedUntilUtc > _fastUntilUtc)
                _fastUntilUtc = extendedUntilUtc;
        }

        _chargingDeviceKeys = currentChargingKeys;
        if (currentChargingKeys.Count == 0)
            _fastUntilUtc = DateTime.MinValue;

        var normalInterval = TimeSpan.FromMinutes(Math.Max(1, settings.PollingIntervalMinutes));
        var reason = ChargingPollingReason.Normal;
        var interval = normalInterval;

        if (currentChargingKeys.Count > 0)
        {
            if (fullByChargingDevice.Values.All(isFull => isFull))
            {
                reason = ChargingPollingReason.FullyCharged;
                interval = FullyChargedInterval;
            }
            else if (!displayActive)
            {
                reason = ChargingPollingReason.DisplayOff;
                interval = SteadyChargingInterval;
            }
            else if (nowUtc < _fastUntilUtc)
            {
                reason = ChargingPollingReason.FastCharging;
                interval = FastChargingInterval;
            }
            else
            {
                reason = ChargingPollingReason.SteadyCharging;
                interval = SteadyChargingInterval;
            }
        }

        return new ChargingPollingDecision
        {
            Interval = interval,
            Reason = reason,
            FastUntilUtc = _fastUntilUtc,
            ChargingDeviceKeys = currentChargingKeys.OrderBy(deviceKey => deviceKey, StringComparer.OrdinalIgnoreCase).ToArray(),
            NewlyChargingDeviceKeys = newlyChargingKeys
        };
    }
}
