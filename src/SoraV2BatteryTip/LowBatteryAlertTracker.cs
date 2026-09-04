namespace SoraV2BatteryTip;

internal enum LowBatteryAlertDisposition
{
    Played,
    Reset,
    Suppressed
}

internal sealed record LowBatteryAlertInput(
    string DeviceKey,
    int BatteryPercentage,
    bool HasBatteryPercentage = true,
    bool IsCharging = false,
    bool IsFullyCharged = false,
    bool IsCableConnected = false);

internal sealed record LowBatteryAlertDecision(
    string DeviceKey,
    int BatteryPercentage,
    LowBatteryAlertDisposition Disposition,
    string Reason);

internal sealed class LowBatteryAlertBatchResult
{
    public bool ShouldPlaySound { get; init; }
    public IReadOnlyList<LowBatteryAlertDecision> Decisions { get; init; } = Array.Empty<LowBatteryAlertDecision>();
}

internal sealed class LowBatteryAlertTracker
{
    private readonly Dictionary<string, DeviceAlertState> _states = new(StringComparer.OrdinalIgnoreCase);

    public LowBatteryAlertBatchResult Update(
        IReadOnlyList<LowBatteryAlertInput> devices,
        DateTime nowUtc,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);

        var decisions = new LowBatteryAlertDecision?[devices.Count];
        var pending = new List<PendingAlert>();
        var seenDeviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var threshold = Math.Clamp(settings.AlertThreshold, 0, 100);
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, settings.AlertCooldownMinutes));

        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var deviceKey = device.DeviceKey?.Trim() ?? string.Empty;
            if (deviceKey.Length == 0)
            {
                decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Suppressed, "invalid_device_key");
                continue;
            }

            if (!seenDeviceKeys.Add(deviceKey))
            {
                decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Suppressed, "duplicate_device");
                continue;
            }

            if (!device.HasBatteryPercentage || device.BatteryPercentage is < 1 or > 100)
            {
                decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Suppressed, "invalid_battery");
                continue;
            }

            if (device.IsCableConnected || device.IsCharging || device.IsFullyCharged)
            {
                _states.Remove(deviceKey);
                decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Reset, "external_power");
                continue;
            }

            if (device.BatteryPercentage > threshold)
            {
                _states.Remove(deviceKey);
                decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Reset, "above_threshold");
                continue;
            }

            var alertLevel = AlertLevelFor(device.BatteryPercentage);
            if (_states.TryGetValue(deviceKey, out var state))
            {
                if (alertLevel >= state.LastAlertedLevel)
                {
                    decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Suppressed, "same_or_higher_bucket");
                    continue;
                }

                if (nowUtc < state.LastAlertUtc.Add(cooldown))
                {
                    decisions[index] = Decision(deviceKey, device, LowBatteryAlertDisposition.Suppressed, "cooldown");
                    continue;
                }
            }

            pending.Add(new PendingAlert(index, deviceKey, device, alertLevel));
        }

        var playedIndex = pending
            .OrderBy(item => item.Device.BatteryPercentage)
            .ThenBy(item => item.DeviceKey, StringComparer.OrdinalIgnoreCase)
            .Select(item => (int?)item.Index)
            .FirstOrDefault();

        foreach (var item in pending)
        {
            _states[item.DeviceKey] = new DeviceAlertState(item.AlertLevel, nowUtc);
            decisions[item.Index] = item.Index == playedIndex
                ? Decision(item.DeviceKey, item.Device, LowBatteryAlertDisposition.Played, "low_battery")
                : Decision(item.DeviceKey, item.Device, LowBatteryAlertDisposition.Suppressed, "batch_coalesced");
        }

        return new LowBatteryAlertBatchResult
        {
            ShouldPlaySound = playedIndex.HasValue,
            Decisions = decisions.Select(decision => decision!).ToArray()
        };
    }

    public bool Reset(string deviceKey)
    {
        return !string.IsNullOrWhiteSpace(deviceKey) && _states.Remove(deviceKey.Trim());
    }

    public bool MoveState(string previousDeviceKey, string currentDeviceKey)
    {
        var previousKey = previousDeviceKey?.Trim() ?? string.Empty;
        var currentKey = currentDeviceKey?.Trim() ?? string.Empty;
        if (previousKey.Length == 0
            || currentKey.Length == 0
            || string.Equals(previousKey, currentKey, StringComparison.OrdinalIgnoreCase)
            || !_states.Remove(previousKey, out var previousState))
            return false;

        if (_states.TryGetValue(currentKey, out var currentState))
        {
            previousState = new DeviceAlertState(
                Math.Min(previousState.LastAlertedLevel, currentState.LastAlertedLevel),
                previousState.LastAlertUtc >= currentState.LastAlertUtc ? previousState.LastAlertUtc : currentState.LastAlertUtc);
        }
        _states[currentKey] = previousState;
        return true;
    }

    private static int AlertLevelFor(int batteryPercentage)
    {
        return Math.Clamp((batteryPercentage / 5) * 5, 0, 100);
    }

    private static LowBatteryAlertDecision Decision(
        string deviceKey,
        LowBatteryAlertInput device,
        LowBatteryAlertDisposition disposition,
        string reason)
    {
        return new LowBatteryAlertDecision(deviceKey, device.BatteryPercentage, disposition, reason);
    }

    private sealed record DeviceAlertState(int LastAlertedLevel, DateTime LastAlertUtc);
    private sealed record PendingAlert(int Index, string DeviceKey, LowBatteryAlertInput Device, int AlertLevel);
}
