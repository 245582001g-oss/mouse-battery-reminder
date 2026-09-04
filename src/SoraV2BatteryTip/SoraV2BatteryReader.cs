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
    private readonly IAppEventLog? _log;

    public BatteryProviderManager(HidDeviceInventory inventory, IEnumerable<IBatteryProvider> providers, IAppEventLog? log = null)
    {
        _inventory = inventory;
        _providers = providers.OrderByDescending(provider => provider.Priority).ToArray();
        _log = log;
        _log?.Write("info", "providers.configured", nameof(BatteryProviderManager), "success", new
        {
            providers = _providers.Select(provider => new { provider.Name, provider.Priority }).ToArray()
        });
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
                _log?.Write("warn", "provider.status_failed", nameof(BatteryProviderManager), "failure", new
                {
                    provider = provider.Name,
                    provider.Priority
                }, ex);
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
        var started = System.Diagnostics.Stopwatch.StartNew();
        var inventory = _inventory.GetSnapshot();
        if (!inventory.IsReliable)
        {
            _log?.Write("warn", "hid.inventory.retry_scheduled", nameof(BatteryProviderManager), "degraded", new
            {
                delay_ms = 250,
                generation = inventory.Generation
            });
            await Task.Delay(250, token).ConfigureAwait(false);
            _inventory.Invalidate("read_retry_after_inventory_failure");
            inventory = _inventory.GetSnapshot();
        }

        _log?.Write("debug", "providers.read_started", nameof(BatteryProviderManager), "success", new
        {
            provider_count = _providers.Count,
            inventory_generation = inventory.Generation,
            inventory_reliable = inventory.IsReliable,
            hid_device_count = inventory.Devices.Count
        });
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
                IdentityObservations = batch.IdentityObservations,
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
                {
                    readings.Add(reading);
                    _log?.Write("debug", "provider.reading_accepted", nameof(BatteryProviderManager), "success", new
                    {
                        provider = _providers[i].Name,
                        battery_percentage = reading.BatteryPercentage,
                        charging = reading.IsCharging,
                        cable_connected = reading.IsCableConnected
                    }, reading: reading);
                }
                else
                {
                    _log?.Write("debug", "provider.reading_deduplicated", nameof(BatteryProviderManager), "skipped", new
                    {
                        provider = _providers[i].Name
                    }, reading: reading);
                }
            }
        }

        if (readings.Count > 0)
        {
            _log?.Write("debug", "providers.read_completed", nameof(BatteryProviderManager), "success", new
            {
                duration_ms = started.ElapsedMilliseconds,
                reading_count = readings.Count,
                identity_observation_count = providerResults.Sum(batch => batch.IdentityObservations.Count),
                candidate_provider_count = candidateProviders.Count,
                inventory_reliable = inventory.IsReliable
            });
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

        var failureReason = !inventory.IsReliable || candidateFound ? "read_failed" : "not_detected";
        _log?.Write(failureReason == "read_failed" ? "warn" : "debug", "providers.read_completed", nameof(BatteryProviderManager), "failure", new
        {
            duration_ms = started.ElapsedMilliseconds,
            reading_count = 0,
            identity_observation_count = providerResults.Sum(batch => batch.IdentityObservations.Count),
            candidate_provider_count = candidateProviders.Count,
            inventory_reliable = inventory.IsReliable,
            failure_reason = failureReason
        });
        return new BatteryReadAllResult
        {
            Source = candidateFound ? string.Join(", ", candidateProviders.Distinct(StringComparer.OrdinalIgnoreCase)) : "none",
            FailureReason = failureReason,
            HasCandidate = candidateFound,
            InventoryReliable = inventory.IsReliable,
            ProviderResults = providerResults
        };
    }

    private async Task<ProviderReadResult> ReadProviderSafelyAsync(IBatteryProvider provider, HidInventorySnapshot inventory, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        _log?.Write("debug", "provider.read_started", provider.Name, "success", new { provider.Priority });
        try
        {
            var result = await provider.ReadAsync(inventory, token).ConfigureAwait(false);
            _log?.Write(
                string.IsNullOrWhiteSpace(result.Error) ? "debug" : "warn",
                "provider.read_completed",
                provider.Name,
                string.IsNullOrWhiteSpace(result.Error) ? "success" : "failure",
                new
                {
                    duration_ms = started.ElapsedMilliseconds,
                    candidate_found = result.CandidateFound,
                    candidate_count = result.CandidateDeviceIds.Count,
                    candidate_tokens = result.CandidateDeviceIds.Select(value => _log?.TokenFor(value, "dev") ?? "dev_redacted").ToArray(),
                    candidate_device_ids = result.CandidateDeviceIds,
                    reading_count = result.Readings.Count,
                    identity_observation_count = result.IdentityObservations.Count,
                    error_code = result.Error
                });
            return result;
        }
        catch (OperationCanceledException)
        {
            _log?.Write("debug", "provider.read_cancelled", provider.Name, "cancelled", new { duration_ms = started.ElapsedMilliseconds });
            throw;
        }
        catch (Exception ex)
        {
            _log?.Write("error", "provider.read_failed", provider.Name, "failure", new { duration_ms = started.ElapsedMilliseconds }, ex);
            return new ProviderReadResult { Error = ex.GetType().Name };
        }
    }

    private static bool IsDuplicateReading(List<BatteryReading> existingReadings, BatteryReading candidate)
    {
        foreach (var existing in existingReadings)
        {
            if (!string.IsNullOrWhiteSpace(candidate.LogicalDeviceId)
                && !string.IsNullOrWhiteSpace(existing.LogicalDeviceId)
                && string.Equals(candidate.LogicalDeviceId, existing.LogicalDeviceId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(candidate.DeviceId)
                && !string.IsNullOrWhiteSpace(existing.DeviceId)
                && string.Equals(candidate.DeviceId, existing.DeviceId, StringComparison.OrdinalIgnoreCase))
                return true;

        }

        return false;
    }
}
