namespace SoraV2BatteryTip;

internal sealed class DeviceBatteryStateStore
{
    private readonly Dictionary<string, DeviceStateEntry> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SoraRejectedReceiverRelation>> _rejectedSoraReceiverRelationsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, SoraReceiverRelationEvidence>> _soraReceiverEvidenceByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _identityEvidenceSync = new();
    private readonly IAppEventLog? _log;

    public DeviceBatteryStateStore(IAppEventLog? log = null) => _log = log;

    public void ObserveIdentityEvidence(IEnumerable<DeviceIdentityObservation> observations)
    {
        foreach (var observation in observations)
        {
            var paths = observation.ResolvedDeviceIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            var receiverAssociationKey = observation.AssociationReceiverHistoryKey;
            var receiverSerial = DeviceIdentity.NormalizeSerial(observation.AssociationReceiverSerial) ?? "";
            if (string.IsNullOrWhiteSpace(receiverAssociationKey))
                continue;
            var changed = false;
            var appliedReceiverSerial = receiverSerial;
            lock (_identityEvidenceSync)
            {
                var confirmSeed = observation.HistoryAnchorEvidence == HistoryAnchorEvidence.ConfirmReceiver
                    ? ResolveConfirmReceiverEvidence(
                        paths,
                        observation,
                        receiverAssociationKey,
                        receiverSerial)
                    : null;
                var effectiveReceiverSerial = confirmSeed?.ReceiverSerial ?? receiverSerial;
                appliedReceiverSerial = effectiveReceiverSerial;
                foreach (var path in paths)
                    changed |= RememberReceiverEvidence(
                        path,
                        observation,
                        receiverAssociationKey,
                        effectiveReceiverSerial,
                        confirmSeed);

                if (observation.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver)
                {
                    var rejectedPaths = !string.IsNullOrWhiteSpace(observation.DeviceId)
                        ? new[] { observation.DeviceId }
                        : paths;
                    foreach (var path in rejectedPaths)
                    {
                        if (!_rejectedSoraReceiverRelationsByPath.TryGetValue(path, out var rejectedReceivers))
                        {
                            rejectedReceivers = new List<SoraRejectedReceiverRelation>();
                            _rejectedSoraReceiverRelationsByPath[path] = rejectedReceivers;
                        }
                        if (!rejectedReceivers.Any(relation => string.Equals(
                                relation.ReceiverAssociationKey,
                                receiverAssociationKey,
                                StringComparison.OrdinalIgnoreCase)
                            && string.Equals(relation.ReceiverSerial, receiverSerial, StringComparison.OrdinalIgnoreCase)))
                        {
                            rejectedReceivers.Add(new SoraRejectedReceiverRelation(receiverAssociationKey, receiverSerial));
                            changed = true;
                        }
                    }
                }
                else if (observation.HistoryAnchorEvidence == HistoryAnchorEvidence.ConfirmReceiver)
                {
                    foreach (var path in paths)
                    {
                        if (!_rejectedSoraReceiverRelationsByPath.TryGetValue(path, out var rejectedReceivers))
                            continue;
                        changed |= rejectedReceivers.RemoveAll(relation => ReceiverAssociationsMatch(
                            relation.ReceiverAssociationKey,
                            relation.ReceiverSerial,
                            receiverAssociationKey,
                            effectiveReceiverSerial)) > 0;
                        if (rejectedReceivers.Count == 0)
                            _rejectedSoraReceiverRelationsByPath.Remove(path);
                    }
                }
                else
                {
                    continue;
                }
            }

            if (!changed)
                continue;
            _log?.Write("info", "device.identity_evidence_applied", nameof(DeviceBatteryStateStore), "success", new
            {
                history_anchor_evidence = observation.HistoryAnchorEvidence.ToString(),
                receiver_association_key = receiverAssociationKey,
                receiver_association_serial = appliedReceiverSerial,
                resolved_device_ids = paths
            });
        }
    }

    private SoraReceiverRelationSeed ResolveConfirmReceiverEvidence(
        IReadOnlyList<string> paths,
        DeviceIdentityObservation observation,
        string receiverAssociationKey,
        string incomingReceiverSerial)
    {
        var previousFacts = paths
            .Where(path => _soraReceiverEvidenceByPath.TryGetValue(path, out _))
            .Select(path => _soraReceiverEvidenceByPath[path].TryGetValue(receiverAssociationKey, out var fact)
                ? fact
                : null)
            .Where(fact => fact != null)
            .Cast<SoraReceiverRelationEvidence>()
            .Where(fact => fact.TimestampUtc <= observation.TimestampUtc)
            .ToArray();
        var effectiveReceiverSerial = incomingReceiverSerial;
        var epochAmbiguous = false;
        if (effectiveReceiverSerial.Length == 0)
        {
            var stableFacts = previousFacts
                .Select(fact => new
                {
                    Fact = fact,
                    Serial = DeviceIdentity.NormalizeSerial(fact.ReceiverSerial)
                })
                .Where(item => item.Serial != null)
                .ToArray();
            if (stableFacts.Length > 0)
            {
                var latestTimestampUtc = stableFacts.Max(item => item.Fact.TimestampUtc);
                var latestSerials = stableFacts
                    .Where(item => item.Fact.TimestampUtc == latestTimestampUtc)
                    .Select(item => item.Serial!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (latestSerials.Length == 1)
                    effectiveReceiverSerial = latestSerials[0];
                else
                    epochAmbiguous = true;
            }
        }

        var canonicalHistoryDeviceKey = observation.HistoryDeviceKey;
        if (!BatteryHistoryStore.IsSoraReceiverSerialDeviceKey(canonicalHistoryDeviceKey)
            && !epochAmbiguous)
        {
            var normalizedEffectiveSerial = DeviceIdentity.NormalizeSerial(effectiveReceiverSerial);
            canonicalHistoryDeviceKey = previousFacts
                    .Where(fact => string.Equals(
                        DeviceIdentity.NormalizeSerial(fact.ReceiverSerial),
                        normalizedEffectiveSerial,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(fact => !string.IsNullOrWhiteSpace(fact.CanonicalHistoryDeviceKey))
                    .OrderByDescending(fact => BatteryHistoryStore.IsSoraReceiverSerialDeviceKey(
                        fact.CanonicalHistoryDeviceKey))
                    .ThenByDescending(fact => fact.TimestampUtc)
                    .Select(fact => fact.CanonicalHistoryDeviceKey)
                    .FirstOrDefault()
                ?? observation.HistoryDeviceKey;
        }

        return new SoraReceiverRelationSeed(effectiveReceiverSerial, canonicalHistoryDeviceKey);
    }

    private bool RememberReceiverEvidence(
        string path,
        DeviceIdentityObservation observation,
        string receiverAssociationKey,
        string receiverSerial,
        SoraReceiverRelationSeed? confirmSeed)
    {
        if (!_soraReceiverEvidenceByPath.TryGetValue(path, out var factsByAssociation))
        {
            factsByAssociation = new Dictionary<string, SoraReceiverRelationEvidence>(StringComparer.OrdinalIgnoreCase);
            _soraReceiverEvidenceByPath[path] = factsByAssociation;
        }

        factsByAssociation.TryGetValue(receiverAssociationKey, out var previous);
        if (previous != null && previous.TimestampUtc > observation.TimestampUtc)
            return false;

        var effectiveSerial = confirmSeed?.ReceiverSerial
            ?? (receiverSerial.Length > 0
                ? receiverSerial
                : previous?.ReceiverSerial ?? "");
        var stableEpochChanged = previous != null
            && previous.ReceiverSerial.Length > 0
            && effectiveSerial.Length > 0
            && !string.Equals(previous.ReceiverSerial, effectiveSerial, StringComparison.OrdinalIgnoreCase);
        var canonicalHistoryDeviceKey = confirmSeed?.CanonicalHistoryDeviceKey
            ?? (observation.HistoryAnchorEvidence == HistoryAnchorEvidence.ConfirmReceiver
            ? !stableEpochChanged
                && previous != null
                && !string.IsNullOrWhiteSpace(previous.CanonicalHistoryDeviceKey)
                && !BatteryHistoryStore.IsSoraReceiverSerialDeviceKey(observation.HistoryDeviceKey)
                    ? previous.CanonicalHistoryDeviceKey
                    : observation.HistoryDeviceKey
            : stableEpochChanged
                ? ""
                : previous?.CanonicalHistoryDeviceKey ?? "");
        var current = new SoraReceiverRelationEvidence(
            receiverAssociationKey,
            effectiveSerial,
            canonicalHistoryDeviceKey,
            observation.HistoryAnchorEvidence,
            observation.TimestampUtc,
            observation.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver
                && string.Equals(path, observation.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (previous == current)
            return false;

        factsByAssociation[receiverAssociationKey] = current;
        return true;
    }

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
            ObserveIdentityEvidence(normalizedReadings
                .Where(reading => reading.HistoryAnchorEvidence is HistoryAnchorEvidence.ConfirmReceiver
                    or HistoryAnchorEvidence.RejectPersistedReceiver)
                .Where(reading => !string.IsNullOrWhiteSpace(reading.AssociationReceiverHistoryKey))
                .Select(reading => new DeviceIdentityObservation
                {
                    LogicalDeviceId = reading.LogicalDeviceId,
                    HistoryDeviceKey = reading.HistoryDeviceKey,
                    AssociationReceiverHistoryKey = reading.AssociationReceiverHistoryKey,
                    AssociationReceiverSerial = reading.AssociationReceiverSerial,
                    HistoryAnchorEvidence = reading.HistoryAnchorEvidence,
                    ResolvedDeviceIds = DeviceIdentity.ResolvedDeviceIds(reading),
                    DeviceName = reading.DeviceName,
                    DeviceId = reading.DeviceId,
                    DeviceSerial = reading.DeviceSerial,
                    VendorId = reading.VendorId,
                    ProductId = reading.ProductId,
                    Source = reading.Source,
                    TimestampUtc = nowUtc
                }));
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

            foreach (var normalizedReading in normalizedReadings)
            {
                var normalized = AdoptActiveReceiverRelation(normalizedReading);
                var key = CreateKey(batch.ProviderName, normalized);
                returnedKeys.Add(key);
                var isSoraWiredFallback = normalized.LogicalDeviceId.StartsWith(
                    "ninjutso-sora-v2:wired:",
                    StringComparison.OrdinalIgnoreCase);
                var mayRecoverResolvedAlias = !isSoraWiredFallback
                    || normalized.HistoryAnchorEvidence == HistoryAnchorEvidence.RecoverPersistedReceiver;

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
                var previousEntryAtCurrentKey = previousEntry;
                var currentReceiverEpochReplaced = previousEntryAtCurrentKey != null
                    && !CanPreservePolicyState(previousEntryAtCurrentKey.Reading, normalized);
                var rejectedEndpointAliases = isSoraWiredFallback && mayRecoverResolvedAlias
                    ? existing
                        .Where(pair => !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                            && _states.ContainsKey(pair.Key)
                            && SharesResolvedEndpoint(pair.Value.Reading, normalized)
                            && IsReceiverAssociationUnavailable(normalized, pair.Value.Reading))
                        .ToArray()
                    : Array.Empty<KeyValuePair<string, DeviceStateEntry>>();
                if (rejectedEndpointAliases.Length > 0)
                {
                    _log?.Write("info", "device.history_anchor_recovery_blocked", nameof(DeviceBatteryStateStore), "success", new
                    {
                        reason = "scoped_receiver_pairing_mismatch",
                        blocked_relation_count = rejectedEndpointAliases.Length,
                        blocked_receiver_association_keys = rejectedEndpointAliases
                            .Select(pair => pair.Value.Reading.AssociationReceiverHistoryKey)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        blocked_receiver_serials = rejectedEndpointAliases
                            .Select(pair => DeviceIdentity.NormalizeSerial(pair.Value.Reading.AssociationReceiverSerial) ?? "")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        resolved_device_ids = DeviceIdentity.ResolvedDeviceIds(normalized)
                    }, reading: normalized);
                }
                var endpointAliases = existing
                    .Where(pair => mayRecoverResolvedAlias
                        && !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                        && !incomingKeys.Contains(pair.Key)
                        && !claimedPriorKeys.Contains(pair.Key)
                        && _states.ContainsKey(pair.Key)
                        && SharesResolvedEndpoint(pair.Value.Reading, normalized)
                        && (!isSoraWiredFallback
                            || !IsReceiverAssociationUnavailable(normalized, pair.Value.Reading)))
                    .OrderByDescending(pair => string.Equals(
                        pair.Value.Reading.DeviceId,
                        normalized.DeviceId,
                        StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(pair => pair.Value.LastSuccessfulReadUtc)
                    .ToArray();
                var previousHistoryReading = previousEntry?.Reading
                    ?? endpointAliases.Select(pair => pair.Value.Reading).FirstOrDefault(
                        reading => !string.IsNullOrWhiteSpace(reading.HistoryDeviceKey));
                if (isSoraWiredFallback
                    && previousHistoryReading != null
                    && IsReceiverAssociationUnavailable(normalized, previousHistoryReading))
                    previousHistoryReading = null;
                var isSameSoraLogicalDevice = normalized.LogicalDeviceId.StartsWith(
                        "ninjutso-sora-v2:receiver:",
                        StringComparison.OrdinalIgnoreCase)
                    && previousHistoryReading != null
                    && string.Equals(
                        normalized.LogicalDeviceId,
                        previousHistoryReading.LogicalDeviceId,
                        StringComparison.OrdinalIgnoreCase);
                if (isSameSoraLogicalDevice
                    && previousHistoryReading != null
                    && CanPreservePolicyState(previousHistoryReading, normalized)
                    && (string.IsNullOrWhiteSpace(normalized.AssociationReceiverHistoryKey)
                        || DeviceIdentity.NormalizeSerial(normalized.AssociationReceiverSerial) == null))
                {
                    normalized = Copy(
                        normalized,
                        normalized.PowerState,
                        normalized.ExternalPowerConnected,
                        batch.ProviderName,
                        normalized.Freshness,
                        normalized.LastSuccessfulReadUtc,
                        normalized.ConsecutiveFailures,
                        associationReceiverHistoryKey: string.IsNullOrWhiteSpace(normalized.AssociationReceiverHistoryKey)
                            ? previousHistoryReading.AssociationReceiverHistoryKey
                            : normalized.AssociationReceiverHistoryKey,
                        associationReceiverSerial: DeviceIdentity.NormalizeSerial(normalized.AssociationReceiverSerial) == null
                            ? previousHistoryReading.AssociationReceiverSerial
                            : normalized.AssociationReceiverSerial);
                }
                if (((isSoraWiredFallback
                            && normalized.HistoryAnchorEvidence == HistoryAnchorEvidence.RecoverPersistedReceiver)
                        || isSameSoraLogicalDevice)
                    && previousHistoryReading != null
                    && BatteryHistoryStore.IsSoraReceiverDeviceKey(previousHistoryReading.HistoryDeviceKey)
                    && !BatteryHistoryStore.IsSoraReceiverSerialDeviceKey(normalized.HistoryDeviceKey))
                {
                    normalized = Copy(
                        normalized,
                        normalized.PowerState,
                        normalized.ExternalPowerConnected,
                        batch.ProviderName,
                        normalized.Freshness,
                        normalized.LastSuccessfulReadUtc,
                        normalized.ConsecutiveFailures,
                        previousHistoryReading.HistoryDeviceKey,
                        associationReceiverHistoryKey: previousHistoryReading.AssociationReceiverHistoryKey,
                        associationReceiverSerial: previousHistoryReading.AssociationReceiverSerial);
                }
                foreach (var alias in endpointAliases)
                {
                    if (!_states.Remove(alias.Key, out var removed))
                        continue;

                    previousEntry ??= removed;
                    claimedPriorKeys.Add(alias.Key);
                    rekeys.Add(new DeviceStateRekey
                    {
                        ProviderName = batch.ProviderName,
                        PreviousKey = alias.Key,
                        CurrentKey = key,
                        PreviousReading = removed.Reading,
                        CurrentReading = normalized,
                        PreservePolicyState = !currentReceiverEpochReplaced
                            && CanPreservePolicyState(removed.Reading, normalized)
                    });
                    _log?.Write("info", "device.identity_rekeyed", nameof(DeviceBatteryStateStore), "success", new
                    {
                        provider = batch.ProviderName,
                        previous_device = _log.DeviceToken(removed.Reading),
                        current_device = _log.DeviceToken(normalized),
                        reason = "resolved_endpoint_overlap"
                    }, reading: normalized);
                }

                if (!_states.ContainsKey(key) && mayRecoverResolvedAlias)
                {
                    var rekeyCandidates = isSoraWiredFallback
                        ? existing
                            .Where(pair => !IsReceiverAssociationUnavailable(normalized, pair.Value.Reading))
                            .ToArray()
                        : existing;
                    var prior = FindRekeyCandidate(
                        rekeyCandidates,
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
                            CurrentReading = normalized,
                            PreservePolicyState = !currentReceiverEpochReplaced
                                && CanPreservePolicyState(removed.Reading, normalized)
                        });
                        _log?.Write("info", "device.identity_rekeyed", nameof(DeviceBatteryStateStore), "success", new
                        {
                            provider = batch.ProviderName,
                            previous_device = _log.DeviceToken(removed.Reading),
                            current_device = _log.DeviceToken(normalized),
                            reason = DeviceIdentity.NormalizeSerial(normalized.DeviceSerial) != null ? "stable_serial" : "unique_path_migration"
                        }, reading: normalized);
                    }
                }

                if (previousEntryAtCurrentKey != null
                    && !string.Equals(
                        DeviceIdentity.CreateRuntimeKey(previousEntryAtCurrentKey.Reading),
                        DeviceIdentity.CreateRuntimeKey(normalized),
                        StringComparison.OrdinalIgnoreCase))
                {
                    rekeys.Add(new DeviceStateRekey
                    {
                        ProviderName = batch.ProviderName,
                        PreviousKey = key,
                        CurrentKey = key,
                        PreviousReading = previousEntryAtCurrentKey.Reading,
                        CurrentReading = normalized,
                        PreservePolicyState = CanPreservePolicyState(previousEntryAtCurrentKey.Reading, normalized)
                    });
                    _log?.Write("info", "device.identity_rekeyed", nameof(DeviceBatteryStateStore), "success", new
                    {
                        provider = batch.ProviderName,
                        previous_device = _log.DeviceToken(previousEntryAtCurrentKey.Reading),
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
                            fully_charged = normalized.IsFullyCharged,
                            previous_transport = previousEntry.Reading.ConnectionTransport.ToString(),
                            transport = normalized.ConnectionTransport.ToString()
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
                var deviceStillPresent = candidateIds.Count == 0
                    || PresenceDeviceIds(entry.Reading).Any(candidateIds.Contains);

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
            || previous.ExternalPowerConnected != current.ExternalPowerConnected
            || previous.ConnectionTransport != current.ConnectionTransport
            || previous.HistoryAnchorEvidence != current.HistoryAnchorEvidence
            || !string.Equals(
                previous.AssociationReceiverHistoryKey,
                current.AssociationReceiverHistoryKey,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                DeviceIdentity.NormalizeSerial(previous.AssociationReceiverSerial),
                DeviceIdentity.NormalizeSerial(current.AssociationReceiverSerial),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private void LogSpecificTransitions(BatteryReading previous, BatteryReading current)
    {
        if (_log == null)
            return;

        var previousExternalPower = DevicePowerSemantics.IsExternallyPowered(previous);
        var currentExternalPower = DevicePowerSemantics.IsExternallyPowered(current);
        if (previous.ConnectionTransport != current.ConnectionTransport
            || !string.Equals(previous.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _log.Write("info", "device.transport_changed", nameof(DeviceBatteryStateStore), "success", new
            {
                previous_transport = previous.ConnectionTransport.ToString(),
                transport = current.ConnectionTransport.ToString(),
                previous_device_path = previous.DeviceId,
                device_path = current.DeviceId,
                previous_product_id = previous.ProductId,
                product_id = current.ProductId,
                battery_percentage = current.BatteryPercentage,
                cable_connected = currentExternalPower,
                fact_basis = "provider_logical_device_identity"
            }, reading: current);
        }

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

    private static bool SharesResolvedEndpoint(BatteryReading previous, BatteryReading current)
    {
        var previousIds = new HashSet<string>(
            DeviceIdentity.ResolvedDeviceIds(previous),
            StringComparer.OrdinalIgnoreCase);
        return previousIds.Count > 0
            && DeviceIdentity.ResolvedDeviceIds(current).Any(previousIds.Contains);
    }

    private IEnumerable<string> PresenceDeviceIds(BatteryReading reading)
    {
        var resolved = DeviceIdentity.ResolvedDeviceIds(reading);
        if (!reading.LogicalDeviceId.StartsWith("ninjutso-sora-v2:receiver:", StringComparison.OrdinalIgnoreCase))
            return resolved;

        var receiverAssociationKey = reading.AssociationReceiverHistoryKey;
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return resolved;

        lock (_identityEvidenceSync)
        {
            return resolved
                .Where(path => !IsAnchoredReceiverRelationUnavailableNoLock(path, reading))
                .ToArray();
        }
    }

    private static bool SameHardwareFamily(BatteryReading previous, BatteryReading current)
    {
        return string.Equals(previous.VendorId?.Trim(), current.VendorId?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.ProductId?.Trim(), current.ProductId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanPreservePolicyState(BatteryReading previous, BatteryReading current)
    {
        var previousReceiverSerial = ReceiverEpochSerial(previous);
        var currentReceiverSerial = ReceiverEpochSerial(current);
        return previousReceiverSerial == null
            || currentReceiverSerial == null
            || string.Equals(previousReceiverSerial, currentReceiverSerial, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReceiverEpochSerial(BatteryReading reading)
    {
        return DeviceIdentity.NormalizeSerial(reading.AssociationReceiverSerial)
            ?? (reading.LogicalDeviceId.StartsWith(
                    "ninjutso-sora-v2:receiver:",
                    StringComparison.OrdinalIgnoreCase)
                ? DeviceIdentity.NormalizeSerial(reading.DeviceSerial)
                : null);
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

    private BatteryReading AdoptActiveReceiverRelation(BatteryReading reading)
    {
        var isWiredRecovery = reading.LogicalDeviceId.StartsWith(
                "ninjutso-sora-v2:wired:",
                StringComparison.OrdinalIgnoreCase)
            && reading.HistoryAnchorEvidence == HistoryAnchorEvidence.RecoverPersistedReceiver;
        var isReceiverReading = reading.LogicalDeviceId.StartsWith(
            "ninjutso-sora-v2:receiver:",
            StringComparison.OrdinalIgnoreCase);
        if (!isWiredRecovery && !isReceiverReading)
            return reading;
        if (isReceiverReading
            && (DeviceIdentity.NormalizeSerial(reading.AssociationReceiverSerial) != null
                || BatteryHistoryStore.IsSoraReceiverSerialDeviceKey(reading.HistoryDeviceKey)))
            return reading;

        SoraReceiverRelationEvidence? active;
        lock (_identityEvidenceSync)
        {
            active = DeviceIdentity.ResolvedDeviceIds(reading)
                .Where(path => _soraReceiverEvidenceByPath.ContainsKey(path))
                .SelectMany(path => _soraReceiverEvidenceByPath[path].Values)
                .Where(fact => fact.Evidence == HistoryAnchorEvidence.ConfirmReceiver)
                .Where(fact => !string.IsNullOrWhiteSpace(fact.CanonicalHistoryDeviceKey))
                .Where(fact => !isReceiverReading
                    || string.IsNullOrWhiteSpace(reading.AssociationReceiverHistoryKey)
                    || ReceiverAssociationsMatch(
                        fact.ReceiverAssociationKey,
                        fact.ReceiverSerial,
                        reading.AssociationReceiverHistoryKey,
                        reading.AssociationReceiverSerial))
                .GroupBy(
                    fact => $"{fact.ReceiverAssociationKey}\u001F{fact.ReceiverSerial}\u001F{fact.CanonicalHistoryDeviceKey}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(fact => fact.TimestampUtc).First())
                .OrderByDescending(fact => fact.TimestampUtc)
                .FirstOrDefault();
        }

        if (active == null)
            return reading;

        return Copy(
            reading,
            reading.PowerState,
            reading.ExternalPowerConnected,
            reading.ProviderName,
            reading.Freshness,
            reading.LastSuccessfulReadUtc,
            reading.ConsecutiveFailures,
            active.CanonicalHistoryDeviceKey,
            associationReceiverHistoryKey: active.ReceiverAssociationKey,
            associationReceiverSerial: active.ReceiverSerial);
    }

    private bool IsReceiverAssociationUnavailable(BatteryReading wiredReading, BatteryReading receiverAnchoredReading)
    {
        if (!wiredReading.LogicalDeviceId.StartsWith("ninjutso-sora-v2:wired:", StringComparison.OrdinalIgnoreCase))
            return false;

        var receiverAssociationKey = receiverAnchoredReading.AssociationReceiverHistoryKey;
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return false;

        lock (_identityEvidenceSync)
        {
            return DeviceIdentity.ResolvedDeviceIds(wiredReading)
                .Any(path => IsAnchoredReceiverRelationUnavailableNoLock(path, receiverAnchoredReading));
        }
    }

    private bool IsAnchoredReceiverRelationUnavailableNoLock(
        string path,
        BatteryReading receiverAnchoredReading)
    {
        var receiverAssociationKey = receiverAnchoredReading.AssociationReceiverHistoryKey;
        if (string.IsNullOrWhiteSpace(receiverAssociationKey))
            return false;

        if (_soraReceiverEvidenceByPath.TryGetValue(path, out var factsByAssociation))
        {
            if (factsByAssociation.TryGetValue(receiverAssociationKey, out var scopedFact))
            {
                var sameEpoch = ReceiverAssociationsMatch(
                    scopedFact.ReceiverAssociationKey,
                    scopedFact.ReceiverSerial,
                    receiverAssociationKey,
                    receiverAnchoredReading.AssociationReceiverSerial);
                if ((scopedFact.Evidence == HistoryAnchorEvidence.RejectPersistedReceiver
                        && (scopedFact.RejectsPairingEndpoint || !sameEpoch))
                    || (scopedFact.Evidence == HistoryAnchorEvidence.ConfirmReceiver && !sameEpoch))
                    return true;
            }

            var latestActive = factsByAssociation.Values
                .Where(fact => fact.Evidence == HistoryAnchorEvidence.ConfirmReceiver)
                .OrderByDescending(fact => fact.TimestampUtc)
                .FirstOrDefault();
            if (latestActive != null
                && !ReceiverAssociationsMatch(
                    latestActive.ReceiverAssociationKey,
                    latestActive.ReceiverSerial,
                    receiverAssociationKey,
                    receiverAnchoredReading.AssociationReceiverSerial))
                return true;
        }

        return _rejectedSoraReceiverRelationsByPath.TryGetValue(path, out var rejectedReceivers)
            && rejectedReceivers.Any(relation => ReceiverAssociationsMatch(
                relation.ReceiverAssociationKey,
                relation.ReceiverSerial,
                receiverAssociationKey,
                receiverAnchoredReading.AssociationReceiverSerial));
    }

    private static bool ReceiverAssociationsMatch(
        string firstAssociationKey,
        string? firstSerial,
        string secondAssociationKey,
        string? secondSerial)
    {
        if (!string.Equals(firstAssociationKey, secondAssociationKey, StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedFirstSerial = DeviceIdentity.NormalizeSerial(firstSerial);
        var normalizedSecondSerial = DeviceIdentity.NormalizeSerial(secondSerial);
        return normalizedFirstSerial == null
            || normalizedSecondSerial == null
            || string.Equals(normalizedFirstSerial, normalizedSecondSerial, StringComparison.OrdinalIgnoreCase);
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
            LogicalDeviceId = !string.IsNullOrWhiteSpace(observed?.LogicalDeviceId) ? observed.LogicalDeviceId : lastKnown.LogicalDeviceId,
            HistoryDeviceKey = !string.IsNullOrWhiteSpace(observed?.HistoryDeviceKey) ? observed.HistoryDeviceKey : lastKnown.HistoryDeviceKey,
            AssociationReceiverHistoryKey = !string.IsNullOrWhiteSpace(observed?.AssociationReceiverHistoryKey)
                ? observed.AssociationReceiverHistoryKey
                : lastKnown.AssociationReceiverHistoryKey,
            AssociationReceiverSerial = !string.IsNullOrWhiteSpace(observed?.AssociationReceiverSerial)
                ? observed.AssociationReceiverSerial
                : lastKnown.AssociationReceiverSerial,
            ConnectionTransport = observed?.ConnectionTransport ?? lastKnown.ConnectionTransport,
            HistoryAnchorEvidence = observed?.HistoryAnchorEvidence ?? lastKnown.HistoryAnchorEvidence,
            ResolvedDeviceIds = observed?.ResolvedDeviceIds.Count > 0 ? observed.ResolvedDeviceIds : lastKnown.ResolvedDeviceIds,
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
        int consecutiveFailures,
        string? historyDeviceKey = null,
        HistoryAnchorEvidence? historyAnchorEvidence = null,
        string? associationReceiverHistoryKey = null,
        string? associationReceiverSerial = null)
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
            LogicalDeviceId = reading.LogicalDeviceId,
            HistoryDeviceKey = historyDeviceKey ?? reading.HistoryDeviceKey,
            AssociationReceiverHistoryKey = associationReceiverHistoryKey ?? reading.AssociationReceiverHistoryKey,
            AssociationReceiverSerial = associationReceiverSerial ?? reading.AssociationReceiverSerial,
            ConnectionTransport = reading.ConnectionTransport,
            HistoryAnchorEvidence = historyAnchorEvidence ?? reading.HistoryAnchorEvidence,
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

    private static string CreateKey(string providerName, BatteryReading reading)
    {
        var serial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial);
        var identity = !string.IsNullOrWhiteSpace(reading.LogicalDeviceId)
            ? $"logical:{reading.LogicalDeviceId.Trim()}"
            : !string.IsNullOrWhiteSpace(reading.DeviceId)
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

    private sealed record SoraRejectedReceiverRelation(string ReceiverAssociationKey, string ReceiverSerial);

    private sealed record SoraReceiverRelationSeed(
        string ReceiverSerial,
        string CanonicalHistoryDeviceKey);

    private sealed record SoraReceiverRelationEvidence(
        string ReceiverAssociationKey,
        string ReceiverSerial,
        string CanonicalHistoryDeviceKey,
        HistoryAnchorEvidence Evidence,
        DateTime TimestampUtc,
        bool RejectsPairingEndpoint);
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
    public bool PreservePolicyState { get; init; } = true;
}
