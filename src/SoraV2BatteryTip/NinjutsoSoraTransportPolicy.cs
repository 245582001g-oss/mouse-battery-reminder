namespace SoraV2BatteryTip;

internal sealed record NinjutsoSoraEndpoint(
    string DeviceId,
    int ProductId,
    string DeviceName,
    string DeviceSerial);

internal sealed record NinjutsoSoraEndpointSelection(
    NinjutsoSoraEndpoint Endpoint,
    NinjutsoSoraEndpoint HistoryEndpoint,
    string LogicalDeviceId,
    DeviceConnectionTransport Transport,
    IReadOnlyList<string> ResolvedDeviceIds,
    HistoryAnchorEvidence HistoryAnchorEvidence = HistoryAnchorEvidence.None,
    NinjutsoSoraEndpoint? AssociationReceiverEndpoint = null);

internal sealed class NinjutsoSoraTransportPlan
{
    public IReadOnlyList<NinjutsoSoraEndpointSelection> SelectedEndpoints { get; init; }
        = Array.Empty<NinjutsoSoraEndpointSelection>();
    public IReadOnlyList<NinjutsoSoraEndpoint> SuppressedEndpoints { get; init; }
        = Array.Empty<NinjutsoSoraEndpoint>();
    public string Reason { get; init; } = "independent_endpoints";
}

internal readonly record struct NinjutsoSoraPowerFacts(
    bool IsCharging,
    bool IsFullyCharged,
    bool IsCableConnected,
    DevicePowerState PowerState,
    bool ExternalPowerConnected);

internal static class NinjutsoSoraTransportPolicy
{
    internal const int SoraV2ReceiverProductId = 0xAE1C;

    private static readonly HashSet<int> ReceiverProductIds = new()
    {
        0xAE1C, 0xAE8A, 0xAE8C
    };

    private static readonly HashSet<int> DirectMouseProductIds = new()
    {
        0xAE11, 0xAE12, 0xAE13, 0xAE14, 0xAE15, 0xAE16
    };

    public static NinjutsoSoraTransportPlan CreatePlan(
        IReadOnlyList<NinjutsoSoraEndpoint> endpoints,
        IReadOnlyDictionary<string, int?>? pairedProductIds = null)
    {
        var independent = endpoints
            .Select(endpoint => new NinjutsoSoraEndpointSelection(
                endpoint,
                endpoint,
                CreateEndpointLogicalId(endpoint),
                GetTransport(endpoint.ProductId),
                ResolvedIds(endpoint)))
            .ToArray();

        var hasReceiver = endpoints.Any(endpoint => IsReceiverProductId(endpoint.ProductId));
        if (!hasReceiver)
        {
            return new NinjutsoSoraTransportPlan
            {
                SelectedEndpoints = WithWiredHistoryEvidence(independent, HistoryAnchorEvidence.RecoverPersistedReceiver),
                Reason = "receiver_absent"
            };
        }

        if (endpoints.Count != 2)
        {
            return new NinjutsoSoraTransportPlan
            {
                SelectedEndpoints = independent
            };
        }

        var receivers = endpoints
            .Where(endpoint => IsReceiverProductId(endpoint.ProductId))
            .ToArray();
        var wiredMice = endpoints
            .Where(endpoint => DirectMouseProductIds.Contains(endpoint.ProductId))
            .ToArray();
        if (receivers.Length != 1 || wiredMice.Length != 1)
        {
            return new NinjutsoSoraTransportPlan
            {
                SelectedEndpoints = independent
            };
        }

        if (HasConflictingStableSerials(receivers[0], wiredMice[0]))
        {
            return new NinjutsoSoraTransportPlan
            {
                SelectedEndpoints = WithWiredHistoryEvidence(
                    independent,
                    HistoryAnchorEvidence.RejectPersistedReceiver,
                    receivers[0]),
                Reason = "endpoint_serial_mismatch"
            };
        }

        if (!CanRepresentSameMouse(receivers[0], wiredMice[0]))
        {
            return new NinjutsoSoraTransportPlan
            {
                SelectedEndpoints = independent,
                Reason = "endpoint_identity_unresolved"
            };
        }

        var receiver = receivers[0];
        var wiredMouse = wiredMice[0];
        int? pairedProductId = null;
        if (pairedProductIds != null && pairedProductIds.TryGetValue(receiver.DeviceId, out var resolvedProductId))
            pairedProductId = resolvedProductId;

        if (pairedProductId != wiredMouse.ProductId)
        {
            var mismatch = pairedProductId is > 0;
            return new NinjutsoSoraTransportPlan
            {
                SelectedEndpoints = mismatch
                    ? WithWiredHistoryEvidence(
                        independent,
                        HistoryAnchorEvidence.RejectPersistedReceiver,
                        receiver)
                    : independent,
                Reason = mismatch
                    ? "receiver_pairing_mismatch"
                    : "receiver_pairing_unresolved"
            };
        }

        return new NinjutsoSoraTransportPlan
        {
            SelectedEndpoints = new[]
            {
                new NinjutsoSoraEndpointSelection(
                    wiredMouse,
                    receiver,
                    CreateReceiverLogicalId(receiver),
                    DeviceConnectionTransport.WiredUsb,
                    ResolvedIds(receiver, wiredMouse),
                    HistoryAnchorEvidence.ConfirmReceiver,
                    receiver)
            },
            SuppressedEndpoints = new[] { receiver },
            Reason = "receiver_reports_wired_mouse_pid"
        };
    }

    public static bool IsDirectMouseProductId(int productId) => DirectMouseProductIds.Contains(productId);

    public static bool IsReceiverProductId(int productId) => ReceiverProductIds.Contains(productId);

    public static DeviceConnectionTransport GetTransport(int productId)
    {
        return IsReceiverProductId(productId)
            ? DeviceConnectionTransport.Receiver
            : DirectMouseProductIds.Contains(productId)
                ? DeviceConnectionTransport.WiredUsb
                : DeviceConnectionTransport.Unknown;
    }

    public static NinjutsoSoraPowerFacts ResolvePowerFacts(
        int batteryPercentage,
        bool reportedCharging,
        DeviceConnectionTransport transport)
    {
        var externallyPowered = transport == DeviceConnectionTransport.WiredUsb || reportedCharging;
        var fullyCharged = externallyPowered && batteryPercentage >= 100;
        return new NinjutsoSoraPowerFacts(
            IsCharging: reportedCharging,
            IsFullyCharged: fullyCharged,
            IsCableConnected: externallyPowered,
            PowerState: fullyCharged
                ? DevicePowerState.FullyCharged
                : reportedCharging
                    ? DevicePowerState.Charging
                    : externallyPowered
                        ? DevicePowerState.PendingCharge
                        : DevicePowerState.Discharging,
            ExternalPowerConnected: externallyPowered);
    }

    private static bool CanRepresentSameMouse(NinjutsoSoraEndpoint receiver, NinjutsoSoraEndpoint wiredMouse)
    {
        var receiverSerial = DeviceIdentity.NormalizeSerial(receiver.DeviceSerial);
        var wiredSerial = DeviceIdentity.NormalizeSerial(wiredMouse.DeviceSerial);
        if (receiverSerial != null
            && wiredSerial != null
            && !string.Equals(receiverSerial, wiredSerial, StringComparison.OrdinalIgnoreCase))
            return false;

        return LooksLikeSora(receiver.DeviceName) || LooksLikeSora(wiredMouse.DeviceName);
    }

    private static bool HasConflictingStableSerials(
        NinjutsoSoraEndpoint receiver,
        NinjutsoSoraEndpoint wiredMouse)
    {
        var receiverSerial = DeviceIdentity.NormalizeSerial(receiver.DeviceSerial);
        var wiredSerial = DeviceIdentity.NormalizeSerial(wiredMouse.DeviceSerial);
        return receiverSerial != null
            && wiredSerial != null
            && !string.Equals(receiverSerial, wiredSerial, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<NinjutsoSoraEndpointSelection> WithWiredHistoryEvidence(
        IReadOnlyList<NinjutsoSoraEndpointSelection> selections,
        HistoryAnchorEvidence evidence,
        NinjutsoSoraEndpoint? associationReceiver = null)
    {
        return selections
            .Select(selection => selection.Transport == DeviceConnectionTransport.WiredUsb
                ? selection with
                {
                    HistoryAnchorEvidence = evidence,
                    AssociationReceiverEndpoint = associationReceiver
                }
                : selection)
            .ToArray();
    }

    private static bool LooksLikeSora(string? deviceName)
    {
        return !string.IsNullOrWhiteSpace(deviceName)
            && deviceName.Contains("Sora", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateEndpointLogicalId(NinjutsoSoraEndpoint endpoint)
    {
        return IsReceiverProductId(endpoint.ProductId)
            ? CreateReceiverLogicalId(endpoint)
            : $"ninjutso-sora-v2:wired:{EndpointAnchor(endpoint)}";
    }

    internal static string CreateReceiverLogicalId(NinjutsoSoraEndpoint receiver)
    {
        return $"ninjutso-sora-v2:receiver:{EndpointAnchor(receiver)}";
    }

    private static string EndpointAnchor(NinjutsoSoraEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.DeviceId))
            return endpoint.DeviceId.Trim();

        var serial = DeviceIdentity.NormalizeSerial(endpoint.DeviceSerial);
        return serial != null
            ? $"serial:{serial}"
            : $"pid:{endpoint.ProductId:X4}:name:{endpoint.DeviceName.Trim()}";
    }

    private static IReadOnlyList<string> ResolvedIds(params NinjutsoSoraEndpoint[] endpoints)
    {
        return endpoints
            .Select(endpoint => endpoint.DeviceId)
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
