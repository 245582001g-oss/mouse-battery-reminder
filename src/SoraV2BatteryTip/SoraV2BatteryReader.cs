namespace SoraV2BatteryTip;

internal interface IBatteryProvider
{
    string Name { get; }
    int Priority { get; }
    bool IsAvailable();
    Task<BatteryReading?> ReadAsync(CancellationToken token);
}

internal interface IMultipleBatteryProvider
{
    Task<IReadOnlyList<BatteryReading>> ReadAllAsync(CancellationToken token);
}

internal sealed class BatteryProviderManager
{
    private readonly IReadOnlyList<IBatteryProvider> _providers;

    public BatteryProviderManager(IEnumerable<IBatteryProvider> providers)
    {
        _providers = providers.OrderByDescending(provider => provider.Priority).ToArray();
    }

    public IReadOnlyList<ProviderStatus> GetProviderStatus()
    {
        var result = new List<ProviderStatus>();
        foreach (var provider in _providers)
        {
            try
            {
                result.Add(new ProviderStatus
                {
                    Name = provider.Name,
                    Priority = provider.Priority,
                    IsAvailable = provider.IsAvailable()
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

    public async Task<BatteryReadResult> ReadAsync(CancellationToken token)
    {
        var result = await ReadAllAsync(token).ConfigureAwait(false);
        return new BatteryReadResult
        {
            Reading = result.Readings.FirstOrDefault(),
            Source = result.Source,
            FailureReason = result.FailureReason
        };
    }

    public async Task<BatteryReadAllResult> ReadAllAsync(CancellationToken token)
    {
        var availableProviderFound = false;
        var failedProviderNames = new List<string>();
        var readings = new List<BatteryReading>();

        foreach (var provider in _providers)
        {
            token.ThrowIfCancellationRequested();
            if (!provider.IsAvailable())
                continue;

            availableProviderFound = true;
            try
            {
                var providerReadings = await ReadProviderAllAsync(provider, token).ConfigureAwait(false);
                if (providerReadings.Count == 0)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    providerReadings = await ReadProviderAllAsync(provider, token).ConfigureAwait(false);
                }

                foreach (var reading in providerReadings)
                {
                    if (!IsDuplicateReading(readings, reading))
                        readings.Add(reading);
                }

                if (providerReadings.Count == 0)
                    failedProviderNames.Add(provider.Name);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failedProviderNames.Add(provider.Name);
            }
        }

        if (readings.Count > 0)
        {
            return new BatteryReadAllResult
            {
                Readings = readings,
                Source = string.Join(", ", readings.Select(reading => reading.Source).Distinct(StringComparer.OrdinalIgnoreCase)),
                FailureReason = ""
            };
        }

        return new BatteryReadAllResult
        {
            Source = failedProviderNames.Count > 0 ? string.Join(", ", failedProviderNames) : "none",
            FailureReason = availableProviderFound ? "read_failed" : "not_detected"
        };
    }

    private static async Task<IReadOnlyList<BatteryReading>> ReadProviderAllAsync(IBatteryProvider provider, CancellationToken token)
    {
        if (provider is IMultipleBatteryProvider multipleProvider)
            return await multipleProvider.ReadAllAsync(token).ConfigureAwait(false);

        var reading = await provider.ReadAsync(token).ConfigureAwait(false);
        return reading == null ? Array.Empty<BatteryReading>() : new[] { reading };
    }

    private static bool IsDuplicateReading(List<BatteryReading> existingReadings, BatteryReading candidate)
    {
        foreach (var existing in existingReadings)
        {
            if (!string.IsNullOrWhiteSpace(candidate.DeviceId)
                && !string.IsNullOrWhiteSpace(existing.DeviceId)
                && string.Equals(candidate.DeviceId, existing.DeviceId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.Equals(candidate.Source, existing.Source, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.VendorId)
                && !string.IsNullOrWhiteSpace(candidate.ProductId)
                && string.Equals(candidate.VendorId, existing.VendorId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ProductId, existing.ProductId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
