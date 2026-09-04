namespace SoraV2BatteryTip;

internal sealed class DeviceBatteryStateStore
{
    private readonly Dictionary<string, DeviceStateEntry> _states = new(StringComparer.OrdinalIgnoreCase);

    public DeviceStateUpdate Apply(BatteryReadAllResult result, DateTime nowUtc, TimeSpan staleAfter)
    {
        var freshReadings = new List<BatteryReading>();
        var offlineReadings = new List<BatteryReading>();

        foreach (var batch in result.ProviderResults)
        {
            var existing = _states
                .Where(pair => string.Equals(pair.Value.ProviderName, batch.ProviderName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var candidateIds = new HashSet<string>(
                batch.CandidateDeviceIds.Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reading in batch.Readings)
            {
                var normalized = Normalize(reading, batch.ProviderName, nowUtc);
                var key = CreateKey(batch.ProviderName, normalized);
                returnedKeys.Add(key);

                if (!normalized.IsOnline || normalized.PowerState == DevicePowerState.Offline)
                {
                    if (_states.Remove(key, out var removed))
                        offlineReadings.Add(removed.Reading);
                    continue;
                }

                foreach (var prior in existing)
                {
                    if (string.Equals(prior.Key, key, StringComparison.OrdinalIgnoreCase)
                        || returnedKeys.Contains(prior.Key)
                        || !IsLikelySamePhysicalDevice(prior.Value.Reading, normalized))
                        continue;

                    _states.Remove(prior.Key);
                }

                _states[key] = new DeviceStateEntry
                {
                    ProviderName = batch.ProviderName,
                    Reading = normalized,
                    LastSuccessfulReadUtc = nowUtc,
                    LastAttemptUtc = nowUtc
                };
                freshReadings.Add(normalized);
            }

            foreach (var pair in existing)
            {
                if (returnedKeys.Contains(pair.Key) || !_states.ContainsKey(pair.Key))
                    continue;

                var entry = pair.Value;
                entry.LastAttemptUtc = nowUtc;
                var inventoryFailure = !result.InventoryReliable || !string.IsNullOrWhiteSpace(batch.Error);
                var deviceStillPresent = candidateIds.Count == 0 || candidateIds.Contains(entry.Reading.DeviceId);

                if (!inventoryFailure && (!batch.CandidateFound || !deviceStillPresent))
                {
                    if (_states.Remove(pair.Key, out var removed))
                        offlineReadings.Add(removed.Reading);
                    continue;
                }

                entry.ConsecutiveFailures++;
            }
        }

        var current = _states.Values
            .Select(entry => CreateVisibleReading(entry, nowUtc, staleAfter))
            .OrderBy(reading => reading.BatteryPercentage)
            .ThenBy(reading => reading.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DeviceStateUpdate
        {
            CurrentReadings = current,
            FreshReadings = freshReadings,
            OfflineReadings = offlineReadings,
            HasFreshSamples = freshReadings.Count > 0,
            HasStaleReadings = current.Any(reading => reading.Freshness == BatteryDataFreshness.Stale)
        };
    }

    private static bool IsLikelySamePhysicalDevice(BatteryReading previous, BatteryReading current)
    {
        if (!string.Equals(previous.VendorId, current.VendorId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.Source, current.Source, StringComparison.OrdinalIgnoreCase))
            return false;

        var previousSerial = NormalizeSerial(previous.DeviceSerial);
        var currentSerial = NormalizeSerial(current.DeviceSerial);
        if (previousSerial != null && currentSerial != null)
            return string.Equals(previousSerial, currentSerial, StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(previous.DeviceName)
            && string.Equals(previous.DeviceName.Trim(), current.DeviceName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        var normalized = serial.Trim();
        return normalized.All(character => character is '0' or '-' or ' ')
            ? null
            : normalized;
    }

    private static BatteryReading Normalize(BatteryReading reading, string providerName, DateTime nowUtc)
    {
        var externalPower = reading.ExternalPowerConnected
            ?? (reading.IsCableConnected || reading.IsCharging || reading.IsFullyCharged);
        var powerState = reading.PowerState;
        if (!reading.IsOnline)
            powerState = DevicePowerState.Offline;
        else if (powerState == DevicePowerState.Unknown)
            powerState = reading.IsFullyCharged
                ? DevicePowerState.FullyCharged
                : reading.IsCharging
                    ? DevicePowerState.Charging
                    : externalPower
                        ? DevicePowerState.PendingCharge
                        : DevicePowerState.Discharging;

        return Copy(reading, powerState, externalPower, providerName, BatteryDataFreshness.Fresh, nowUtc, 0);
    }

    private static BatteryReading CreateVisibleReading(DeviceStateEntry entry, DateTime nowUtc, TimeSpan staleAfter)
    {
        var stale = entry.ConsecutiveFailures >= 2 || nowUtc - entry.LastSuccessfulReadUtc >= staleAfter;
        return Copy(
            entry.Reading,
            entry.Reading.PowerState,
            entry.Reading.ExternalPowerConnected,
            entry.ProviderName,
            stale ? BatteryDataFreshness.Stale : BatteryDataFreshness.Fresh,
            entry.LastSuccessfulReadUtc,
            entry.ConsecutiveFailures);
    }

    private static BatteryReading Copy(
        BatteryReading reading,
        DevicePowerState powerState,
        bool? externalPower,
        string providerName,
        BatteryDataFreshness freshness,
        DateTime lastSuccessfulReadUtc,
        int consecutiveFailures)
    {
        return new BatteryReading
        {
            BatteryPercentage = reading.BatteryPercentage,
            HasBatteryPercentage = reading.HasBatteryPercentage,
            IsCharging = reading.IsCharging,
            IsFullyCharged = reading.IsFullyCharged,
            IsOnline = reading.IsOnline,
            IsCableConnected = reading.IsCableConnected,
            PowerState = powerState,
            ExternalPowerConnected = externalPower,
            Freshness = freshness,
            LastSuccessfulReadUtc = lastSuccessfulReadUtc,
            ConsecutiveFailures = consecutiveFailures,
            ProviderName = providerName,
            DeviceName = reading.DeviceName,
            DeviceId = reading.DeviceId,
            DeviceSerial = reading.DeviceSerial,
            VendorId = reading.VendorId,
            ProductId = reading.ProductId,
            Source = reading.Source,
            TimestampUtc = reading.TimestampUtc
        };
    }

    private static string CreateKey(string providerName, BatteryReading reading)
    {
        var identity = !string.IsNullOrWhiteSpace(reading.DeviceId)
            ? reading.DeviceId
            : !string.IsNullOrWhiteSpace(reading.DeviceSerial)
                ? $"{reading.VendorId}:{reading.ProductId}:{reading.DeviceSerial}"
                : $"{reading.VendorId}:{reading.ProductId}:{reading.DeviceName}:{reading.Source}";
        return $"{providerName}|{identity}";
    }

    private sealed class DeviceStateEntry
    {
        public string ProviderName { get; init; } = "";
        public BatteryReading Reading { get; init; } = new();
        public DateTime LastSuccessfulReadUtc { get; init; }
        public DateTime LastAttemptUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
    }
}

internal sealed class DeviceStateUpdate
{
    public IReadOnlyList<BatteryReading> CurrentReadings { get; init; } = Array.Empty<BatteryReading>();
    public IReadOnlyList<BatteryReading> FreshReadings { get; init; } = Array.Empty<BatteryReading>();
    public IReadOnlyList<BatteryReading> OfflineReadings { get; init; } = Array.Empty<BatteryReading>();
    public bool HasFreshSamples { get; init; }
    public bool HasStaleReadings { get; init; }
}
