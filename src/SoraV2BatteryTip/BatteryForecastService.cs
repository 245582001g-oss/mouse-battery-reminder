namespace SoraV2BatteryTip;

internal enum BatteryForecastConfidence
{
    None,
    Low,
    Medium,
    High
}

internal sealed class BatteryForecastResult
{
    public bool IsAvailable { get; init; }
    public double TypicalDrainPerHour { get; init; }
    public double MinimumRemainingDays { get; init; }
    public double MaximumRemainingDays { get; init; }
    public int ConsumedPercentage { get; init; }
    public double ValidHours { get; init; }
    public int SegmentCount { get; init; }
    public BatteryForecastConfidence Confidence { get; init; }
}

internal static class BatteryForecastService
{
    public static BatteryForecastResult Analyze(IReadOnlyList<BatteryHistoryEntry> source, int currentBatteryPercentage)
    {
        if (source.Count < 2)
            return new BatteryForecastResult();

        var entries = source.OrderBy(entry => entry.TimestampUtc).ToArray();
        var maximumGap = GetMaximumGap(entries);
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
                    segments.Add(new DischargeSegment(hours, consumed, rate, segmentEndTime.Value));
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
                && !IsBoundary(previous, current)
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
        var confidence = DetermineConfidence(segments.Count, consumedPercentage, validHours);
        if (confidence == BatteryForecastConfidence.None)
        {
            return new BatteryForecastResult
            {
                ConsumedPercentage = consumedPercentage,
                ValidHours = validHours,
                SegmentCount = segments.Count,
                Confidence = confidence
            };
        }

        var weighted = segments.Select(segment => new WeightedRate(
            segment.RatePerHour,
            segment.Hours * Math.Pow(0.5, Math.Max(0, (DateTime.UtcNow - segment.EndUtc).TotalDays) / 7d)))
            .Where(item => item.Weight > 0)
            .ToArray();
        var typicalRate = WeightedQuantile(weighted, 0.5);
        var lowerRate = WeightedQuantile(weighted, 0.25);
        var upperRate = WeightedQuantile(weighted, 0.75);
        var canEstimate = currentBatteryPercentage is >= 1 and <= 100
            && typicalRate > 0.01
            && lowerRate > 0.01
            && upperRate > 0.01;

        return new BatteryForecastResult
        {
            IsAvailable = canEstimate,
            TypicalDrainPerHour = typicalRate,
            MinimumRemainingDays = canEstimate ? currentBatteryPercentage / upperRate / 24d : 0,
            MaximumRemainingDays = canEstimate ? currentBatteryPercentage / lowerRate / 24d : 0,
            ConsumedPercentage = consumedPercentage,
            ValidHours = validHours,
            SegmentCount = segments.Count,
            Confidence = confidence
        };
    }

    public static TimeSpan GetMaximumGap(IReadOnlyList<BatteryHistoryEntry> entries)
    {
        var intervals = new List<double>();
        for (var i = 1; i < entries.Count; i++)
        {
            var previous = entries[i - 1];
            var current = entries[i];
            var minutes = (current.TimestampUtc - previous.TimestampUtc).TotalMinutes;
            if (minutes is > 0 and <= 180
                && !IsCharging(previous)
                && !IsCharging(current)
                && !IsBoundary(previous, current))
                intervals.Add(minutes);
        }

        if (intervals.Count == 0)
            return TimeSpan.FromMinutes(90);

        intervals.Sort();
        var middle = intervals.Count / 2;
        var median = intervals.Count % 2 == 0
            ? (intervals[middle - 1] + intervals[middle]) / 2
            : intervals[middle];
        return TimeSpan.FromMinutes(Math.Clamp(median * 3, 30, 180));
    }

    private static BatteryForecastConfidence DetermineConfidence(int segmentCount, int consumedPercentage, double validHours)
    {
        if (segmentCount >= 3 && consumedPercentage >= 20 && validHours >= 48)
            return BatteryForecastConfidence.High;
        if (segmentCount >= 2 && consumedPercentage >= 10 && validHours >= 24)
            return BatteryForecastConfidence.Medium;
        if (segmentCount >= 1 && consumedPercentage >= 3 && validHours >= 6)
            return BatteryForecastConfidence.Low;
        return BatteryForecastConfidence.None;
    }

    private static double WeightedQuantile(IReadOnlyList<WeightedRate> source, double quantile)
    {
        if (source.Count == 0)
            return 0;

        var ordered = source.OrderBy(item => item.Rate).ToArray();
        var target = ordered.Sum(item => item.Weight) * Math.Clamp(quantile, 0, 1);
        var cumulative = 0d;
        foreach (var item in ordered)
        {
            cumulative += item.Weight;
            if (cumulative >= target)
                return item.Rate;
        }
        return ordered[^1].Rate;
    }

    private static bool IsCharging(BatteryHistoryEntry entry) => entry.IsCharging || entry.IsCableConnected;

    private static bool IsBoundary(BatteryHistoryEntry previous, BatteryHistoryEntry current)
    {
        return previous.State == "device_offline"
            || current.State is "device_offline" or "device_online";
    }

    private readonly record struct DischargeSegment(double Hours, int ConsumedPercentage, double RatePerHour, DateTime EndUtc);
    private readonly record struct WeightedRate(double Rate, double Weight);
}
