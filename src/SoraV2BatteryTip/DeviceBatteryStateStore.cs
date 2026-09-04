namespace SoraV2BatteryTip;

internal sealed class DeviceBatteryStateStore
{
    private readonly Dictionary<string, DeviceStateEntry> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppEventLog? _log;

    public DeviceBatteryStateStore(IAppEventLog? log = null) => _log = log;

    public DeviceStateUpdate Apply(BatteryReadAllResult result, DateTime nowUtc, TimeSpan staleAfter)
    {
        var freshReadings = new List<BatteryReading>();
        var offlineReadings = new List<BatteryReading>();
        var rekeys = new List<DeviceStateRekey>();

        foreach (var batch in result.ProviderResults)
        {
            var existing = _states
                .Where(pair => string.Equals(pair.Value.ProviderName, batch.ProviderName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var candidateIds = new HashSet<string>(
                batch.CandidateDeviceIds.Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            var normalizedReadings = batch.Readings
                .Select(reading => Normalize(reading, batch.ProviderName, nowUtc))
                .ToArray();
            var incomingKeys = new HashSet<string>(
                normalizedReadings.Select(reading => CreateKey(batch.ProviderName, reading)),
                StringComparer.OrdinalIgnoreCase);
            var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var claimedPriorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _log?.Write("debug", "device_state.batch_started", nameof(DeviceBatteryStateStore), "success", new
            {
                provider = batch.ProviderName,
                existing_count = existing.Length,
                candidate_count = candidateIds.Count,
                returned_count = normalizedReadings.Length,
                provider_error = batch.Error
            });

            foreach (var normalized in normalizedReadings)
            {
                var key = CreateKey(batch.ProviderName, normalized);
                returnedKeys.Add(key);
                var rekeyRecorded = false;

                if (!normalized.IsOnline || normalized.PowerState == DevicePowerState.Offline)
                {
                    if (_states.Remove(key, out var removed))
                    {
                        var offline = CreateOfflineReading(removed, normalized, batch.ProviderName);
                        offlineReadings.Add(offline);
                        _log?.Write("info", "device.state_offline", nameof(DeviceBatteryStateStore), "success", new
                        {
                            reason = "provider_reported_offline",
                            previous_battery_percentage = removed.Reading.BatteryPercentage,
                            previous_power_state = removed.Reading.PowerState.ToString(),
                            provider_reported_battery_percentage = normalized.BatteryPercentage,
                            provider_reported_has_battery_percentage = normalized.HasBatteryPercentage,
                            fact_basis = "provider_offline_reading",
                            battery_fact = "last_known_online_sample"
                        }, reading: offline);
                    }
                    continue;
                }

                _states.TryGetValue(key, out var previousEntry);
                if (!_states.ContainsKey(key))
                {
                    var prior = FindRekeyCandidate(
                        existing,
                        normalized,
                        candidateIds,
                        incomingKeys,
                        claimedPriorKeys);
                    if (prior.HasValue && _states.Remove(prior.Value.Key, out var removed))
                    {
                        previousEntry = removed;
                        claimedPriorKeys.Add(prior.Value.Key);
                        rekeys.Add(new DeviceStateRekey
                        {
                            ProviderName = batch.ProviderName,
                            PreviousKey = prior.Value.Key,
                            CurrentKey = key,
                            PreviousReading = removed.Reading,
                            CurrentReading = normalized
                        });
                        rekeyRecorded = true;
                        _log?.Write("info", "device.identity_rekeyed", nameof(DeviceBatteryStateStore), "success", new
                        {
                            provider = batch.ProviderName,
                            previous_device = _log.DeviceToken(removed.Reading),
                            current_device = _log.DeviceToken(normalized),
                            reason = DeviceIdentity.NormalizeSerial(normalized.DeviceSerial) != null ? "stable_serial" : "unique_path_migration"
                        }, reading: normalized);
                    }
                }

                if (previousEntry != null
                    && !rekeyRecorded
                    && !string.Equals(
                        DeviceIdentity.CreateRuntimeKey(previousEntry.Reading),
                        DeviceIdentity.CreateRuntimeKey(normalized),
                        StringComparison.OrdinalIgnoreCase))
                {
                    rekeys.Add(new DeviceStateRekey
                    {
                        ProviderName = batch.ProviderName,
                        PreviousKey = key,
                        CurrentKey = key,
                        PreviousReading = previousEntry.Reading,
                        CurrentReading = normalized
                    });
                    _log?.Write("info", "device.identity_rekeyed", nameof(DeviceBatteryStateStore), "success", new
                    {
                        provider = batch.ProviderName,
                        previous_device = _log.DeviceToken(previousEntry.Reading),
                        current_device = _log.DeviceToken(normalized),
                        reason = "runtime_identity_changed"
                    }, reading: normalized);
                }

                _states[key] = new DeviceStateEntry
                {
                    ProviderName = batch.ProviderName,
                    Reading = normalized,
                    LastSuccessfulReadUtc = nowUtc,
                    LastAttemptUtc = nowUtc
                };
                freshReadings.Add(normalized);
                _log?.Write("debug", "device.reading_observed", nameof(DeviceBatteryStateStore), "success", new
                {
                    battery_percentage = normalized.BatteryPercentage,
                    cable_connected = normalized.IsCableConnected,
                    charging = normalized.IsCharging,
                    fully_charged = normalized.IsFullyCharged,
                    external_power_connected = normalized.ExternalPowerConnected,
                    power_state = normalized.PowerState.ToString(),
                    provider = batch.ProviderName,
                    fact_basis = "provider_reading"
                }, reading: normalized);

                if (previousEntry == null)
                {
                    _log?.Write("info", "device.state_added", nameof(DeviceBatteryStateStore), "success", reading: normalized);
                }
                else
                {
                    if (previousEntry.ConsecutiveFailures > 0)
                    {
                        _log?.Write("info", "device.state_recovered", nameof(DeviceBatteryStateStore), "success", new
                        {
                            previous_failure_count = previousEntry.ConsecutiveFailures
                        }, reading: normalized);
                    }
                    if (HasStateChanged(previousEntry.Reading, normalized))
                    {
                        _log?.Write("info", "device.state_changed", nameof(DeviceBatteryStateStore), "success", new
                        {
                            previous_battery_percentage = previousEntry.Reading.BatteryPercentage,
                            battery_percentage = normalized.BatteryPercentage,
                            previous_power_state = previousEntry.Reading.PowerState.ToString(),
                            power_state = normalized.PowerState.ToString(),
                            previous_cable_connected = previousEntry.Reading.IsCableConnected,
                            cable_connected = normalized.IsCableConnected,
                            previous_charging = previousEntry.Reading.IsCharging,
                            charging = normalized.IsCharging,
                            previous_fully_charged = previousEntry.Reading.IsFullyCharged,
                            fully_charged = normalized.IsFullyCharged
                        }, reading: normalized);
                        LogSpecificTransitions(previousEntry.Reading, normalized);
                    }
                }
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
                    {
                        var offline = CreateOfflineReading(removed, observed: null, batch.ProviderName);
                        offlineReadings.Add(offline);
                        _log?.Write("info", "device.state_offline", nameof(DeviceBatteryStateStore), "success", new
                        {
                            reason = batch.CandidateFound ? "candidate_removed" : "provider_no_candidate",
                            previous_battery_percentage = removed.Reading.BatteryPercentage,
                            previous_power_state = removed.Reading.PowerState.ToString(),
                            fact_basis = "inventory_candidate_absence",
                            battery_fact = "last_known_online_sample"
                        }, reading: offline);
                    }
                    continue;
                }

                var wasStale = entry.ConsecutiveFailures >= 2 || nowUtc - entry.LastSuccessfulReadUtc >= staleAfter;
                entry.ConsecutiveFailures++;
                var isStale = entry.ConsecutiveFailures >= 2 || nowUtc - entry.LastSuccessfulReadUtc >= staleAfter;
                _log?.Write("debug", "device.read_failed", nameof(DeviceBatteryStateStore), "degraded", new
                {
                    failure_count = entry.ConsecutiveFailures,
                    inventory_failure = !result.InventoryReliable,
                    provider_error = batch.Error,
                    candidate_still_present = deviceStillPresent
                }, reading: entry.Reading);
                if (!wasStale && isStale)
                {
                    _log?.Write("warn", "device.state_stale", nameof(DeviceBatteryStateStore), "degraded", new
                    {
                        failure_count = entry.ConsecutiveFailures,
                        last_success_utc = entry.LastSuccessfulReadUtc
                    }, reading: entry.Reading);
                }
            }
        }

        var current = _states.Values
            .Select(entry => CreateVisibleReading(entry, nowUtc, staleAfter))
            .OrderBy(reading => reading.BatteryPercentage)
            .ThenBy(reading => reading.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _log?.Write("debug", "device_state.apply_completed", nameof(DeviceBatteryStateStore), "success", new
        {
            current_count = current.Length,
            fresh_count = freshReadings.Count,
            offline_count = offlineReadings.Count,
            rekey_count = rekeys.Count,
            stale_count = current.Count(reading => reading.Freshness == BatteryDataFreshness.Stale)
        });
        return new DeviceStateUpdate
        {
            CurrentReadings = current,
            FreshReadings = freshReadings,
            OfflineReadings = offlineReadings,
            Rekeys = rekeys,
            HasFreshSamples = freshReadings.Count > 0,
            HasStaleReadings = current.Any(reading => reading.Freshness == BatteryDataFreshness.Stale)
        };
    }

    private static bool HasStateChanged(BatteryReading previous, BatteryReading current)
    {
        return previous.BatteryPercentage != current.BatteryPercentage
            || previous.HasBatteryPercentage != current.HasBatteryPercentage
            || previous.IsCharging != current.IsCharging
            || previous.IsFullyCharged != current.IsFullyCharged
            || previous.IsOnline != current.IsOnline
            || previous.IsCableConnected != current.IsCableConnected
            || previous.PowerState != current.PowerState
            || previous.ExternalPowerConnected != current.ExternalPowerConnected;
    }

    private void LogSpecificTransitions(BatteryReading previous, BatteryReading current)
    {
        if (_log == null)
            return;

        var previousExternalPower = DevicePowerSemantics.IsExternallyPowered(previous);
        var currentExternalPower = DevicePowerSemantics.IsExternallyPowered(current);
        if (previousExternalPower != currentExternalPower)
        {
            _log.Write("info", currentExternalPower ? "device.cable_connected" : "device.cable_disconnected", nameof(DeviceBatteryStateStore), "success", new
            {
                battery_percentage = current.BatteryPercentage,
                previous_battery_percentage = previous.BatteryPercentage,
                observed_cable_flag = current.IsCableConnected,
                observed_external_power = current.ExternalPowerConnected,
                power_state = current.PowerState.ToString(),
                fact_basis = "derived_from_consecutive_provider_readings"
            }, reading: current);
        }

        var previousCharging = previous.IsCharging || previous.PowerState == DevicePowerState.Charging;
        var currentCharging = current.IsCharging || current.PowerState == DevicePowerState.Charging;
        if (previousCharging != currentCharging)
        {
            _log.Write("info", currentCharging ? "device.charging_started" : "device.charging_stopped", nameof(DeviceBatteryStateStore), "success", new
            {
                battery_percentage = current.BatteryPercentage,
                previous_battery_percentage = previous.BatteryPercentage,
                cable_connected = currentExternalPower,
                fact_basis = "derived_from_consecutive_provider_readings"
            }, reading: current);
        }

        var previousFull = previous.IsFullyCharged || previous.PowerState == DevicePowerState.FullyCharged;
        var currentFull = current.IsFullyCharged || current.PowerState == DevicePowerState.FullyCharged;
        if (!previousFull && currentFull)
        {
            _log.Write("info", "device.full_charge_reached", nameof(DeviceBatteryStateStore), "success", new
            {
                battery_percentage = current.BatteryPercentage,
                fact_basis = "derived_from_consecutive_provider_readings"
            }, reading: current);
        }

        if (previous.BatteryPercentage != current.BatteryPercentage)
        {
            _log.Write("info", "device.battery_changed", nameof(DeviceBatteryStateStore), "success", new
            {
                previous_battery_percentage = previous.BatteryPercentage,
                battery_percentage = current.BatteryPercentage,
                delta = current.BatteryPercentage - previous.BatteryPercentage,
                cable_connected = currentExternalPower,
                charging = currentCharging,
                fact_basis = "derived_from_consecutive_provider_readings"
            }, reading: current);
        }
    }

    private static KeyValuePair<string, DeviceStateEntry>? FindRekeyCandidate(
        IReadOnlyList<KeyValuePair<string, DeviceStateEntry>> existing,
        BatteryReading current,
        IReadOnlySet<string> candidateIds,
        IReadOnlySet<string> incomingKeys,
        IReadOnlySet<string> claimedPriorKeys)
    {
        var currentSerial = DeviceIdentity.NormalizeSerial(current.DeviceSerial);
        var candidates = existing
            .Where(pair => !incomingKeys.Contains(pair.Key)
                && !claimedPriorKeys.Contains(pair.Key)
                && SameHardwareFamily(pair.Value.Reading, current))
            .Where(pair => currentSerial != null
                ? string.Equals(
                    DeviceIdentity.NormalizeSerial(pair.Value.Reading.DeviceSerial),
                    currentSerial,
                    StringComparison.OrdinalIgnoreCase)
                : IsUniqueMissingPathFallback(pair.Value.Reading, current, candidateIds))
            .Take(2)
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool SameHardwareFamily(BatteryReading previous, BatteryReading current)
    {
        return string.Equals(previous.VendorId?.Trim(), current.VendorId?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.ProductId?.Trim(), current.ProductId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUniqueMissingPathFallback(
        BatteryReading previous,
        BatteryReading current,
        IReadOnlySet<string> candidateIds)
    {
        if (DeviceIdentity.NormalizeSerial(previous.DeviceSerial) != null
            || candidateIds.Count == 0
            || string.IsNullOrWhiteSpace(previous.DeviceId)
            || string.IsNullOrWhiteSpace(current.DeviceId)
            || !candidateIds.Contains(current.DeviceId)
            || candidateIds.Contains(previous.DeviceId))
            return false;

        return !string.IsNullOrWhiteSpace(previous.DeviceName)
            && !string.IsNullOrWhiteSpace(current.DeviceName)
            && string.Equals(previous.Source?.Trim(), current.Source?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.DeviceName.Trim(), current.DeviceName.Trim(), StringComparison.OrdinalIgnoreCase);
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

    private static BatteryReading CreateOfflineReading(
        DeviceStateEntry previous,
        BatteryReading? observed,
        string providerName)
    {
        var lastKnown = previous.Reading;
        return new BatteryReading
        {
            BatteryPercentage = lastKnown.BatteryPercentage,
            HasBatteryPercentage = lastKnown.HasBatteryPercentage,
            IsCharging = false,
            IsFullyCharged = false,
            IsOnline = false,
            IsCableConnected = false,
            PowerState = DevicePowerState.Offline,
            ExternalPowerConnected = false,
            Freshness = BatteryDataFreshness.Stale,
            LastSuccessfulReadUtc = previous.LastSuccessfulReadUtc,
            ConsecutiveFailures = previous.ConsecutiveFailures,
            ProviderName = providerName,
            DeviceName = !string.IsNullOrWhiteSpace(observed?.DeviceName) ? observed.DeviceName : lastKnown.DeviceName,
            DeviceId = !string.IsNullOrWhiteSpace(observed?.DeviceId) ? observed.DeviceId : lastKnown.DeviceId,
            DeviceSerial = !string.IsNullOrWhiteSpace(observed?.DeviceSerial) ? observed.DeviceSerial : lastKnown.DeviceSerial,
            VendorId = !string.IsNullOrWhiteSpace(observed?.VendorId) ? observed.VendorId : lastKnown.VendorId,
            ProductId = !string.IsNullOrWhiteSpace(observed?.ProductId) ? observed.ProductId : lastKnown.ProductId,
            Source = !string.IsNullOrWhiteSpace(observed?.Source) ? observed.Source : lastKnown.Source,
            TimestampUtc = observed?.TimestampUtc ?? DateTime.UtcNow
        };
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
        var serial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial);
        var identity = !string.IsNullOrWhiteSpace(reading.DeviceId)
            ? reading.DeviceId
            : serial != null
                ? $"{reading.VendorId}:{reading.ProductId}:{serial}"
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
    public IReadOnlyList<DeviceStateRekey> Rekeys { get; init; } = Array.Empty<DeviceStateRekey>();
    public bool HasFreshSamples { get; init; }
    public bool HasStaleReadings { get; init; }
}

internal sealed class DeviceStateRekey
{
    public string ProviderName { get; init; } = "";
    public string PreviousKey { get; init; } = "";
    public string CurrentKey { get; init; } = "";
    public BatteryReading PreviousReading { get; init; } = new();
    public BatteryReading CurrentReading { get; init; } = new();
}
