using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoraV2BatteryTip;

internal sealed class BatteryHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string DeviceKey { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string LogicalDeviceId { get; set; } = "";
    public string AssociationReceiverHistoryKey { get; set; } = "";
    public string AssociationReceiverSerial { get; set; } = "";
    public string ReceiverCanonicalHistoryKey { get; set; } = "";
    public string ConnectionTransport { get; set; } = "";
    public string HistoryAnchorEvidence { get; set; } = "";
    public IReadOnlyList<string> ResolvedDeviceIds { get; set; } = Array.Empty<string>();
    public string DeviceSerial { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public bool HasBatteryPercentage { get; set; } = true;
    public int BatteryPercentage { get; set; }
    public bool IsCharging { get; set; }
    public bool IsCableConnected { get; set; }
    public string State { get; set; } = "";
    public string Source { get; set; } = "";
}

internal sealed class BatteryHistoryDevice
{
    public string DeviceKey { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public DateTime LastSeenUtc { get; init; }
    public int LastBatteryPercentage { get; init; }
    public bool IsCharging { get; init; }
}

internal sealed class BatteryHistoryStore
{
    private const string SoraReceiverLogicalPrefix = "ninjutso-sora-v2:receiver:";
    private const string SoraWiredLogicalPrefix = "ninjutso-sora-v2:wired:";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly AppPaths _paths;
    private readonly IAppEventLog? _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, BatteryHistoryEntry> _lastByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SoraHistoryAnchor>> _soraHistoryRelationsByPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _lastCacheLoaded;
    private DateTime _lastPruneDateUtc = DateTime.MinValue;

    public BatteryHistoryStore(AppPaths paths, IAppEventLog? log = null)
    {
        _paths = paths;
        _log = log;
    }

    public void Append(BatteryReading reading)
    {
        if (!reading.HasBatteryPercentage || reading.BatteryPercentage is < 1 or > 100)
            return;

        var deviceKey = CreateDeviceKey(reading);
        if (string.IsNullOrWhiteSpace(deviceKey))
            return;

        AppendRaw(new BatteryHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            DeviceKey = deviceKey,
            DeviceName = DisplayDeviceName(reading),
            LogicalDeviceId = reading.LogicalDeviceId,
            AssociationReceiverHistoryKey = reading.AssociationReceiverHistoryKey,
            AssociationReceiverSerial = DeviceIdentity.NormalizeSerial(reading.AssociationReceiverSerial) ?? "",
            ReceiverCanonicalHistoryKey = IsSoraReceiverDeviceKey(deviceKey) ? deviceKey : "",
            ConnectionTransport = reading.ConnectionTransport.ToString(),
            HistoryAnchorEvidence = reading.HistoryAnchorEvidence.ToString(),
            ResolvedDeviceIds = DeviceIdentity.ResolvedDeviceIds(reading),
            DeviceSerial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial) ?? "",
            VendorId = NormalizeId(reading.VendorId),
            ProductId = NormalizeId(reading.ProductId),
            BatteryPercentage = reading.BatteryPercentage,
            IsCharging = DevicePowerSemantics.IsExternallyPowered(reading),
            IsCableConnected = DevicePowerSemantics.IsExternallyPowered(reading),
            State = "sample",
            Source = reading.Source
        }, CreateLegacyAliasKeys(reading));
    }

    public void MarkOffline(BatteryReading reading)
    {
        var deviceKey = CreateDeviceKey(reading);
        if (string.IsNullOrWhiteSpace(deviceKey))
            return;

        AppendRaw(new BatteryHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            DeviceKey = deviceKey,
            DeviceName = DisplayDeviceName(reading),
            LogicalDeviceId = reading.LogicalDeviceId,
            AssociationReceiverHistoryKey = reading.AssociationReceiverHistoryKey,
            AssociationReceiverSerial = DeviceIdentity.NormalizeSerial(reading.AssociationReceiverSerial) ?? "",
            ReceiverCanonicalHistoryKey = IsSoraReceiverDeviceKey(deviceKey) ? deviceKey : "",
            ConnectionTransport = reading.ConnectionTransport.ToString(),
            HistoryAnchorEvidence = reading.HistoryAnchorEvidence.ToString(),
            ResolvedDeviceIds = DeviceIdentity.ResolvedDeviceIds(reading),
            DeviceSerial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial) ?? "",
            VendorId = NormalizeId(reading.VendorId),
            ProductId = NormalizeId(reading.ProductId),
            BatteryPercentage = Math.Clamp(reading.BatteryPercentage, 1, 100),
            IsCharging = false,
            IsCableConnected = false,
            State = "device_offline",
            Source = reading.Source
        }, CreateLegacyAliasKeys(reading), migrateLegacyAliases: false);
    }

    public void RecordIdentityEvidence(DeviceIdentityObservation observation)
    {
        if (observation.HistoryAnchorEvidence is not (HistoryAnchorEvidence.ConfirmReceiver
            or HistoryAnchorEvidence.RejectPersistedReceiver)
            || string.IsNullOrWhiteSpace(observation.HistoryDeviceKey)
            || observation.ResolvedDeviceIds.Count == 0)
            return;

        lock (_sync)
        {
            try
            {
                _paths.Ensure();
                PruneOldEntriesIfDue();
                EnsureLastCacheLoaded();
                var entry = new BatteryHistoryEntry
                {
                    TimestampUtc = observation.TimestampUtc,
                    DeviceKey = observation.HistoryDeviceKey,
                    DeviceName = string.IsNullOrWhiteSpace(observation.DeviceName)
                        ? observation.Source
                        : observation.DeviceName,
                    LogicalDeviceId = observation.LogicalDeviceId,
                    AssociationReceiverHistoryKey = observation.AssociationReceiverHistoryKey,
                    AssociationReceiverSerial = DeviceIdentity.NormalizeSerial(observation.AssociationReceiverSerial) ?? "",
                    ReceiverCanonicalHistoryKey = observation.HistoryAnchorEvidence == HistoryAnchorEvidence.ConfirmReceiver
                        && IsSoraReceiverDeviceKey(observation.HistoryDeviceKey)
                            ? observation.HistoryDeviceKey
                            : "",
                    ConnectionTransport = NinjutsoSoraTransportPolicy.GetTransport(
                        ParseProductId(observation.ProductId)).ToString(),
                    HistoryAnchorEvidence = observation.HistoryAnchorEvidence.ToString(),
                    ResolvedDeviceIds = IdentityEvidencePaths(observation),
                    DeviceSerial = DeviceIdentity.NormalizeSerial(observation.DeviceSerial) ?? "",
                    VendorId = NormalizeId(observation.VendorId),
                    ProductId = NormalizeId(observation.ProductId),
                    HasBatteryPercentage = false,
                    State = observation.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver
                        ? "identity_anchor_rejected"
                        : "identity_anchor_confirmed",
                    Source = observation.Source
                };
                PrepareReceiverCanonicalIdentity(entry);
                if (IdentityEvidenceAlreadyCurrent(entry))
                {
                    _log?.Write("debug", "history.identity_evidence_duplicate", nameof(BatteryHistoryStore), "skipped", new
                    {
                        device_key = entry.DeviceKey,
                        receiver_association_key = entry.AssociationReceiverHistoryKey,
                        history_anchor_evidence = entry.HistoryAnchorEvidence,
                        resolved_device_ids = entry.ResolvedDeviceIds
                    }, reading: IdentityObservationForLog(observation));
                    return;
                }

                File.AppendAllText(_paths.HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                RememberSoraHistoryAnchor(entry);
                _log?.Write("info", "history.identity_evidence_recorded", nameof(BatteryHistoryStore), "success", new
                    {
                        device_key = entry.DeviceKey,
                        receiver_association_key = entry.AssociationReceiverHistoryKey,
                        history_event = entry.State,
                    history_anchor_evidence = entry.HistoryAnchorEvidence,
                    resolved_device_ids = entry.ResolvedDeviceIds,
                    battery_fact_present = false
                }, reading: IdentityObservationForLog(observation));
            }
            catch (Exception ex)
            {
                _log?.Write("error", "history.identity_evidence_failed", nameof(BatteryHistoryStore), "failure", exception: ex);
            }
        }
    }

    public IReadOnlyList<BatteryHistoryEntry> ReadLast(TimeSpan range, string? deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            return Array.Empty<BatteryHistoryEntry>();

        lock (_sync)
        {
            PruneOldEntriesIfDue();
            var fromUtc = DateTime.UtcNow - range;
            return ReadEntries(fromUtc)
                .Where(entry => entry.HasBatteryPercentage)
                .Where(entry => string.Equals(entry.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.TimestampUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<BatteryHistoryDevice> ReadDevices(TimeSpan range)
    {
        lock (_sync)
        {
            PruneOldEntriesIfDue();
            var fromUtc = DateTime.UtcNow - range;
            var latestByDevice = new Dictionary<string, BatteryHistoryEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ReadEntries(fromUtc))
            {
                if (!entry.HasBatteryPercentage)
                    continue;
                if (!latestByDevice.TryGetValue(entry.DeviceKey, out var previous) || entry.TimestampUtc > previous.TimestampUtc)
                    latestByDevice[entry.DeviceKey] = entry;
            }

            return latestByDevice.Values
                .OrderByDescending(entry => entry.TimestampUtc)
                .Select(entry => new BatteryHistoryDevice
                {
                    DeviceKey = entry.DeviceKey,
                    DeviceName = entry.DeviceName,
                    VendorId = entry.VendorId,
                    ProductId = entry.ProductId,
                    LastSeenUtc = entry.TimestampUtc,
                    LastBatteryPercentage = entry.BatteryPercentage,
                    IsCharging = entry.IsCharging || entry.IsCableConnected
                })
                .ToArray();
        }
    }

    private void AppendRaw(
        BatteryHistoryEntry entry,
        IReadOnlyCollection<string> legacyAliasKeys,
        bool migrateLegacyAliases = true)
    {
        lock (_sync)
        {
            try
            {
                _paths.Ensure();
                PruneOldEntriesIfDue();
                EnsureLastCacheLoaded();
                PrepareReceiverCanonicalIdentity(entry);
                AdoptKnownSoraReceiverHistoryKey(entry);
                if (migrateLegacyAliases)
                    MigrateLegacyEntries(entry, legacyAliasKeys);
                EnsureLastCacheLoaded();
                _lastByDevice.TryGetValue(entry.DeviceKey, out var previous);
                entry.State = DetermineEvent(previous, entry);
                if ((string.IsNullOrEmpty(entry.State) || entry.State == "heartbeat")
                    && RestoresRejectedSoraAnchor(entry))
                    entry.State = "transport_change";
                if (string.IsNullOrEmpty(entry.State))
                {
                    _log?.Write("debug", "history.suppressed_duplicate", nameof(BatteryHistoryStore), "skipped", new
                    {
                        device_key = entry.DeviceKey,
                        battery_percentage = entry.BatteryPercentage
                    });
                    return;
                }

                File.AppendAllText(_paths.HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                _lastByDevice[entry.DeviceKey] = entry;
                RememberSoraHistoryAnchor(entry);
                _log?.Write("debug", "history.appended", nameof(BatteryHistoryStore), "success", new
                {
                    device_key = entry.DeviceKey,
                    history_event = entry.State,
                    battery_percentage = entry.BatteryPercentage,
                    charging = entry.IsCharging,
                    cable_connected = entry.IsCableConnected
                });
            }
            catch (Exception ex)
            {
                _log?.Write("error", "history.append_failed", nameof(BatteryHistoryStore), "failure", exception: ex);
            }
        }
    }

    private void EnsureLastCacheLoaded()
    {
        if (_lastCacheLoaded)
            return;

        _lastByDevice.Clear();
        _soraHistoryRelationsByPath.Clear();
        foreach (var entry in ReadEntries(DateTime.MinValue))
        {
            if (entry.HasBatteryPercentage
                && (!_lastByDevice.TryGetValue(entry.DeviceKey, out var previous) || entry.TimestampUtc > previous.TimestampUtc))
                _lastByDevice[entry.DeviceKey] = entry;
            RememberSoraHistoryAnchor(entry);
        }
        _lastCacheLoaded = true;
    }

    private void MigrateLegacyEntries(BatteryHistoryEntry current, IReadOnlyCollection<string> legacyAliasKeys)
    {
        if (!File.Exists(_paths.HistoryPath))
            return;

        var legacyKeys = new HashSet<string>(legacyAliasKeys, StringComparer.OrdinalIgnoreCase)
        {
            $"{current.VendorId}:{current.ProductId}:{NormalizeForKey(current.DeviceName)}"
        };
        legacyKeys.Remove(current.DeviceKey);
        legacyKeys.RemoveWhere(key => !_lastByDevice.ContainsKey(key));
        if (legacyKeys.Count == 0)
            return;

        var entries = ReadEntries(DateTime.MinValue).ToArray();
        var migratedCount = 0;
        foreach (var entry in entries)
        {
            if (!legacyKeys.Contains(entry.DeviceKey))
                continue;
            entry.DeviceKey = current.DeviceKey;
            migratedCount++;
        }

        if (migratedCount == 0)
            return;

        var temporaryPath = _paths.HistoryPath + ".migrate.tmp";
        File.WriteAllLines(temporaryPath, entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
        File.Move(temporaryPath, _paths.HistoryPath, overwrite: true);
        _lastCacheLoaded = false;
        _log?.Write("info", "history.migrated", nameof(BatteryHistoryStore), "success", new
        {
            device_key = current.DeviceKey,
            migrated_entry_count = migratedCount,
            legacy_alias_count = legacyKeys.Count
        });
    }

    private void AdoptKnownSoraReceiverHistoryKey(BatteryHistoryEntry current)
    {
        var receiverLogical = current.LogicalDeviceId.StartsWith(SoraReceiverLogicalPrefix, StringComparison.OrdinalIgnoreCase);
        var wiredLogical = current.LogicalDeviceId.StartsWith(SoraWiredLogicalPrefix, StringComparison.OrdinalIgnoreCase);
        if ((!receiverLogical && !wiredLogical) || current.ResolvedDeviceIds.Count == 0)
            return;

        if (wiredLogical
            && !string.Equals(
                current.HistoryAnchorEvidence,
                HistoryAnchorEvidence.RecoverPersistedReceiver.ToString(),
                StringComparison.OrdinalIgnoreCase))
            return;

        if (receiverLogical && IsSoraReceiverSerialDeviceKey(current.DeviceKey))
            return;

        var persistedCandidates = current.ResolvedDeviceIds
            .SelectMany(SoraHistoryAnchorsForPath)
            .Distinct()
            .ToArray();
        var latestFactsByAssociation = persistedCandidates
            .GroupBy(
                anchor => anchor.ReceiverAssociationKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(anchor => anchor.TimestampUtc)
                .First())
            .ToArray();
        var knownCandidates = latestFactsByAssociation
            .Where(anchor => !anchor.IsRejected)
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor.CanonicalHistoryDeviceKey))
            .ToArray();
        var known = (receiverLogical
                ? knownCandidates.Where(anchor => IsSoraReceiverSerialDeviceKey(anchor.CanonicalHistoryDeviceKey))
                : knownCandidates)
            .OrderByDescending(anchor => anchor.TimestampUtc)
            .FirstOrDefault();
        if (wiredLogical && known == null && latestFactsByAssociation.Any(anchor => anchor.IsRejected))
        {
            _log?.Write("info", "history.identity_recovery_blocked", nameof(BatteryHistoryStore), "success", new
            {
                provisional_device_key = current.DeviceKey,
                reason = "scoped_receiver_pairing_mismatch",
                rejected_relation_count = latestFactsByAssociation.Count(anchor => anchor.IsRejected),
                resolved_device_ids = current.ResolvedDeviceIds
            });
        }
        if (known == null
            || (string.Equals(known.CanonicalHistoryDeviceKey, current.DeviceKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(known.ReceiverAssociationKey, current.AssociationReceiverHistoryKey, StringComparison.OrdinalIgnoreCase)))
            return;

        var provisionalKey = current.DeviceKey;
        current.DeviceKey = known.CanonicalHistoryDeviceKey;
        current.AssociationReceiverHistoryKey = known.ReceiverAssociationKey;
        current.AssociationReceiverSerial = known.ReceiverSerial;
        current.ReceiverCanonicalHistoryKey = known.CanonicalHistoryDeviceKey;
        _log?.Write("info", "history.identity_reused", nameof(BatteryHistoryStore), "success", new
        {
            provisional_device_key = provisionalKey,
            device_key = current.DeviceKey,
            reason = "persisted_resolved_endpoint_overlap",
            resolved_device_ids = current.ResolvedDeviceIds
        });
    }

    private void RememberSoraHistoryAnchor(BatteryHistoryEntry entry)
    {
        RememberSoraHistoryAnchorIn(_soraHistoryRelationsByPath, entry);
    }

    private static void RememberSoraHistoryAnchorIn(
        Dictionary<string, List<SoraHistoryAnchor>> relationsByPath,
        BatteryHistoryEntry entry)
    {
        var rejected = string.Equals(
            entry.HistoryAnchorEvidence,
            HistoryAnchorEvidence.RejectPersistedReceiver.ToString(),
            StringComparison.OrdinalIgnoreCase);
        if (!rejected && string.Equals(entry.State, "device_offline", StringComparison.OrdinalIgnoreCase))
            return;

        var receiverAssociationKey = ReceiverAssociationKey(entry);
        if (string.IsNullOrWhiteSpace(receiverAssociationKey)
            || (!rejected && !IsSoraReceiverDeviceKey(entry.DeviceKey)))
            return;

        foreach (var deviceId in entry.ResolvedDeviceIds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!relationsByPath.TryGetValue(deviceId, out var relations))
            {
                relations = new List<SoraHistoryAnchor>();
                relationsByPath[deviceId] = relations;
            }
            var previous = FindReceiverAnchor(
                relations,
                receiverAssociationKey,
                entry.AssociationReceiverSerial);
            if (previous != null && previous.TimestampUtc > entry.TimestampUtc)
                continue;
            if (previous != null)
                relations.Remove(previous);
            var recordedCanonicalHistoryDeviceKey = !string.IsNullOrWhiteSpace(entry.ReceiverCanonicalHistoryKey)
                ? entry.ReceiverCanonicalHistoryKey
                : !rejected && IsSoraReceiverDeviceKey(entry.DeviceKey)
                    ? entry.DeviceKey
                    : "";
            var canonicalHistoryDeviceKey = rejected
                ? previous?.CanonicalHistoryDeviceKey ?? recordedCanonicalHistoryDeviceKey
                : PreferCanonicalReceiverHistoryKey(
                    previous?.CanonicalHistoryDeviceKey,
                    recordedCanonicalHistoryDeviceKey);
            var receiverSerial = DeviceIdentity.NormalizeSerial(entry.AssociationReceiverSerial)
                ?? previous?.ReceiverSerial
                ?? "";
            relations.Add(new SoraHistoryAnchor(
                receiverAssociationKey,
                receiverSerial,
                canonicalHistoryDeviceKey,
                entry.TimestampUtc,
                entry.DeviceSerial,
                rejected,
                entry));
        }
    }

    private bool IdentityEvidenceAlreadyCurrent(BatteryHistoryEntry entry)
    {
        var rejected = string.Equals(
            entry.HistoryAnchorEvidence,
            HistoryAnchorEvidence.RejectPersistedReceiver.ToString(),
            StringComparison.OrdinalIgnoreCase);
        var paths = entry.ResolvedDeviceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
            return true;

        var receiverAssociationKey = ReceiverAssociationKey(entry);
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return false;

        return paths.All(path =>
        {
            if (!_soraHistoryRelationsByPath.TryGetValue(path, out var relations))
                return false;
            var current = FindReceiverAnchor(
                relations,
                receiverAssociationKey,
                entry.AssociationReceiverSerial);
            var latestForAssociation = LatestFactForAssociation(relations, receiverAssociationKey);
            var latestActiveRelation = relations
                .GroupBy(
                    anchor => anchor.ReceiverAssociationKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(anchor => anchor.TimestampUtc)
                    .First())
                .Where(anchor => !anchor.IsRejected)
                .Where(anchor => !string.IsNullOrWhiteSpace(anchor.CanonicalHistoryDeviceKey))
                .OrderByDescending(anchor => anchor.TimestampUtc)
                .FirstOrDefault();
            return current != null
                && ReferenceEquals(current, latestForAssociation)
                && (rejected || ReferenceEquals(current, latestActiveRelation))
                && current.IsRejected == rejected
                && (rejected
                    || string.Equals(
                        current.CanonicalHistoryDeviceKey,
                        entry.ReceiverCanonicalHistoryKey,
                        StringComparison.OrdinalIgnoreCase));
        });
    }

    private bool RestoresRejectedSoraAnchor(BatteryHistoryEntry entry)
    {
        if (!IsSoraReceiverDeviceKey(entry.DeviceKey))
            return false;

        var receiverAssociationKey = ReceiverAssociationKey(entry);
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return false;

        return entry.ResolvedDeviceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(path => FindReceiverAnchor(
                SoraHistoryAnchorsForPath(path),
                receiverAssociationKey,
                entry.AssociationReceiverSerial))
            .Any(anchor => anchor?.IsRejected == true);
    }

    private IEnumerable<SoraHistoryAnchor> SoraHistoryAnchorsForPath(string path)
    {
        return _soraHistoryRelationsByPath.TryGetValue(path, out var relations)
            ? relations
            : Array.Empty<SoraHistoryAnchor>();
    }

    private static SoraHistoryAnchor? FindReceiverAnchor(
        IEnumerable<SoraHistoryAnchor> anchors,
        string receiverAssociationKey,
        string? receiverSerial)
    {
        var sameAssociation = anchors
            .Where(anchor => string.Equals(
                anchor.ReceiverAssociationKey,
                receiverAssociationKey,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var normalizedReceiverSerial = DeviceIdentity.NormalizeSerial(receiverSerial);
        if (normalizedReceiverSerial == null)
            return sameAssociation
                .OrderByDescending(anchor => anchor.TimestampUtc)
                .FirstOrDefault();

        return sameAssociation
                .Where(anchor => string.Equals(
                    DeviceIdentity.NormalizeSerial(anchor.ReceiverSerial),
                    normalizedReceiverSerial,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(anchor => anchor.TimestampUtc)
                .FirstOrDefault()
            ?? sameAssociation
                .Where(anchor => DeviceIdentity.NormalizeSerial(anchor.ReceiverSerial) == null)
                .OrderByDescending(anchor => anchor.TimestampUtc)
                .FirstOrDefault();
    }

    private static SoraHistoryAnchor? LatestFactForAssociation(
        IEnumerable<SoraHistoryAnchor> anchors,
        string receiverAssociationKey)
    {
        return anchors
            .Where(anchor => string.Equals(
                anchor.ReceiverAssociationKey,
                receiverAssociationKey,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(anchor => anchor.TimestampUtc)
            .FirstOrDefault();
    }

    private static string ReceiverAssociationKey(BatteryHistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.AssociationReceiverHistoryKey))
            return entry.AssociationReceiverHistoryKey;
        if (!IsSoraReceiverDeviceKey(entry.DeviceKey))
            return "";

        var receiverPath = entry.ResolvedDeviceIds.FirstOrDefault(deviceId =>
            IsSoraReceiverProductId(ProductIdFromHidPath(deviceId)));
        if (string.IsNullOrWhiteSpace(receiverPath)
            && entry.LogicalDeviceId.StartsWith(SoraReceiverLogicalPrefix, StringComparison.OrdinalIgnoreCase))
            receiverPath = entry.LogicalDeviceId[SoraReceiverLogicalPrefix.Length..];
        if (string.IsNullOrWhiteSpace(receiverPath))
            return entry.DeviceKey;

        var receiverProductId = ProductIdFromHidPath(receiverPath);
        if (!IsSoraReceiverProductId(receiverProductId))
            receiverProductId = "0XAE1C";
        return $"{NormalizeId(entry.VendorId)}:{receiverProductId}:PATH:{ShortHash(receiverPath)}";
    }

    private static string PreferCanonicalReceiverHistoryKey(string? previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous))
            return current;
        if (IsSoraReceiverSerialDeviceKey(current))
            return current;
        return previous;
    }

    private static IReadOnlyList<string> IdentityEvidencePaths(DeviceIdentityObservation observation)
    {
        if (observation.HistoryAnchorEvidence != HistoryAnchorEvidence.RejectPersistedReceiver)
            return observation.ResolvedDeviceIds;

        if (!string.IsNullOrWhiteSpace(observation.DeviceId))
            return new[] { observation.DeviceId };

        return observation.ResolvedDeviceIds;
    }

    private void PrepareReceiverCanonicalIdentity(BatteryHistoryEntry entry)
    {
        var receiverLogical = entry.LogicalDeviceId.StartsWith(
            SoraReceiverLogicalPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (receiverLogical && string.IsNullOrWhiteSpace(entry.AssociationReceiverHistoryKey))
            entry.AssociationReceiverHistoryKey = ReceiverAssociationKey(entry);
        if (receiverLogical
            && string.Equals(
                entry.ConnectionTransport,
                DeviceConnectionTransport.Receiver.ToString(),
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(entry.AssociationReceiverSerial))
            entry.AssociationReceiverSerial = DeviceIdentity.NormalizeSerial(entry.DeviceSerial) ?? "";
        if (string.IsNullOrWhiteSpace(entry.ReceiverCanonicalHistoryKey)
            && IsSoraReceiverDeviceKey(entry.DeviceKey))
            entry.ReceiverCanonicalHistoryKey = entry.DeviceKey;

        var previousCanonicalKeys = ReceiverCanonicalHistoryKeys(entry);
        CompleteReceiverCanonicalHistoryKey(entry);
        if (!string.Equals(
                entry.HistoryAnchorEvidence,
                HistoryAnchorEvidence.RejectPersistedReceiver.ToString(),
                StringComparison.OrdinalIgnoreCase)
            && IsSoraReceiverSerialDeviceKey(entry.ReceiverCanonicalHistoryKey)
            && previousCanonicalKeys.Any(key => !string.Equals(
                key,
                entry.ReceiverCanonicalHistoryKey,
                StringComparison.OrdinalIgnoreCase)))
        {
            PromoteReceiverHistoryKey(
                ReceiverAssociationKey(entry),
                entry.AssociationReceiverSerial,
                entry.ReceiverCanonicalHistoryKey,
                previousCanonicalKeys);
            EnsureLastCacheLoaded();
            CompleteReceiverCanonicalHistoryKey(entry);
        }
    }

    private void CompleteReceiverCanonicalHistoryKey(BatteryHistoryEntry entry)
    {
        var receiverAssociationKey = ReceiverAssociationKey(entry);
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return;

        var previousCanonical = entry.ResolvedDeviceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(path => FindReceiverAnchor(
                SoraHistoryAnchorsForPath(path),
                receiverAssociationKey,
                entry.AssociationReceiverSerial))
            .Where(anchor => anchor != null)
            .Cast<SoraHistoryAnchor>()
            .Select(anchor => anchor.CanonicalHistoryDeviceKey)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var recordedCanonical = !string.IsNullOrWhiteSpace(entry.ReceiverCanonicalHistoryKey)
            ? entry.ReceiverCanonicalHistoryKey
            : !string.Equals(
                entry.HistoryAnchorEvidence,
                HistoryAnchorEvidence.RejectPersistedReceiver.ToString(),
                StringComparison.OrdinalIgnoreCase)
                && IsSoraReceiverDeviceKey(entry.DeviceKey)
                    ? entry.DeviceKey
                    : "";
        entry.ReceiverCanonicalHistoryKey = PreferCanonicalReceiverHistoryKey(
            previousCanonical,
            recordedCanonical);
    }

    private IReadOnlyCollection<string> ReceiverCanonicalHistoryKeys(BatteryHistoryEntry entry)
    {
        var receiverAssociationKey = ReceiverAssociationKey(entry);
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return Array.Empty<string>();

        return entry.ResolvedDeviceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(path => FindReceiverAnchor(
                SoraHistoryAnchorsForPath(path),
                receiverAssociationKey,
                entry.AssociationReceiverSerial))
            .Where(anchor => anchor != null)
            .Cast<SoraHistoryAnchor>()
            .Select(anchor => anchor.CanonicalHistoryDeviceKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void PromoteReceiverHistoryKey(
        string receiverAssociationKey,
        string receiverSerial,
        string canonicalHistoryDeviceKey,
        IReadOnlyCollection<string> previousCanonicalKeys)
    {
        if (string.IsNullOrWhiteSpace(receiverAssociationKey)
            || string.IsNullOrWhiteSpace(canonicalHistoryDeviceKey)
            || previousCanonicalKeys.Count == 0
            || !File.Exists(_paths.HistoryPath))
            return;

        var previousKeys = new HashSet<string>(previousCanonicalKeys, StringComparer.OrdinalIgnoreCase);
        previousKeys.Remove(canonicalHistoryDeviceKey);
        if (previousKeys.Count == 0)
            return;

        var entries = ReadEntries(DateTime.MinValue).ToArray();
        var migratedCount = 0;
        foreach (var historicalEntry in entries)
        {
            var changed = false;
            if (previousKeys.Contains(historicalEntry.DeviceKey))
            {
                historicalEntry.DeviceKey = canonicalHistoryDeviceKey;
                changed = true;
            }
            if (string.Equals(
                    ReceiverAssociationKey(historicalEntry),
                    receiverAssociationKey,
                    StringComparison.OrdinalIgnoreCase)
                && SerialsCanRepresentSameDevice(
                    historicalEntry.AssociationReceiverSerial,
                    receiverSerial)
                && (string.IsNullOrWhiteSpace(historicalEntry.ReceiverCanonicalHistoryKey)
                    || previousKeys.Contains(historicalEntry.ReceiverCanonicalHistoryKey)))
            {
                historicalEntry.AssociationReceiverHistoryKey = receiverAssociationKey;
                historicalEntry.AssociationReceiverSerial = DeviceIdentity.NormalizeSerial(receiverSerial) ?? "";
                historicalEntry.ReceiverCanonicalHistoryKey = canonicalHistoryDeviceKey;
                changed = true;
            }
            if (changed)
                migratedCount++;
        }
        if (migratedCount == 0)
            return;

        var temporaryPath = _paths.HistoryPath + ".identity-promote.tmp";
        File.WriteAllLines(temporaryPath, entries.Select(item => JsonSerializer.Serialize(item, JsonOptions)));
        File.Move(temporaryPath, _paths.HistoryPath, overwrite: true);
        _lastCacheLoaded = false;
        _log?.Write("info", "history.receiver_key_promoted", nameof(BatteryHistoryStore), "success", new
        {
            receiver_association_key = receiverAssociationKey,
            canonical_history_device_key = canonicalHistoryDeviceKey,
            previous_history_device_keys = previousKeys,
            migrated_entry_count = migratedCount
        });
    }

    private static string DetermineEvent(BatteryHistoryEntry? previous, BatteryHistoryEntry current)
    {
        if (previous == null)
            return current.State == "device_offline" ? "device_offline" : "sample";
        if (current.State == "device_offline")
            return previous.State == "device_offline" ? "" : "device_offline";
        if (previous.State == "device_offline")
            return "device_online";
        if (!previous.IsCharging && current.IsCharging)
            return "charging_start";
        if (previous.IsCharging && !current.IsCharging)
            return "charging_end";
        if (!string.Equals(previous.LogicalDeviceId, current.LogicalDeviceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.ConnectionTransport, current.ConnectionTransport, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.HistoryAnchorEvidence, current.HistoryAnchorEvidence, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.AssociationReceiverHistoryKey, current.AssociationReceiverHistoryKey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.ReceiverCanonicalHistoryKey, current.ReceiverCanonicalHistoryKey, StringComparison.OrdinalIgnoreCase)
            || !new HashSet<string>(previous.ResolvedDeviceIds, StringComparer.OrdinalIgnoreCase)
                .SetEquals(current.ResolvedDeviceIds))
            return "transport_change";
        if (previous.BatteryPercentage != current.BatteryPercentage)
            return "sample";
        if (current.TimestampUtc - previous.TimestampUtc >= TimeSpan.FromHours(1))
            return "heartbeat";
        return "";
    }

    private IReadOnlyList<BatteryHistoryEntry> ReadEntries(DateTime fromUtc)
    {
        var entries = new List<BatteryHistoryEntry>();
        try
        {
            if (!File.Exists(_paths.HistoryPath))
                return entries;

            foreach (var line in File.ReadLines(_paths.HistoryPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                BatteryHistoryEntry? entry;
                try { entry = JsonSerializer.Deserialize<BatteryHistoryEntry>(line); }
                catch { continue; }

                if (entry == null || entry.TimestampUtc < fromUtc || !IsValidEntry(entry))
                    continue;

                entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _log?.Write("warn", "history.read_failed", nameof(BatteryHistoryStore), "failure", exception: ex);
        }

        return entries;
    }

    private static bool IsValidEntry(BatteryHistoryEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.DeviceKey)
            && !string.IsNullOrWhiteSpace(entry.DeviceName)
            && (!entry.HasBatteryPercentage || entry.BatteryPercentage is >= 1 and <= 100);
    }

    private void PruneOldEntriesIfDue()
    {
        try
        {
            var todayUtc = DateTime.UtcNow.Date;
            if (_lastPruneDateUtc == todayUtc)
                return;
            if (!File.Exists(_paths.HistoryPath))
            {
                _lastPruneDateUtc = todayUtc;
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-30);
            var allEntries = ReadEntries(DateTime.MinValue).ToArray();
            var keptEntries = allEntries
                .Where(entry => entry.TimestampUtc >= cutoff)
                .ToList();
            var latestRelations = new Dictionary<string, List<SoraHistoryAnchor>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in allEntries)
                RememberSoraHistoryAnchorIn(latestRelations, entry);
            foreach (var entry in keptEntries)
                MaterializeReceiverIdentityFromLatestRelation(entry, latestRelations);
            var retainedIdentityEntries = latestRelations
                .SelectMany(path => path.Value.Select(anchor => new { Path = path.Key, Anchor = anchor }))
                .Where(item => item.Anchor.SourceEntry.TimestampUtc < cutoff)
                .Select(item => CreateRetainedIdentityEntry(item.Path, item.Anchor))
                .ToArray();
            keptEntries.AddRange(retainedIdentityEntries);
            var kept = keptEntries
                .Select(entry => JsonSerializer.Serialize(entry, JsonOptions))
                .ToArray();
            var temporaryPath = _paths.HistoryPath + ".tmp";
            File.WriteAllLines(temporaryPath, kept);
            File.Move(temporaryPath, _paths.HistoryPath, overwrite: true);
            _lastCacheLoaded = false;
            _lastPruneDateUtc = todayUtc;
            _log?.Write("info", "history.pruned", nameof(BatteryHistoryStore), "success", new
            {
                cutoff_utc = cutoff,
                kept_count = kept.Length,
                retained_identity_count = retainedIdentityEntries.Length
            });
        }
        catch (Exception ex)
        {
            _log?.Write("warn", "history.prune_failed", nameof(BatteryHistoryStore), "failure", exception: ex);
        }
    }

    private static BatteryHistoryEntry CreateRetainedIdentityEntry(string deviceId, SoraHistoryAnchor anchor)
    {
        var source = anchor.SourceEntry;
        return new BatteryHistoryEntry
        {
            TimestampUtc = source.TimestampUtc,
            DeviceKey = anchor.IsRejected ? source.DeviceKey : anchor.CanonicalHistoryDeviceKey,
            DeviceName = source.DeviceName,
            LogicalDeviceId = source.LogicalDeviceId,
            AssociationReceiverHistoryKey = anchor.ReceiverAssociationKey,
            AssociationReceiverSerial = anchor.ReceiverSerial,
            ReceiverCanonicalHistoryKey = anchor.CanonicalHistoryDeviceKey,
            ConnectionTransport = source.ConnectionTransport,
            HistoryAnchorEvidence = anchor.IsRejected
                ? HistoryAnchorEvidence.RejectPersistedReceiver.ToString()
                : HistoryAnchorEvidence.ConfirmReceiver.ToString(),
            ResolvedDeviceIds = new[] { deviceId },
            DeviceSerial = source.DeviceSerial,
            VendorId = source.VendorId,
            ProductId = source.ProductId,
            HasBatteryPercentage = false,
            State = anchor.IsRejected ? "identity_anchor_rejected" : "identity_anchor_retained",
            Source = source.Source
        };
    }

    private static void MaterializeReceiverIdentityFromLatestRelation(
        BatteryHistoryEntry entry,
        IReadOnlyDictionary<string, List<SoraHistoryAnchor>> latestRelations)
    {
        var sourceAnchor = latestRelations.Values
            .SelectMany(anchors => anchors)
            .Where(anchor => ReferenceEquals(anchor.SourceEntry, entry))
            .OrderByDescending(anchor => anchor.TimestampUtc)
            .FirstOrDefault();
        if (sourceAnchor == null)
            return;

        entry.AssociationReceiverHistoryKey = sourceAnchor.ReceiverAssociationKey;
        entry.AssociationReceiverSerial = sourceAnchor.ReceiverSerial;
        entry.ReceiverCanonicalHistoryKey = sourceAnchor.CanonicalHistoryDeviceKey;
    }

    internal static string CreateDeviceKey(BatteryReading reading)
    {
        if (!string.IsNullOrWhiteSpace(reading.HistoryDeviceKey))
            return reading.HistoryDeviceKey.Trim();

        if (!string.IsNullOrWhiteSpace(reading.LogicalDeviceId))
        {
            var logicalDeviceId = reading.LogicalDeviceId.Trim();
            if (logicalDeviceId.StartsWith(SoraReceiverLogicalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var receiverAnchor = logicalDeviceId[SoraReceiverLogicalPrefix.Length..];
                var logicalVendorId = NormalizeId(reading.VendorId);
                if (!string.IsNullOrWhiteSpace(receiverAnchor))
                {
                    var receiverProductId = ProductIdFromHidPath(receiverAnchor);
                    if (!IsSoraReceiverProductId(receiverProductId))
                        receiverProductId = "0XAE1C";
                    return $"{logicalVendorId}:{receiverProductId}:PATH:{ShortHash(receiverAnchor)}";
                }
            }

            if (!logicalDeviceId.StartsWith(SoraWiredLogicalPrefix, StringComparison.OrdinalIgnoreCase))
                return $"LOGICAL:{ShortHash(logicalDeviceId)}";
        }

        var vendorId = NormalizeId(reading.VendorId);
        var productId = NormalizeId(reading.ProductId);
        var name = DisplayDeviceName(reading);
        var serial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial);

        if (!string.IsNullOrWhiteSpace(vendorId) && !string.IsNullOrWhiteSpace(productId) && !string.IsNullOrWhiteSpace(serial))
            return $"{vendorId}:{productId}:SERIAL:{serial}";

        if (!string.IsNullOrWhiteSpace(reading.DeviceId))
            return $"{vendorId}:{productId}:PATH:{ShortHash(reading.DeviceId)}";

        if (!string.IsNullOrWhiteSpace(vendorId) && !string.IsNullOrWhiteSpace(productId))
            return $"{vendorId}:{productId}:NAME:{NormalizeForKey(name)}";

        return NormalizeForKey($"{reading.Source}:{name}");
    }

    internal static bool IsSoraReceiverDeviceKey(string? deviceKey)
    {
        return !string.IsNullOrWhiteSpace(deviceKey)
            && (deviceKey.StartsWith("0X1915:0XAE1C:", StringComparison.OrdinalIgnoreCase)
                || deviceKey.StartsWith("0X1915:0XAE8A:", StringComparison.OrdinalIgnoreCase)
                || deviceKey.StartsWith("0X1915:0XAE8C:", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsSoraReceiverSerialDeviceKey(string? deviceKey)
    {
        return IsSoraReceiverDeviceKey(deviceKey)
            && deviceKey!.Contains(":SERIAL:", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> CreateLegacyAliasKeys(BatteryReading reading)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(reading.LogicalDeviceId))
            return aliases;

        var logicalDeviceId = reading.LogicalDeviceId.Trim();
        var isReceiverLogical = logicalDeviceId.StartsWith(SoraReceiverLogicalPrefix, StringComparison.OrdinalIgnoreCase);
        var isWiredLogical = logicalDeviceId.StartsWith(SoraWiredLogicalPrefix, StringComparison.OrdinalIgnoreCase);
        if (!isReceiverLogical && !isWiredLogical)
            return aliases;

        aliases.Add($"LOGICAL:{ShortHash(logicalDeviceId)}");

        var vendorId = NormalizeId(reading.VendorId);
        var productIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isReceiverLogical)
        {
            var receiverAnchor = logicalDeviceId[SoraReceiverLogicalPrefix.Length..];
            var receiverProductId = ProductIdFromHidPath(receiverAnchor);
            productIds.Add(IsSoraReceiverProductId(receiverProductId) ? receiverProductId : "0XAE1C");
        }
        if (!string.IsNullOrWhiteSpace(reading.ProductId))
            productIds.Add(NormalizeId(reading.ProductId));

        foreach (var deviceId in DeviceIdentity.ResolvedDeviceIds(reading))
        {
            var pathProductId = ProductIdFromHidPath(deviceId);
            if (!string.IsNullOrWhiteSpace(pathProductId))
                productIds.Add(pathProductId);
            if (!string.IsNullOrWhiteSpace(pathProductId))
            {
                aliases.Add($"{vendorId}:{pathProductId}:PATH:{ShortHash(deviceId)}");
                if (!IsSoraReceiverProductId(pathProductId))
                    aliases.Add($"LOGICAL:{ShortHash(SoraWiredLogicalPrefix + deviceId)}");
            }
        }

        var serial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial);
        if (serial != null)
        {
            foreach (var productId in productIds)
                aliases.Add($"{vendorId}:{productId}:SERIAL:{serial}");
        }

        var name = NormalizeForKey(DisplayDeviceName(reading));
        foreach (var productId in productIds)
            aliases.Add($"{vendorId}:{productId}:{name}");

        return aliases;
    }

    private static bool IsSoraReceiverProductId(string? productId)
    {
        return productId is not null
            && (productId.Equals("0XAE1C", StringComparison.OrdinalIgnoreCase)
                || productId.Equals("0XAE8A", StringComparison.OrdinalIgnoreCase)
                || productId.Equals("0XAE8C", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SerialsCanRepresentSameDevice(string? previous, string? current)
    {
        var previousSerial = DeviceIdentity.NormalizeSerial(previous);
        var currentSerial = DeviceIdentity.NormalizeSerial(current);
        return previousSerial == null
            || currentSerial == null
            || string.Equals(previousSerial, currentSerial, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProductIdFromHidPath(string deviceId)
    {
        const string marker = "pid_";
        var markerIndex = deviceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 || deviceId.Length < markerIndex + marker.Length + 4)
            return "";

        var value = deviceId.Substring(markerIndex + marker.Length, 4);
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var productId)
                ? $"0X{productId:X4}"
                : "";
    }

    private static string DisplayDeviceName(BatteryReading reading)
    {
        var name = string.IsNullOrWhiteSpace(reading.DeviceName) ? reading.Source : reading.DeviceName;
        name = name
            .Replace("Wireless mouse", "Mouse", StringComparison.OrdinalIgnoreCase)
            .Replace("NANO dongle", "Dongle", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrWhiteSpace(name) ? "Mouse" : Shorten(name, 48);
    }

    private static string NormalizeId(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
    }

    private static int ParseProductId(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        return int.TryParse(
            normalized,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var productId)
                ? productId
                : 0;
    }

    private static BatteryReading IdentityObservationForLog(DeviceIdentityObservation observation)
    {
        return new BatteryReading
        {
            HasBatteryPercentage = false,
            LogicalDeviceId = observation.LogicalDeviceId,
            HistoryDeviceKey = observation.HistoryDeviceKey,
            AssociationReceiverHistoryKey = observation.AssociationReceiverHistoryKey,
            AssociationReceiverSerial = observation.AssociationReceiverSerial,
            ConnectionTransport = NinjutsoSoraTransportPolicy.GetTransport(ParseProductId(observation.ProductId)),
            HistoryAnchorEvidence = observation.HistoryAnchorEvidence,
            ResolvedDeviceIds = observation.ResolvedDeviceIds,
            DeviceName = observation.DeviceName,
            DeviceId = observation.DeviceId,
            DeviceSerial = observation.DeviceSerial,
            VendorId = observation.VendorId,
            ProductId = observation.ProductId,
            Source = observation.Source,
            TimestampUtc = observation.TimestampUtc
        };
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string NormalizeForKey(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
    }

    private static string Shorten(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
    }

    private sealed record SoraHistoryAnchor(
        string ReceiverAssociationKey,
        string ReceiverSerial,
        string CanonicalHistoryDeviceKey,
        DateTime TimestampUtc,
        string DeviceSerial,
        bool IsRejected,
        BatteryHistoryEntry SourceEntry);
}

internal sealed class BatteryHistoryWindow : Form
{
    private readonly BatteryHistoryStore _store;
    private readonly Localizer _text;
    private readonly BatteryChartPanel _chart = new();
    private readonly ComboBox _devicePicker = new();
    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly Label _estimate = new();
    private readonly Label _details = new();
    private TimeSpan _range = TimeSpan.FromDays(1);
    private string? _selectedDeviceKey;
    private bool _refreshingDevices;

    public BatteryHistoryWindow(BatteryHistoryStore store, Localizer text)
    {
        _store = store;
        _text = text;
        Text = $"{_text["AppName"]} - {_text["BatteryHistory"]}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 760);
        Size = new Size(890, 1140);
        BackColor = Color.FromArgb(25, 25, 25);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 11F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(38), BackColor = BackColor, RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        Controls.Add(root);

        var header = Card();
        header.Padding = new Padding(30, 22, 30, 20);
        _title.SetBounds(30, 20, 500, 56);
        _title.Font = new Font(Font.FontFamily, 27F, FontStyle.Bold);
        _title.ForeColor = Color.White;
        _summary.SetBounds(31, 78, 420, 76);
        _summary.Font = new Font(Font.FontFamily, 15F);
        _estimate.SetBounds(560, 32, 210, 92);
        _estimate.TextAlign = ContentAlignment.MiddleRight;
        _estimate.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        _estimate.ForeColor = Color.White;
        header.Controls.Add(_title);
        header.Controls.Add(_summary);
        header.Controls.Add(_estimate);
        root.Controls.Add(header, 0, 0);

        var deviceBar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor, ColumnCount = 2, Padding = new Padding(0, 0, 0, 14) };
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var deviceLabel = new Label
        {
            Text = _text.IsZh ? "鼠标" : "Mouse",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gainsboro,
            Font = new Font(Font.FontFamily, 14F)
        };
        _devicePicker.Dock = DockStyle.Fill;
        _devicePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _devicePicker.Font = new Font(Font.FontFamily, 13F);
        _devicePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingDevices)
                return;
            if (_devicePicker.SelectedItem is DevicePickerItem item)
            {
                _selectedDeviceKey = item.DeviceKey;
                Reload();
            }
        };
        deviceBar.Controls.Add(deviceLabel, 0, 0);
        deviceBar.Controls.Add(_devicePicker, 1, 0);
        root.Controls.Add(deviceBar, 0, 1);

        var rangeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor };
        AddRangeButton(rangeBar, _text["Last24Hours"], TimeSpan.FromDays(1), true);
        AddRangeButton(rangeBar, _text["Last7Days"], TimeSpan.FromDays(7), false);
        AddRangeButton(rangeBar, _text["Last30Days"], TimeSpan.FromDays(30), false);
        root.Controls.Add(rangeBar, 0, 2);

        var chartCard = Card();
        chartCard.Padding = new Padding(28);
        _chart.Dock = DockStyle.Fill;
        chartCard.Controls.Add(_chart);
        root.Controls.Add(chartCard, 0, 3);

        var detailCard = Card();
        detailCard.Padding = new Padding(28, 18, 28, 18);
        _details.Dock = DockStyle.Fill;
        _details.Font = new Font(Font.FontFamily, 14F);
        _details.ForeColor = Color.White;
        detailCard.Controls.Add(_details);
        root.Controls.Add(detailCard, 0, 4);

        Load += (_, _) => ApplyDarkTitleBar();
        Reload();
    }

    public void Reload()
    {
        RefreshDevicePicker();
        var viewEntries = _store.ReadLast(_range, _selectedDeviceKey);
        var modelEntries = _store.ReadLast(TimeSpan.FromDays(30), _selectedDeviceKey);
        _chart.SetData(viewEntries, _range, _text["HistoryChartLegend"], _text["HistoryDailyLegend"]);

        var latest = modelEntries.LastOrDefault(entry => entry.State != "device_offline");
        var forecast = BatteryForecastService.Analyze(modelEntries, latest?.BatteryPercentage ?? 0);
        var viewAnalysis = BatteryForecastService.Analyze(viewEntries, latest?.BatteryPercentage ?? 0);
        var estimateText = forecast.IsAvailable
            ? forecast.MaximumRemainingDays - forecast.MinimumRemainingDays < 0.2
                ? $"{forecast.MinimumRemainingDays:0.0} d"
                : $"{forecast.MinimumRemainingDays:0.0}–{forecast.MaximumRemainingDays:0.0} d"
            : _text["HistoryUnavailable"];
        var charging = latest?.IsCharging == true || latest?.IsCableConnected == true;
        var lastChargeEntry = modelEntries.LastOrDefault(entry => entry.State == "charging_end")
            ?? modelEntries.LastOrDefault(entry => (entry.IsCharging || entry.IsCableConnected) && entry.BatteryPercentage >= 99)
            ?? modelEntries.LastOrDefault(entry => entry.IsCharging || entry.IsCableConnected);
        var lastCharge = lastChargeEntry?.TimestampUtc.ToLocalTime();
        var deviceName = CurrentDeviceName();
        var averageText = forecast.IsAvailable
            ? $"{forecast.TypicalDrainPerHour:0.00}%/h"
            : _text["HistoryUnavailable"];
        var confidenceText = _text[$"Confidence{forecast.Confidence}"];

        _title.Text = latest == null ? deviceName : $"{deviceName} · {latest.BatteryPercentage}%";
        _summary.Text = $"{(modelEntries.Count == 0 ? _text["HistoryDemo"] : _text["HistoryRealData"])}\r\n{(charging ? _text["Charging"] : "")}";
        _summary.ForeColor = charging ? Color.FromArgb(88, 224, 112) : Color.Gainsboro;
        _estimate.Text = $"{_text["HistoryEstimate"]}\r\n{estimateText}";
        _details.Text =
            $"{_text["HistoryConsumed"]}: {viewAnalysis.ConsumedPercentage}%    {_text["HistoryAverage"]}: {averageText}\r\n" +
            $"{_text["HistoryLastFull"]}: {(lastCharge.HasValue ? lastCharge.Value.ToString("MM-dd HH:mm") : _text["HistoryUnavailable"])}\r\n" +
            $"{_text["HistoryConfidence"]}: {confidenceText}    {_text["HistoryBasis"]}: {forecast.SegmentCount} {_text["HistorySegments"]} / {forecast.ValidHours:0}h";
    }

    private static BatteryEstimate CalculateEstimate(IReadOnlyList<BatteryHistoryEntry> source)
    {
        if (source.Count < 2)
            return default;

        var entries = source.OrderBy(entry => entry.TimestampUtc).ToArray();
        var maximumGap = CalculateMaximumGap(entries);
        var segments = new List<DischargeSegment>();
        DateTime? segmentStartTime = null;
        DateTime? segmentEndTime = null;
        var segmentStartBattery = 0;
        var segmentEndBattery = 0;

        void CompleteSegment()
        {
            if (!segmentStartTime.HasValue || !segmentEndTime.HasValue)
                return;

            var hours = (segmentEndTime.Value - segmentStartTime.Value).TotalHours;
            var consumed = segmentStartBattery - segmentEndBattery;
            if (hours >= 0.5 && consumed >= 1)
            {
                var rate = consumed / hours;
                if (rate is >= 0.01 and <= 20)
                    segments.Add(new DischargeSegment(hours, consumed, rate));
            }

            segmentStartTime = null;
            segmentEndTime = null;
        }

        for (var i = 1; i < entries.Length; i++)
        {
            var previous = entries[i - 1];
            var current = entries[i];
            var elapsed = current.TimestampUtc - previous.TimestampUtc;
            var drop = previous.BatteryPercentage - current.BatteryPercentage;
            var maximumPlausibleDrop = Math.Max(5, (int)Math.Ceiling(elapsed.TotalHours * 10));
            var validPair = elapsed > TimeSpan.Zero
                && elapsed <= maximumGap
                && !IsCharging(previous)
                && !IsCharging(current)
                && drop >= 0
                && drop <= maximumPlausibleDrop;

            if (!validPair)
            {
                CompleteSegment();
                continue;
            }

            if (!segmentStartTime.HasValue)
            {
                segmentStartTime = previous.TimestampUtc;
                segmentStartBattery = previous.BatteryPercentage;
            }

            segmentEndTime = current.TimestampUtc;
            segmentEndBattery = current.BatteryPercentage;
        }

        CompleteSegment();

        var validHours = segments.Sum(segment => segment.Hours);
        var consumedPercentage = segments.Sum(segment => segment.ConsumedPercentage);
        if (segments.Count == 0 || validHours < 2 || consumedPercentage < 1)
            return new BatteryEstimate(false, 0, consumedPercentage, validHours, segments.Count);

        var average = WeightedMedianRate(segments);
        return new BatteryEstimate(average > 0.01, average, consumedPercentage, validHours, segments.Count);
    }

    private static TimeSpan CalculateMaximumGap(IReadOnlyList<BatteryHistoryEntry> entries)
    {
        var intervals = new List<double>();
        for (var i = 1; i < entries.Count; i++)
        {
            var minutes = (entries[i].TimestampUtc - entries[i - 1].TimestampUtc).TotalMinutes;
            if (minutes is > 0 and <= 180)
                intervals.Add(minutes);
        }

        if (intervals.Count == 0)
            return TimeSpan.FromMinutes(45);

        intervals.Sort();
        var middle = intervals.Count / 2;
        var median = intervals.Count % 2 == 0
            ? (intervals[middle - 1] + intervals[middle]) / 2
            : intervals[middle];
        return TimeSpan.FromMinutes(Math.Clamp(median * 3, 30, 180));
    }

    private static double WeightedMedianRate(IReadOnlyList<DischargeSegment> source)
    {
        var segments = source.OrderBy(segment => segment.RatePerHour).ToArray();
        var halfWeight = segments.Sum(segment => segment.Hours) / 2;
        var cumulative = 0d;
        foreach (var segment in segments)
        {
            cumulative += segment.Hours;
            if (cumulative >= halfWeight)
                return segment.RatePerHour;
        }

        return segments[^1].RatePerHour;
    }

    private static bool IsCharging(BatteryHistoryEntry entry)
    {
        return entry.IsCharging || entry.IsCableConnected;
    }

    private readonly record struct DischargeSegment(double Hours, int ConsumedPercentage, double RatePerHour);
    private readonly record struct BatteryEstimate(bool IsAvailable, double AverageDrainPerHour, int ConsumedPercentage, double ValidHours, int SegmentCount);

    private void RefreshDevicePicker()
    {
        var previous = _selectedDeviceKey;
        var devices = _store.ReadDevices(TimeSpan.FromDays(30));
        _refreshingDevices = true;
        try
        {
            _devicePicker.Items.Clear();
            foreach (var device in devices)
                _devicePicker.Items.Add(new DevicePickerItem(device, _text["Charging"]));

            if (_devicePicker.Items.Count == 0)
            {
                _selectedDeviceKey = null;
                _devicePicker.Enabled = false;
                return;
            }

            _devicePicker.Enabled = true;
            var selectedIndex = 0;
            for (var i = 0; i < _devicePicker.Items.Count; i++)
            {
                if (_devicePicker.Items[i] is DevicePickerItem item && string.Equals(item.DeviceKey, previous, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }

            _devicePicker.SelectedIndex = selectedIndex;
            if (_devicePicker.SelectedItem is DevicePickerItem selected)
                _selectedDeviceKey = selected.DeviceKey;
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    private string CurrentDeviceName()
    {
        return _devicePicker.SelectedItem is DevicePickerItem item ? item.DeviceName : _text["AppName"];
    }

    private void AddRangeButton(FlowLayoutPanel parent, string label, TimeSpan range, bool isDefault)
    {
        var button = new RadioButton
        {
            Text = label,
            Checked = isDefault,
            ForeColor = Color.White,
            BackColor = BackColor,
            AutoSize = false,
            Width = 230,
            Height = 60,
            Font = new Font(Font.FontFamily, 14F),
            Padding = new Padding(0, 8, 0, 0)
        };
        button.CheckedChanged += (_, _) =>
        {
            if (!button.Checked) return;
            _range = range;
            Reload();
        };
        parent.Controls.Add(button);
    }

    private static Panel Card() => new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(38, 38, 38), Margin = new Padding(0, 0, 0, 24) };

    private void ApplyDarkTitleBar()
    {
        if (Environment.OSVersion.Version.Major < 10) return;
        var value = 1;
        DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private sealed class DevicePickerItem
    {
        public DevicePickerItem(BatteryHistoryDevice device, string chargingText)
        {
            DeviceKey = device.DeviceKey;
            DeviceName = device.DeviceName;
            Text = device.IsCharging
                ? $"{device.DeviceName} · {device.LastBatteryPercentage}% {chargingText}"
                : $"{device.DeviceName} · {device.LastBatteryPercentage}%";
        }

        public string DeviceKey { get; }
        public string DeviceName { get; }
        private string Text { get; }
        public override string ToString() => Text;
    }
}

internal sealed class BatteryChartPanel : Control
{
    private IReadOnlyList<BatteryHistoryEntry> _entries = Array.Empty<BatteryHistoryEntry>();
    private TimeSpan _range = TimeSpan.FromDays(1);
    private string _levelLegend = "";
    private string _dailyLegend = "";

    public BatteryChartPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(38, 38, 38);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 11F);
    }

    public void SetData(IReadOnlyList<BatteryHistoryEntry> entries, TimeSpan range, string levelLegend, string dailyLegend)
    {
        _entries = entries;
        _range = range;
        _levelLegend = levelLegend;
        _dailyLegend = dailyLegend;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_range > TimeSpan.FromDays(1.5))
        {
            DrawDailyUsage(g);
            return;
        }

        DrawBatteryLevel(g);
    }

    private void DrawBatteryLevel(Graphics g)
    {
        var plot = new Rectangle(78, 38, Math.Max(10, Width - 120), Math.Max(10, Height - 96));
        using var grid = new Pen(Color.FromArgb(55, 255, 255, 255));
        using var label = new SolidBrush(Color.FromArgb(205, 220, 220, 220));
        using var line = new Pen(Color.FromArgb(89, 169, 255), 4F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var chargingLine = new Pen(Color.FromArgb(70, 220, 105), 4F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var chargingBand = new SolidBrush(Color.FromArgb(32, 70, 220, 105));
        using var gap = new Pen(Color.FromArgb(120, 200, 200, 200), 3F) { DashStyle = DashStyle.Dash };

        foreach (var value in new[] { 100, 75, 50, 25, 0 })
        {
            var y = Y(value, plot);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{value}%", Font, label, 8, y - 11);
        }

        var from = DateTime.UtcNow - _range;
        var to = DateTime.UtcNow;
        var maximumGap = BatteryForecastService.GetMaximumGap(_entries);
        for (var i = 1; i < _entries.Count; i++)
        {
            var a = _entries[i - 1];
            var b = _entries[i];
            var x1 = X(a.TimestampUtc, plot, from, to);
            var x2 = X(b.TimestampUtc, plot, from, to);
            var isGap = b.TimestampUtc - a.TimestampUtc > maximumGap
                || a.State == "device_offline"
                || b.State is "device_offline" or "device_online";
            var charging = a.IsCharging || a.IsCableConnected || b.IsCharging || b.IsCableConnected;
            if (charging && !isGap)
                g.FillRectangle(chargingBand, Math.Min(x1, x2), plot.Top, Math.Max(2, Math.Abs(x2 - x1)), plot.Height);
            var pen = isGap ? gap : charging ? chargingLine : line;
            g.DrawLine(pen, x1, Y(a.BatteryPercentage, plot), x2, Y(b.BatteryPercentage, plot));
        }

        g.DrawString(_levelLegend, Font, label, 8, Height - 34);
    }

    private void DrawDailyUsage(Graphics g)
    {
        var days = Math.Clamp((int)Math.Round(_range.TotalDays), 2, 30);
        var today = DateTime.Today;
        var daily = new List<(DateTime Day, int Consumed)>();
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            var entries = _entries.Where(entry => entry.TimestampUtc.ToLocalTime().Date == day).ToArray();
            var analysis = BatteryForecastService.Analyze(entries, entries.LastOrDefault()?.BatteryPercentage ?? 0);
            daily.Add((day, analysis.ConsumedPercentage));
        }

        var maximumValue = Math.Max(5, (int)Math.Ceiling(daily.Max(item => item.Consumed) / 5d) * 5);
        var plot = new Rectangle(68, 38, Math.Max(10, Width - 100), Math.Max(10, Height - 104));
        using var grid = new Pen(Color.FromArgb(55, 255, 255, 255));
        using var label = new SolidBrush(Color.FromArgb(205, 220, 220, 220));
        using var bar = new SolidBrush(Color.FromArgb(89, 169, 255));

        foreach (var value in new[] { maximumValue, maximumValue / 2, 0 })
        {
            var y = plot.Bottom - value / (float)maximumValue * plot.Height;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{value}%", Font, label, 8, y - 11);
        }

        var slotWidth = plot.Width / (float)days;
        var barWidth = Math.Max(3, slotWidth * 0.62f);
        for (var i = 0; i < daily.Count; i++)
        {
            var item = daily[i];
            var height = item.Consumed <= 0 ? 0 : Math.Max(2, item.Consumed / (float)maximumValue * plot.Height);
            var x = plot.Left + i * slotWidth + (slotWidth - barWidth) / 2;
            if (height > 0)
                g.FillRectangle(bar, x, plot.Bottom - height, barWidth, height);

            if (days <= 7 || i == daily.Count - 1 || i % 5 == 0)
                g.DrawString(item.Day.ToString("M/d"), Font, label, x - 4, plot.Bottom + 8);
        }

        g.DrawString(_dailyLegend, Font, label, 8, Height - 30);
    }

    private static float X(DateTime t, Rectangle plot, DateTime from, DateTime to)
    {
        var total = Math.Max(1, (to - from).TotalSeconds);
        return plot.Left + (float)Math.Clamp((t - from).TotalSeconds / total, 0, 1) * plot.Width;
    }

    private static float Y(int value, Rectangle plot) => plot.Bottom - Math.Clamp(value, 0, 100) / 100f * plot.Height;
}

internal sealed class RoundedPanel : Panel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        using var path = new GraphicsPath();
        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        const int d = 34;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }
}
