namespace SoraV2BatteryTip;

internal static class DeviceIdentity
{
    public static string? NormalizeSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        var normalized = serial.Trim().ToUpperInvariant();
        return normalized.All(character => character == '0' || character == '-' || char.IsWhiteSpace(character))
            ? null
            : normalized;
    }

    public static string CreateRuntimeKey(BatteryReading reading)
    {
        if (!string.IsNullOrWhiteSpace(reading.LogicalDeviceId))
        {
            var logicalDeviceId = reading.LogicalDeviceId.Trim();
            var receiverEpochSerial = NormalizeSerial(reading.AssociationReceiverSerial)
                ?? (logicalDeviceId.StartsWith(
                        "ninjutso-sora-v2:receiver:",
                        StringComparison.OrdinalIgnoreCase)
                    ? NormalizeSerial(reading.DeviceSerial)
                    : null);
            return receiverEpochSerial == null
                ? $"LOGICAL:{logicalDeviceId}"
                : $"LOGICAL:{logicalDeviceId}:RECEIVER-SERIAL:{receiverEpochSerial}";
        }

        var serial = NormalizeSerial(reading.DeviceSerial);
        if (serial != null)
            return $"{reading.VendorId}:{reading.ProductId}:SERIAL:{serial}";
        if (!string.IsNullOrWhiteSpace(reading.DeviceId))
            return reading.DeviceId;

        return $"{reading.VendorId}:{reading.ProductId}:{reading.Source}:{reading.DeviceName}";
    }

    public static bool PreviousReadingsCoveredBy(
        IReadOnlyList<BatteryReading> previousReadings,
        IReadOnlyList<BatteryReading> currentReadings)
    {
        if (previousReadings.Count == 0)
            return currentReadings.Count > 0;

        var unmatchedPrevious = Enumerable.Range(0, previousReadings.Count).ToList();
        var unmatchedCurrent = Enumerable.Range(0, currentReadings.Count).ToList();

        MatchDistinct(
            unmatchedPrevious,
            unmatchedCurrent,
            previousReadings,
            currentReadings,
            static (previous, current) => !string.IsNullOrWhiteSpace(previous.LogicalDeviceId)
                && !string.IsNullOrWhiteSpace(current.LogicalDeviceId)
                && string.Equals(previous.LogicalDeviceId.Trim(), current.LogicalDeviceId.Trim(), StringComparison.OrdinalIgnoreCase));

        MatchDistinct(
            unmatchedPrevious,
            unmatchedCurrent,
            previousReadings,
            currentReadings,
            static (previous, current) => !string.IsNullOrWhiteSpace(previous.DeviceId)
                && string.Equals(previous.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase));

        MatchDistinct(
            unmatchedPrevious,
            unmatchedCurrent,
            previousReadings,
            currentReadings,
            static (previous, current) =>
            {
                var previousSerial = NormalizeSerial(previous.DeviceSerial);
                var currentSerial = NormalizeSerial(current.DeviceSerial);
                return previousSerial != null
                    && currentSerial != null
                    && SameProviderFamily(previous, current)
                    && string.Equals(previousSerial, currentSerial, StringComparison.OrdinalIgnoreCase);
            });

        MatchDistinct(
            unmatchedPrevious,
            unmatchedCurrent,
            previousReadings,
            currentReadings,
            static (previous, current) => NormalizeSerial(previous.DeviceSerial) == null
                && NormalizeSerial(current.DeviceSerial) == null
                && SameProviderFamily(previous, current)
                && string.Equals(previous.ProductId, current.ProductId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(previous.DeviceName)
                && string.Equals(previous.DeviceName.Trim(), current.DeviceName.Trim(), StringComparison.OrdinalIgnoreCase));

        return unmatchedPrevious.Count == 0;
    }

    public static bool CandidatePathsCoveredBy(IReadOnlyList<ProviderBatchResult> providerResults)
    {
        var candidatePaths = providerResults
            .SelectMany(result => result.CandidateDeviceIds)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidatePaths.Count == 0)
            return providerResults.Any(result => result.Readings.Count > 0);

        var returnedPaths = providerResults
            .SelectMany(result => result.Readings)
            .SelectMany(ResolvedDeviceIds)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidatePaths.IsSubsetOf(returnedPaths);
    }

    public static IReadOnlyList<string> ResolvedDeviceIds(BatteryReading reading)
    {
        return reading.ResolvedDeviceIds
            .Append(reading.DeviceId)
            .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void MatchDistinct(
        List<int> unmatchedPrevious,
        List<int> unmatchedCurrent,
        IReadOnlyList<BatteryReading> previousReadings,
        IReadOnlyList<BatteryReading> currentReadings,
        Func<BatteryReading, BatteryReading, bool> matches)
    {
        foreach (var previousIndex in unmatchedPrevious.ToArray())
        {
            var currentIndex = unmatchedCurrent.FirstOrDefault(
                candidateIndex => matches(previousReadings[previousIndex], currentReadings[candidateIndex]),
                -1);
            if (currentIndex < 0)
                continue;

            unmatchedPrevious.Remove(previousIndex);
            unmatchedCurrent.Remove(currentIndex);
        }
    }

    private static bool SameProviderFamily(BatteryReading first, BatteryReading second)
    {
        return string.Equals(first.VendorId, second.VendorId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Source, second.Source, StringComparison.OrdinalIgnoreCase);
    }
}
