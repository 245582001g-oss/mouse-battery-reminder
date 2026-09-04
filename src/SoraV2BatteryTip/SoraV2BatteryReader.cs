namespace SoraV2BatteryTip;

internal interface IBatteryProvider
{
    string Name { get; }
    int Priority { get; }
    bool IsAvailable(HidInventorySnapshot inventory);
    Task<ProviderReadResult> ReadAsync(HidInventorySnapshot inventory, CancellationToken token);
}

internal sealed class BatteryProviderManager
{
    private readonly HidDeviceInventory _inventory;
    private readonly IReadOnlyList<IBatteryProvider> _providers;

    public BatteryProviderManager(HidDeviceInventory inventory, IEnumerable<IBatteryProvider> providers)
    {
        _inventory = inventory;
        _providers = providers.OrderByDescending(provider => provider.Priority).ToArray();
    }

    public IReadOnlyList<ProviderStatus> GetProviderStatus()
    {
        var inventory = _inventory.GetSnapshot();
        var result = new List<ProviderStatus>();
        foreach (var provider in _providers)
        {
            try
            {
                result.Add(new ProviderStatus
                {
                    Name = provider.Name,
                    Priority = provider.Priority,
                    IsAvailable = inventory.IsReliable && provider.IsAvailable(inventory)
                });
            }
            catch (Exception ex)
            {
                result.Add(new ProviderStatus
                {
                    Name = provider.Name,
                    Priority = provider.Priority,
                    IsAvailable = false,
                    Error = ex.GetType().Name
                });
            }
        }

        return result;
    }

    public async Task<BatteryReadAllResult> ReadAllAsync(CancellationToken token)
    {
        var inventory = _inventory.GetSnapshot();
        var batches = await Task.WhenAll(_providers.Select(provider => ReadProviderSafelyAsync(provider, inventory, token))).ConfigureAwait(false);
        var readings = new List<BatteryReading>();
        var candidateFound = false;
        var candidateProviders = new List<string>();
        var providerResults = new List<ProviderBatchResult>();

        for (var i = 0; i < _providers.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var batch = batches[i];
            providerResults.Add(new ProviderBatchResult
            {
                ProviderName = _providers[i].Name,
                Priority = _providers[i].Priority,
                Readings = batch.Readings,
                CandidateFound = batch.CandidateFound,
                CandidateDeviceIds = batch.CandidateDeviceIds,
                Error = batch.Error
            });
            candidateFound |= batch.CandidateFound;
            if (batch.CandidateFound)
                candidateProviders.Add(_providers[i].Name);
            foreach (var reading in batch.Readings)
            {
                if (!IsDuplicateReading(readings, reading))
                    readings.Add(reading);
            }
        }

        if (readings.Count > 0)
        {
            return new BatteryReadAllResult
            {
                Readings = readings,
                Source = string.Join(", ", readings.Select(reading => reading.Source).Distinct(StringComparer.OrdinalIgnoreCase)),
                FailureReason = "",
                HasCandidate = true,
                InventoryReliable = inventory.IsReliable,
                ProviderResults = providerResults
            };
        }

        return new BatteryReadAllResult
        {
            Source = candidateFound ? string.Join(", ", candidateProviders.Distinct(StringComparer.OrdinalIgnoreCase)) : "none",
            FailureReason = !inventory.IsReliable || candidateFound ? "read_failed" : "not_detected",
            HasCandidate = candidateFound,
            InventoryReliable = inventory.IsReliable,
            ProviderResults = providerResults
        };
    }

    private static async Task<ProviderReadResult> ReadProviderSafelyAsync(IBatteryProvider provider, HidInventorySnapshot inventory, CancellationToken token)
    {
        try
        {
            return await provider.ReadAsync(inventory, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderReadResult { Error = ex.GetType().Name };
        }
    }

    private static bool IsDuplicateReading(List<BatteryReading> existingReadings, BatteryReading candidate)
    {
        foreach (var existing in existingReadings)
        {
            if (!string.IsNullOrWhiteSpace(candidate.DeviceId)
                && !string.IsNullOrWhiteSpace(existing.DeviceId)
                && string.Equals(candidate.DeviceId, existing.DeviceId, StringComparison.OrdinalIgnoreCase))
                return true;

        }

        return false;
    }
}
