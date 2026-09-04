namespace SoraV2BatteryTip;

internal sealed record DeviceRefreshEvent(string DevicePath, bool Arrived, bool WasTracked);

internal sealed record PollAttemptOutcome(
    bool PollCompleted,
    bool InventoryReliable,
    bool StateApplied,
    bool HasFreshSamples,
    bool CandidateCoverageComplete,
    IReadOnlyList<string> CandidateDevicePaths,
    IReadOnlyList<string> FreshDevicePaths,
    string FailureReason)
{
    public static PollAttemptOutcome NotStarted(string reason) => new(
        false,
        false,
        false,
        false,
        false,
        Array.Empty<string>(),
        Array.Empty<string>(),
        reason);
}

internal sealed record DeviceRefreshDecision(bool IsComplete, string Outcome, string Reason);

internal static class DeviceRefreshPolicy
{
    public static DeviceRefreshDecision Evaluate(
        IReadOnlyList<DeviceRefreshEvent> events,
        IReadOnlySet<string> baselineDevicePaths,
        IReadOnlySet<string> inventoryDevicePaths,
        bool inventoryReliable,
        PollAttemptOutcome poll,
        bool finalAttempt)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(baselineDevicePaths);
        ArgumentNullException.ThrowIfNull(inventoryDevicePaths);
        ArgumentNullException.ThrowIfNull(poll);

        if (!poll.PollCompleted)
            return finalAttempt
                ? new DeviceRefreshDecision(true, "failure", poll.FailureReason)
                : new DeviceRefreshDecision(false, "retry", poll.FailureReason);

        if (!inventoryReliable || !poll.InventoryReliable)
            return finalAttempt
                ? new DeviceRefreshDecision(true, "degraded", "inventory_unreliable")
                : new DeviceRefreshDecision(false, "retry", "inventory_unreliable");

        if (!poll.StateApplied)
            return finalAttempt
                ? new DeviceRefreshDecision(true, "degraded", "state_not_applied")
                : new DeviceRefreshDecision(false, "retry", "state_not_applied");

        if (events.Count == 0)
        {
            if (poll.HasFreshSamples && (poll.CandidateCoverageComplete || poll.CandidateDevicePaths.Count == 0))
                return new DeviceRefreshDecision(true, "success", "forced_fresh_state_applied");
            if (poll.CandidateDevicePaths.Count == 0
                && !string.Equals(poll.FailureReason, "read_failed", StringComparison.OrdinalIgnoreCase))
                return new DeviceRefreshDecision(true, "success", "forced_absence_applied");

            return finalAttempt
                ? new DeviceRefreshDecision(true, "degraded", "forced_read_incomplete")
                : new DeviceRefreshDecision(false, "retry", "forced_read_incomplete");
        }

        var removals = events.Where(item => !item.Arrived).ToArray();
        var arrivals = events.Where(item => item.Arrived).ToArray();
        var removalsResolved = removals.All(item => !inventoryDevicePaths.Contains(item.DevicePath));

        var candidatePaths = poll.CandidateDevicePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var freshPaths = poll.FreshDevicePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateArrivals = arrivals.Where(item => candidatePaths.Contains(item.DevicePath)).ToArray();
        var everyArrivalReadExactly = arrivals.Length > 0
            && arrivals.All(item => candidatePaths.Contains(item.DevicePath) && freshPaths.Contains(item.DevicePath));
        var newFreshPaths = freshPaths.Where(path => !baselineDevicePaths.Contains(path)).ToArray();
        var singleArrivalCompositeFallback = arrivals.Length == 1
            && newFreshPaths.Length == 1
            && candidatePaths.Contains(newFreshPaths[0]);
        var arrivalsResolvedByReading = everyArrivalReadExactly || singleArrivalCompositeFallback;

        if (removalsResolved && arrivals.Length == 0)
            return new DeviceRefreshDecision(true, "success", "removal_confirmed");
        if (removalsResolved && arrivalsResolvedByReading && poll.CandidateCoverageComplete)
        {
            return new DeviceRefreshDecision(
                true,
                "success",
                everyArrivalReadExactly ? "all_arrival_candidates_read" : "single_arrival_composite_read");
        }

        if (!finalAttempt)
        {
            var reason = !removalsResolved
                ? "removal_still_present"
                : arrivalsResolvedByReading
                    ? "candidate_coverage_incomplete"
                    : "arrival_not_resolved";
            return new DeviceRefreshDecision(false, "retry", reason);
        }

        if (!removalsResolved)
            return new DeviceRefreshDecision(true, "degraded", "removal_still_present_after_settling");
        if (candidateArrivals.Any(item => !freshPaths.Contains(item.DevicePath)))
            return new DeviceRefreshDecision(true, "degraded", "arrival_battery_read_unavailable");
        if (arrivalsResolvedByReading && !poll.CandidateCoverageComplete)
            return new DeviceRefreshDecision(true, "degraded", "candidate_coverage_incomplete");

        var everyArrivalPresent = arrivals.All(item => inventoryDevicePaths.Contains(item.DevicePath));
        if (!everyArrivalPresent && !singleArrivalCompositeFallback)
            return new DeviceRefreshDecision(true, "degraded", "arrival_not_present_after_settling");
        if (candidateArrivals.Length > 0)
            return new DeviceRefreshDecision(true, "degraded", "arrival_batch_incomplete");
        return new DeviceRefreshDecision(true, "skipped", "non_battery_hid_settled");
    }
}
