namespace SoraV2BatteryTip;

// A receiver can briefly report 100%/charging after its paired USB mouse disappears.
// Keep those raw observations in the flight log, but do not publish them as facts.
internal sealed class SoraDisconnectRecovery
{
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MinimumSettlingTime = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ConfirmationSpacing = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, Recovery> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppEventLog? _log;

    public SoraDisconnectRecovery(IAppEventLog? log = null) => _log = log;

    public bool BeginRemoval(string path, IReadOnlyList<BatteryReading> readings, DateTime nowUtc)
    {
        var changed = false;
        foreach (var reading in readings.Where(reading =>
                     reading.ConnectionTransport == DeviceConnectionTransport.WiredUsb
                     && reading.LogicalDeviceId.StartsWith("ninjutso-sora-v2:", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(reading.DeviceId, path, StringComparison.OrdinalIgnoreCase)))
        {
            var key = DeviceIdentity.CreateRuntimeKey(reading);
            if (_pending.ContainsKey(key))
                continue;
            _pending[key] = new Recovery(reading, nowUtc);
            changed = true;
            _log?.Write("info", "device.disconnect_verification_started", nameof(SoraDisconnectRecovery), "success",
                new { maximum_seconds = MaximumDuration.TotalSeconds }, reading: reading);
        }
        return changed;
    }

    public void ObserveArrival(string path)
    {
        foreach (var pair in _pending.Where(pair => string.Equals(
                     pair.Value.Previous.DeviceId, path, StringComparison.OrdinalIgnoreCase)).ToArray())
            Complete(pair.Key, "wired_reconnected", pair.Value.Previous);
    }

    public void MoveState(string previousKey, string currentKey, bool preserve)
    {
        if (string.Equals(previousKey, currentKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!preserve) _pending.Remove(previousKey);
            return;
        }
        if (_pending.Remove(previousKey, out var state) && preserve)
            _pending[currentKey] = state;
    }

    public void Observe(IReadOnlyList<BatteryReading> freshReadings, DateTime nowUtc)
    {
        foreach (var pair in _pending.ToArray())
        {
            var state = pair.Value;
            if (nowUtc - state.StartedUtc >= MaximumDuration)
            {
                Complete(pair.Key, "verification_timeout", state.Previous);
                continue;
            }
            var reading = freshReadings.FirstOrDefault(reading => string.Equals(
                DeviceIdentity.CreateRuntimeKey(reading), pair.Key, StringComparison.OrdinalIgnoreCase));
            if (reading == null || !reading.IsOnline || !reading.HasBatteryPercentage
                || reading.Freshness != BatteryDataFreshness.Fresh
                || reading.BatteryPercentage is < 1 or > 100)
            {
                state.FirstUnpoweredUtc = null;
                continue;
            }
            if (reading.ConnectionTransport != DeviceConnectionTransport.Receiver
                || DevicePowerSemantics.IsExternallyPowered(reading))
            {
                state.FirstUnpoweredUtc = null;
                continue;
            }
            state.FirstUnpoweredUtc ??= nowUtc;
            if (nowUtc - state.StartedUtc >= MinimumSettlingTime
                && nowUtc - state.FirstUnpoweredUtc.Value >= ConfirmationSpacing)
                Complete(pair.Key, "receiver_discharge_confirmed", reading);
        }
    }

    public bool IsActive(DateTime nowUtc) => _pending.Values.Any(state => nowUtc - state.StartedUtc < MaximumDuration);

    public bool IsPending(BatteryReading reading, DateTime nowUtc) =>
        _pending.TryGetValue(DeviceIdentity.CreateRuntimeKey(reading), out var state)
        && nowUtc - state.StartedUtc < MaximumDuration;

    public BatteryReading Project(BatteryReading reading, DateTime nowUtc)
    {
        if (!IsPending(reading, nowUtc)) return reading;
        var previous = _pending[DeviceIdentity.CreateRuntimeKey(reading)].Previous;
        return new BatteryReading
        {
            BatteryPercentage = previous.BatteryPercentage,
            HasBatteryPercentage = previous.HasBatteryPercentage,
            IsOnline = reading.IsOnline,
            IsCharging = false,
            IsFullyCharged = false,
            IsCableConnected = false,
            ExternalPowerConnected = null,
            PowerState = DevicePowerState.PendingDischarge,
            Freshness = BatteryDataFreshness.Stale,
            LastSuccessfulReadUtc = previous.LastSuccessfulReadUtc,
            ConsecutiveFailures = reading.ConsecutiveFailures,
            ProviderName = reading.ProviderName,
            LogicalDeviceId = reading.LogicalDeviceId,
            HistoryDeviceKey = reading.HistoryDeviceKey,
            AssociationReceiverHistoryKey = reading.AssociationReceiverHistoryKey,
            AssociationReceiverSerial = reading.AssociationReceiverSerial,
            ConnectionTransport = reading.ConnectionTransport,
            HistoryAnchorEvidence = reading.HistoryAnchorEvidence,
            ResolvedDeviceIds = reading.ResolvedDeviceIds,
            DeviceName = reading.DeviceName,
            DeviceId = reading.DeviceId,
            DeviceSerial = reading.DeviceSerial,
            VendorId = reading.VendorId,
            ProductId = reading.ProductId,
            Source = reading.Source,
            TimestampUtc = reading.TimestampUtc
        };
    }

    private void Complete(string key, string reason, BatteryReading reading)
    {
        _pending.Remove(key);
        var timedOut = reason == "verification_timeout";
        _log?.Write(timedOut ? "warn" : "info", "device.disconnect_verification_completed",
            nameof(SoraDisconnectRecovery), timedOut ? "degraded" : "success", new { reason }, reading: reading);
    }

    private sealed class Recovery(BatteryReading previous, DateTime startedUtc)
    {
        public BatteryReading Previous { get; } = previous;
        public DateTime StartedUtc { get; } = startedUtc;
        public DateTime? FirstUnpoweredUtc { get; set; }
    }
}
