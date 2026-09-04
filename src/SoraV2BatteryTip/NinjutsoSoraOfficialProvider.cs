using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class NinjutsoSoraOfficialProvider : IBatteryProvider
{
    private const int VendorId = 0x1915;
    private const byte FeatureReportId = 0x05;
    private const byte BatteryCommand = 0x15;
    private const byte PairedProductCommand = 0x28;
    private const int MinimumFeatureLength = 32;
    private const int IoTimeoutMs = 700;
    private static readonly TimeSpan PairCacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly HashSet<int> SupportedProductIds = new()
    {
        0xAE11, 0xAE12, 0xAE13, 0xAE14, 0xAE15, 0xAE16,
        0xAE1C, 0xAE8A, 0xAE8C
    };
    private readonly IAppEventLog? _log;
    private readonly Action<IReadOnlyList<DeviceIdentityObservation>>? _identityObservationSink;
    private readonly Dictionary<string, PairedProductCacheEntry> _pairedProductCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pairingSync = new();

    public NinjutsoSoraOfficialProvider(
        IAppEventLog? log = null,
        Action<IReadOnlyList<DeviceIdentityObservation>>? identityObservationSink = null)
    {
        _log = log;
        _identityObservationSink = identityObservationSink;
    }

    public string Name => "SORA V2 Official HID";
    public int Priority => 300;

    public bool IsAvailable(HidInventorySnapshot inventory) => EnumerateCandidateDevices(inventory).Any();

    public Task<ProviderReadResult> ReadAsync(HidInventorySnapshot inventory, CancellationToken token)
    {
        return Task.Run(() => ReadAllOnce(inventory, token), token);
    }

    private ProviderReadResult ReadAllOnce(HidInventorySnapshot inventory, CancellationToken token)
    {
        var devices = EnumerateCandidateDevices(inventory).ToArray();
        var candidates = devices
            .Select(device => new HidCandidate(device, new NinjutsoSoraEndpoint(
                Safe(() => device.DevicePath),
                device.ProductID,
                Safe(() => device.GetProductName()),
                Safe(() => device.GetSerialNumber()))))
            .ToArray();
        var pairedProductIds = ResolvePairedProductIds(candidates);
        var transportPlan = NinjutsoSoraTransportPolicy.CreatePlan(
            candidates.Select(candidate => candidate.Endpoint).ToArray(),
            pairedProductIds);
        var identityObservations = CreateIdentityObservations(transportPlan);
        PublishIdentityObservations(identityObservations);
        _log?.Write("debug", "provider.transport_plan", Name, "success", new
        {
            reason = transportPlan.Reason,
            selected = transportPlan.SelectedEndpoints.Select(selection => new
            {
                device_id = selection.Endpoint.DeviceId,
                product_id = $"0x{selection.Endpoint.ProductId:X4}",
                transport = selection.Transport.ToString(),
                logical_device_id = selection.LogicalDeviceId,
                history_anchor_evidence = selection.HistoryAnchorEvidence.ToString(),
                association_receiver_device_id = selection.AssociationReceiverEndpoint?.DeviceId,
                resolved_device_ids = selection.ResolvedDeviceIds
            }).ToArray(),
            suppressed = transportPlan.SuppressedEndpoints.Select(endpoint => new
            {
                device_id = endpoint.DeviceId,
                product_id = $"0x{endpoint.ProductId:X4}"
            }).ToArray()
        });
        if (transportPlan.SuppressedEndpoints.Count > 0)
        {
            _log?.Write("info", "provider.transport_arbitrated", Name, "success", new
            {
                reason = transportPlan.Reason,
                selected = transportPlan.SelectedEndpoints.Select(selection => new
                {
                    device_id = selection.Endpoint.DeviceId,
                    product_id = $"0x{selection.Endpoint.ProductId:X4}",
                    transport = selection.Transport.ToString(),
                    logical_device_id = selection.LogicalDeviceId,
                    history_anchor_evidence = selection.HistoryAnchorEvidence.ToString(),
                    association_receiver_device_id = selection.AssociationReceiverEndpoint?.DeviceId,
                    resolved_device_ids = selection.ResolvedDeviceIds
                }).ToArray(),
                suppressed = transportPlan.SuppressedEndpoints.Select(endpoint => new
                {
                    device_id = endpoint.DeviceId,
                    product_id = $"0x{endpoint.ProductId:X4}"
                }).ToArray()
            });
        }

        var readings = new List<BatteryReading>();
        foreach (var selection in transportPlan.SelectedEndpoints)
        {
            token.ThrowIfCancellationRequested();

            var candidate = candidates.FirstOrDefault(item => ReferenceEquals(item.Endpoint, selection.Endpoint))
                ?? candidates.FirstOrDefault(item => string.Equals(
                    item.Endpoint.DeviceId,
                    selection.Endpoint.DeviceId,
                    StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
                continue;

            var readingHistoryEvidence = selection.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver
                ? HistoryAnchorEvidence.RecoverPersistedReceiver
                : selection.HistoryAnchorEvidence;
            var receiverAssociationEndpoint = selection.AssociationReceiverEndpoint
                ?? (selection.Transport == DeviceConnectionTransport.Receiver ? selection.Endpoint : null);
            var readingAssociationKey = selection.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver
                || receiverAssociationEndpoint == null
                    ? ""
                    : CreateReceiverAssociationKey(receiverAssociationEndpoint);
            var readingAssociationSerial = selection.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver
                || receiverAssociationEndpoint == null
                    ? ""
                    : receiverAssociationEndpoint.DeviceSerial;
            var reading = TryReadDevice(
                candidate.Device,
                selection.LogicalDeviceId,
                CreateHistoryDeviceKey(selection.HistoryEndpoint),
                readingAssociationKey,
                readingAssociationSerial,
                selection.Transport,
                selection.ResolvedDeviceIds,
                readingHistoryEvidence);
            if (reading != null && !readings.Any(existing => string.Equals(existing.DeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase)))
                readings.Add(reading);
        }

        return new ProviderReadResult
        {
            Readings = readings,
            IdentityObservations = identityObservations,
            CandidateFound = candidates.Length > 0,
            CandidateDeviceIds = candidates
                .Select(candidate => candidate.Endpoint.DeviceId)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray()
        };
    }

    internal static IReadOnlyList<DeviceIdentityObservation> CreateIdentityObservations(
        NinjutsoSoraTransportPlan transportPlan)
    {
        var observations = new List<DeviceIdentityObservation>();
        foreach (var selection in transportPlan.SelectedEndpoints.Where(selection =>
                     selection.HistoryAnchorEvidence is HistoryAnchorEvidence.ConfirmReceiver
                         or HistoryAnchorEvidence.RejectPersistedReceiver))
        {
            var timestampUtc = DateTime.UtcNow;
            if (selection.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver
                && selection.AssociationReceiverEndpoint is { } receiver)
            {
                observations.Add(new DeviceIdentityObservation
                {
                    LogicalDeviceId = NinjutsoSoraTransportPolicy.CreateReceiverLogicalId(receiver),
                    HistoryDeviceKey = CreateHistoryDeviceKey(receiver),
                    AssociationReceiverHistoryKey = CreateReceiverAssociationKey(receiver),
                    AssociationReceiverSerial = receiver.DeviceSerial,
                    HistoryAnchorEvidence = HistoryAnchorEvidence.ConfirmReceiver,
                    ResolvedDeviceIds = string.IsNullOrWhiteSpace(receiver.DeviceId)
                        ? Array.Empty<string>()
                        : new[] { receiver.DeviceId },
                    DeviceName = receiver.DeviceName,
                    DeviceId = receiver.DeviceId,
                    DeviceSerial = receiver.DeviceSerial,
                    VendorId = $"0x{VendorId:X4}",
                    ProductId = $"0x{receiver.ProductId:X4}",
                    Source = "SORA V2 Official HID",
                    TimestampUtc = timestampUtc
                });
            }

            observations.Add(new DeviceIdentityObservation
            {
                LogicalDeviceId = selection.LogicalDeviceId,
                HistoryDeviceKey = CreateHistoryDeviceKey(selection.HistoryEndpoint),
                AssociationReceiverHistoryKey = selection.AssociationReceiverEndpoint == null
                    ? ""
                    : CreateReceiverAssociationKey(selection.AssociationReceiverEndpoint),
                AssociationReceiverSerial = selection.AssociationReceiverEndpoint?.DeviceSerial ?? "",
                HistoryAnchorEvidence = selection.HistoryAnchorEvidence,
                ResolvedDeviceIds = selection.ResolvedDeviceIds,
                DeviceName = selection.Endpoint.DeviceName,
                DeviceId = selection.Endpoint.DeviceId,
                DeviceSerial = selection.Endpoint.DeviceSerial,
                VendorId = $"0x{VendorId:X4}",
                ProductId = $"0x{selection.Endpoint.ProductId:X4}",
                Source = "SORA V2 Official HID",
                TimestampUtc = timestampUtc
            });
        }

        return observations;
    }

    private void PublishIdentityObservations(IReadOnlyList<DeviceIdentityObservation> observations)
    {
        if (observations.Count == 0 || _identityObservationSink == null)
            return;

        try
        {
            _identityObservationSink(observations);
            _log?.Write("debug", "provider.identity_evidence_published", Name, "success", new
            {
                observation_count = observations.Count,
                evidence = observations.Select(observation => observation.HistoryAnchorEvidence.ToString()).ToArray()
            });
        }
        catch (Exception ex)
        {
            _log?.Write("error", "provider.identity_evidence_publish_failed", Name, "failure", new
            {
                observation_count = observations.Count
            }, ex);
        }
    }

    private IReadOnlyDictionary<string, int?> ResolvePairedProductIds(IReadOnlyList<HidCandidate> candidates)
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count != 2
            || candidates.Count(candidate => NinjutsoSoraTransportPolicy.IsReceiverProductId(candidate.Endpoint.ProductId)) != 1
            || candidates.Count(candidate => NinjutsoSoraTransportPolicy.IsDirectMouseProductId(candidate.Endpoint.ProductId)) != 1)
            return result;

        var receiver = candidates.Single(candidate => NinjutsoSoraTransportPolicy.IsReceiverProductId(candidate.Endpoint.ProductId));
        if (string.IsNullOrWhiteSpace(receiver.Endpoint.DeviceId))
            return result;

        var pairedProductId = TryReadPairedProductId(receiver.Device);
        if (pairedProductId is > 0)
        {
            var receiverSerial = DeviceIdentity.NormalizeSerial(receiver.Endpoint.DeviceSerial) ?? "";
            lock (_pairingSync)
                _pairedProductCache[receiver.Endpoint.DeviceId] = new PairedProductCacheEntry(
                    pairedProductId.Value,
                    DateTime.UtcNow,
                    receiverSerial);
        }
        else
        {
            var directProductId = candidates.Single(candidate =>
                NinjutsoSoraTransportPolicy.IsDirectMouseProductId(candidate.Endpoint.ProductId)).Endpoint.ProductId;
            var receiverSerial = DeviceIdentity.NormalizeSerial(receiver.Endpoint.DeviceSerial) ?? "";
            PairedProductCacheEntry? cached;
            PairedProductCacheEntry? invalidatedCache = null;
            string? cacheInvalidationReason = null;
            lock (_pairingSync)
            {
                _pairedProductCache.TryGetValue(receiver.Endpoint.DeviceId, out cached);
                if (cached != null)
                {
                    cacheInvalidationReason = DateTime.UtcNow - cached.LastResolvedUtc > PairCacheLifetime
                        ? "expired"
                        : !ReceiverSerialMatchesPairCache(cached.ReceiverSerial, receiverSerial)
                            ? "receiver_serial_epoch_changed"
                            : null;
                    if (cacheInvalidationReason != null)
                    {
                        invalidatedCache = cached;
                        _pairedProductCache.Remove(receiver.Endpoint.DeviceId);
                        cached = null;
                    }
                }
            }

            if (invalidatedCache != null)
            {
                _log?.Write("info", "provider.receiver_pair_cache_invalidated", Name, "success", new
                {
                    reason = cacheInvalidationReason,
                    cached_paired_product_id = $"0x{invalidatedCache.ProductId:X4}",
                    cached_receiver_serial = invalidatedCache.ReceiverSerial,
                    current_receiver_serial = receiverSerial,
                    last_resolved_utc = invalidatedCache.LastResolvedUtc
                }, reading: DeviceForLog(receiver.Device));
            }

            if (cached?.ProductId == directProductId)
            {
                pairedProductId = cached.ProductId;
                _log?.Write("info", "provider.receiver_pair_cache_used", Name, "degraded", new
                {
                    paired_product_id = $"0x{cached.ProductId:X4}",
                    last_resolved_utc = cached.LastResolvedUtc,
                    cache_lifetime_seconds = PairCacheLifetime.TotalSeconds,
                    reason = "live_pair_query_unresolved"
                }, reading: DeviceForLog(receiver.Device));
            }
        }
        result[receiver.Endpoint.DeviceId] = pairedProductId;
        return result;
    }

    private int? TryReadPairedProductId(HidDevice device)
    {
        if (!device.TryOpen(out var stream) || stream == null)
        {
            _log?.Write("warn", "provider.receiver_pair_open_failed", Name, "failure", reading: DeviceForLog(device));
            return null;
        }

        using (stream)
        {
            try
            {
                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;
                var length = Math.Max(MinimumFeatureLength, SafeInt(device.GetMaxFeatureReportLength));
                var request = new byte[length];
                request[0] = FeatureReportId;
                request[1] = PairedProductCommand;
                request[4] = 0x01;
                request[7] = 0x02;

                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    stream.SetFeature(request, 0, request.Length);
                    Thread.Sleep(attempt == 1 ? 15 : 5);
                    var response = new byte[length];
                    response[0] = FeatureReportId;
                    stream.GetFeature(response, 0, response.Length);
                    var pairedProductId = ParsePairedProductIdResponse(response);
                    if (pairedProductId is not > 0)
                        continue;

                    _log?.Write("info", "provider.receiver_pair_resolved", Name, "success", new
                    {
                        paired_product_id = $"0x{pairedProductId.Value:X4}",
                        attempt
                    }, reading: DeviceForLog(device));
                    return pairedProductId;
                }

                _log?.Write("warn", "provider.receiver_pair_unresolved", Name, "degraded", new
                {
                    command = $"0x{PairedProductCommand:X2}",
                    attempts = 2
                }, reading: DeviceForLog(device));
                return null;
            }
            catch (Exception ex)
            {
                _log?.Write("warn", "provider.receiver_pair_failed", Name, "degraded", new
                {
                    operation = "paired_product_query",
                    command = $"0x{PairedProductCommand:X2}",
                    timeout_ms = IoTimeoutMs
                }, ex, DeviceForLog(device));
                return null;
            }
        }
    }

    internal static int? ParsePairedProductIdResponse(byte[] response)
    {
        if (response.Length <= 10
            || response[0] != FeatureReportId
            || response[1] != PairedProductCommand)
            return null;

        var pairedProductId = response[10] << 8 | response[9];
        return pairedProductId > 0 ? pairedProductId : null;
    }

    internal static bool ReceiverSerialMatchesPairCache(string? cachedSerial, string? currentSerial)
    {
        return string.Equals(
            DeviceIdentity.NormalizeSerial(cachedSerial) ?? "",
            DeviceIdentity.NormalizeSerial(currentSerial) ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    private BatteryReading? TryReadDevice(
        HidDevice device,
        string logicalDeviceId,
        string historyDeviceKey,
        string associationReceiverHistoryKey,
        string associationReceiverSerial,
        DeviceConnectionTransport connectionTransport,
        IReadOnlyList<string> resolvedDeviceIds,
        HistoryAnchorEvidence historyAnchorEvidence)
    {
        var contextReading = DeviceForLog(
            device,
            logicalDeviceId,
            historyDeviceKey,
            associationReceiverHistoryKey,
            associationReceiverSerial,
            connectionTransport,
            resolvedDeviceIds,
            historyAnchorEvidence);
        if (!device.TryOpen(out var stream) || stream == null)
        {
            _log?.Write("warn", "provider.device_open_failed", Name, "failure", reading: contextReading);
            return null;
        }

        using (stream)
        {
            try
            {
                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;

                var response = QueryBatteryReport(stream, device, contextReading);
                var reading = ParseResponse(response);
                if (reading == null)
                {
                    _log?.Write("warn", "provider.parse_rejected", Name, "failure", new
                    {
                        reason = ParseFailureReason(response),
                        report_length = response.Length
                    }, reading: contextReading);
                }
                return reading == null
                    ? null
                    : EnrichReading(
                        reading,
                        device,
                        logicalDeviceId,
                        historyDeviceKey,
                        associationReceiverHistoryKey,
                        associationReceiverSerial,
                        connectionTransport,
                        resolvedDeviceIds,
                        historyAnchorEvidence);
            }
            catch (Exception ex)
            {
                _log?.Write("warn", "provider.io_failed", Name, "failure", new
                {
                    operation = "feature_query",
                    timeout_ms = IoTimeoutMs
                }, ex, contextReading);
                return null;
            }
        }
    }

    private byte[] QueryBatteryReport(HidStream stream, HidDevice device, BatteryReading contextReading)
    {
        var length = Math.Max(MinimumFeatureLength, SafeInt(device.GetMaxFeatureReportLength));
        var request = new byte[length];
        request[0] = FeatureReportId;
        request[1] = BatteryCommand;
        request[4] = 0x01;
        request[7] = 0x04;

        stream.SetFeature(request, 0, request.Length);
        Thread.Sleep(15);

        var response = new byte[length];
        response[0] = FeatureReportId;
        stream.GetFeature(response, 0, response.Length);

        if (response.Length > 9 && response[9] == 0)
        {
            _log?.Write("debug", "provider.zero_battery_retry", Name, "success", new
            {
                report_id = $"0x{FeatureReportId:X2}",
                command = $"0x{BatteryCommand:X2}"
            }, reading: contextReading);
            Thread.Sleep(15);
            stream.SetFeature(request, 0, request.Length);
            Thread.Sleep(5);
            response[0] = FeatureReportId;
            stream.GetFeature(response, 0, response.Length);
        }

        return response;
    }

    private static BatteryReading? ParseResponse(byte[] response)
    {
        if (response.Length <= 10)
            return null;

        if (response[0] != FeatureReportId || response[1] != BatteryCommand)
            return null;

        var battery = response[9];
        if (battery is < 1 or > 100)
            return null;

        var charging = response[10] == 1;
        return new BatteryReading
        {
            BatteryPercentage = battery,
            IsCharging = charging,
            IsFullyCharged = charging && battery >= 100,
            IsOnline = true,
            IsCableConnected = charging,
            PowerState = charging
                ? battery >= 100 ? DevicePowerState.FullyCharged : DevicePowerState.Charging
                : DevicePowerState.Discharging,
            ExternalPowerConnected = charging,
            Source = "SORA V2 Official HID"
        };
    }

    private static string ParseFailureReason(byte[] response)
    {
        if (response.Length <= 10)
            return "short_report";
        if (response[0] != FeatureReportId)
            return "wrong_report_id";
        if (response[1] != BatteryCommand)
            return "wrong_command";
        if (response[9] is < 1 or > 100)
            return "battery_out_of_range";
        return "unknown";
    }

    private static BatteryReading DeviceForLog(
        HidDevice device,
        string logicalDeviceId = "",
        string historyDeviceKey = "",
        string associationReceiverHistoryKey = "",
        string associationReceiverSerial = "",
        DeviceConnectionTransport? connectionTransport = null,
        IReadOnlyList<string>? resolvedDeviceIds = null,
        HistoryAnchorEvidence historyAnchorEvidence = HistoryAnchorEvidence.None)
    {
        var deviceId = Safe(() => device.DevicePath);
        return new BatteryReading
        {
            HasBatteryPercentage = false,
            LogicalDeviceId = logicalDeviceId,
            HistoryDeviceKey = historyDeviceKey,
            AssociationReceiverHistoryKey = associationReceiverHistoryKey,
            AssociationReceiverSerial = associationReceiverSerial,
            ConnectionTransport = connectionTransport ?? NinjutsoSoraTransportPolicy.GetTransport(device.ProductID),
            HistoryAnchorEvidence = historyAnchorEvidence,
            ResolvedDeviceIds = resolvedDeviceIds
                ?? (string.IsNullOrWhiteSpace(deviceId) ? Array.Empty<string>() : new[] { deviceId }),
            DeviceName = Safe(() => device.GetProductName()),
            DeviceId = deviceId,
            DeviceSerial = Safe(() => device.GetSerialNumber()),
            VendorId = $"0x{device.VendorID:X4}",
            ProductId = $"0x{device.ProductID:X4}",
            Source = "SORA V2 Official HID"
        };
    }

    private static BatteryReading EnrichReading(
        BatteryReading reading,
        HidDevice device,
        string logicalDeviceId,
        string historyDeviceKey,
        string associationReceiverHistoryKey,
        string associationReceiverSerial,
        DeviceConnectionTransport connectionTransport,
        IReadOnlyList<string> resolvedDeviceIds,
        HistoryAnchorEvidence historyAnchorEvidence)
    {
        var power = NinjutsoSoraTransportPolicy.ResolvePowerFacts(
            reading.BatteryPercentage,
            reading.IsCharging,
            connectionTransport);
        return new BatteryReading
        {
            BatteryPercentage = reading.BatteryPercentage,
            HasBatteryPercentage = reading.HasBatteryPercentage,
            IsCharging = power.IsCharging,
            IsFullyCharged = power.IsFullyCharged,
            IsOnline = reading.IsOnline,
            IsCableConnected = power.IsCableConnected,
            PowerState = power.PowerState,
            ExternalPowerConnected = power.ExternalPowerConnected,
            LogicalDeviceId = logicalDeviceId,
            HistoryDeviceKey = historyDeviceKey,
            AssociationReceiverHistoryKey = associationReceiverHistoryKey,
            AssociationReceiverSerial = associationReceiverSerial,
            ConnectionTransport = connectionTransport,
            HistoryAnchorEvidence = historyAnchorEvidence,
            ResolvedDeviceIds = resolvedDeviceIds,
            DeviceName = Safe(() => device.GetProductName()),
            DeviceId = Safe(() => device.DevicePath),
            DeviceSerial = Safe(() => device.GetSerialNumber()),
            VendorId = $"0x{device.VendorID:X4}",
            ProductId = $"0x{device.ProductID:X4}",
            Source = reading.Source,
            TimestampUtc = reading.TimestampUtc
        };
    }

    private static string CreateHistoryDeviceKey(NinjutsoSoraEndpoint endpoint)
    {
        return BatteryHistoryStore.CreateDeviceKey(new BatteryReading
        {
            DeviceName = endpoint.DeviceName,
            DeviceId = endpoint.DeviceId,
            DeviceSerial = endpoint.DeviceSerial,
            VendorId = $"0x{VendorId:X4}",
            ProductId = $"0x{endpoint.ProductId:X4}",
            Source = "SORA V2 Official HID"
        });
    }

    private static string CreateReceiverAssociationKey(NinjutsoSoraEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.DeviceId))
            return CreateHistoryDeviceKey(endpoint);

        return BatteryHistoryStore.CreateDeviceKey(new BatteryReading
        {
            DeviceName = endpoint.DeviceName,
            DeviceId = endpoint.DeviceId,
            DeviceSerial = "",
            VendorId = $"0x{VendorId:X4}",
            ProductId = $"0x{endpoint.ProductId:X4}",
            Source = "SORA V2 Official HID"
        });
    }

    private static IEnumerable<HidDevice> EnumerateCandidateDevices(HidInventorySnapshot inventory)
    {
        return inventory.Devices
            .Where(device => device.VendorID == VendorId)
            .Where(device => SupportedProductIds.Contains(device.ProductID))
            .Where(device => SafeInt(device.GetMaxFeatureReportLength) >= MinimumFeatureLength)
            .OrderByDescending(DeviceScore)
            .ToArray();
    }

    private static int DeviceScore(HidDevice device)
    {
        var score = 0;
        var path = Safe(() => device.DevicePath);
        var name = Safe(() => device.GetProductName());

        if (device.ProductID is 0xAE1C or 0xAE8A or 0xAE8C)
            score += 100;
        if (path.Contains("&mi_01", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (path.Contains("&col04", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (name.Contains("Sora", StringComparison.OrdinalIgnoreCase))
            score += 40;

        score += Math.Min(100, SafeInt(device.GetMaxFeatureReportLength) / 8);
        return score;
    }

    public static bool HandlesProfile(string? vendorId, IEnumerable<string> productIds)
    {
        if (ParseInt(vendorId) != VendorId)
            return false;

        return productIds.Any(productId => ParseInt(productId) is int parsed && SupportedProductIds.Contains(parsed));
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : null;

        return int.TryParse(value, out var dec) ? dec : null;
    }

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private sealed record HidCandidate(HidDevice Device, NinjutsoSoraEndpoint Endpoint);
    private sealed record PairedProductCacheEntry(
        int ProductId,
        DateTime LastResolvedUtc,
        string ReceiverSerial);
}
