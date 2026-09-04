using System.Text.Json;
using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class KnownDeviceProfileProvider : IBatteryProvider
{
    private readonly AppPaths _paths;
    private readonly HidDeviceInventory _inventory;
    private readonly IAppEventLog? _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public KnownDeviceProfileProvider(AppPaths paths, HidDeviceInventory inventory, IAppEventLog? log = null)
    {
        _paths = paths;
        _inventory = inventory;
        _log = log;
    }

    public string Name => "Known device profile";
    public int Priority => 100;

    public bool IsAvailable(HidInventorySnapshot inventory) => LoadProfiles().Any(profile => EnumerateMatchingDevices(profile, inventory).Any());

    public Task<ProviderReadResult> ReadAsync(HidInventorySnapshot inventory, CancellationToken token)
    {
        return Task.Run(() => ReadAllOnce(inventory, token), token);
    }

    public void ReloadProfiles()
    {
        _paths.Ensure();
        _log?.Write("info", "profile.reload", nameof(KnownDeviceProfileProvider), "success");
    }

    public BatteryReading? TryReadProfileFile(string file)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
            if (profile == null || !profile.Enabled || !ValidateProfile(profile, out _) || IsHandledByBuiltInProvider(profile))
                return null;

            var inventory = _inventory.GetSnapshot();
            foreach (var device in EnumerateMatchingDevices(profile, inventory))
            {
                var reading = ReadProfileDevice(profile, device, inventory);
                if (reading != null)
                    return reading;
            }
        }
        catch (Exception ex)
        {
            _log?.Write("warn", "profile.read_file_failed", nameof(KnownDeviceProfileProvider), "failure", new
            {
                file_name = Path.GetFileName(file)
            }, ex);
        }

        return null;
    }

    public IReadOnlyList<ProfileValidationStatus> GetProfileStatus()
    {
        try
        {
            _paths.Ensure();
            if (!Directory.Exists(_paths.ProfilesDirectory))
                return Array.Empty<ProfileValidationStatus>();

            return Directory.GetFiles(_paths.ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(ReadProfileStatus)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log?.Write("warn", "profile.status_failed", nameof(KnownDeviceProfileProvider), "failure", exception: ex);
            return new[]
            {
                new ProfileValidationStatus
                {
                    FileName = _paths.ProfilesDirectory,
                    IsValid = false,
                    Error = ex.GetType().Name
                }
            };
        }
    }

    private ProviderReadResult ReadAllOnce(HidInventorySnapshot inventory, CancellationToken token)
    {
        var readings = new List<BatteryReading>();
        var candidateFound = false;
        var candidateDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in LoadProfiles().OrderByDescending(profile => profile.Priority))
        {
            token.ThrowIfCancellationRequested();
            var devices = EnumerateMatchingDevices(profile, inventory).ToArray();
            candidateFound |= devices.Length > 0;
            foreach (var device in devices)
            {
                var path = Safe(() => device.DevicePath);
                if (!string.IsNullOrWhiteSpace(path))
                    candidateDeviceIds.Add(path);
            }

            foreach (var device in devices)
            {
                token.ThrowIfCancellationRequested();
                var reading = ReadProfileDevice(profile, device, inventory);
                if (reading != null && !readings.Any(existing => string.Equals(existing.DeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    readings.Add(reading);
            }
        }

        return new ProviderReadResult
        {
            Readings = readings,
            CandidateFound = candidateFound,
            CandidateDeviceIds = candidateDeviceIds.ToArray()
        };
    }

    private BatteryReading? ReadProfileDevice(DeviceProfile profile, HidDevice device, HidInventorySnapshot inventory)
    {
        if (!profile.ReportType.Equals("Feature", StringComparison.OrdinalIgnoreCase) || profile.BatteryOffset == null)
            return null;
        if (!device.TryOpen(out var stream) || stream == null)
        {
            _log?.Write("warn", "provider.device_open_failed", Name, "failure", new { profile = profile.Name }, reading: DeviceForLog(device, profile));
            return null;
        }

        using (stream)
        {
            try
            {
                stream.ReadTimeout = 450;
                stream.WriteTimeout = 450;

                var reportId = ParseByte(profile.ReportId) ?? 0;
                var requestBytes = ParseBytes(profile.RequestBytes);
                var length = Math.Max(Math.Max(2, profile.MinFeatureLength), SafeInt(device.GetMaxFeatureReportLength));
                var request = new byte[length];
                request[0] = reportId;
                for (var i = 0; i < requestBytes.Length && i < request.Length; i++)
                    request[i] = requestBytes[i];

                if (profile.SendRequest)
                {
                    stream.SetFeature(request, 0, request.Length);
                    Thread.Sleep(Math.Clamp(profile.DelayMs, 0, 250));
                }

                var response = new byte[length];
                response[0] = ParseByte(profile.ResponseReportId) ?? request[0];
                stream.GetFeature(response, 0, response.Length);
                var reading = ParseReading(profile, response, device, inventory);
                if (reading == null)
                {
                    _log?.Write("warn", "provider.parse_rejected", Name, "failure", new
                    {
                        profile = profile.Name,
                        reason = "profile_offsets_or_flags_invalid",
                        report_length = response.Length
                    }, reading: DeviceForLog(device, profile));
                }
                return reading;
            }
            catch (Exception ex)
            {
                _log?.Write("warn", "provider.io_failed", Name, "failure", new
                {
                    profile = profile.Name,
                    operation = "feature_query"
                }, ex, DeviceForLog(device, profile));
                return null;
            }
        }
    }

    private BatteryReading? ParseReading(DeviceProfile profile, byte[] response, HidDevice device, HidInventorySnapshot inventory)
    {
        var starts = profile.PayloadStarts.Length == 0 ? new[] { 0 } : profile.PayloadStarts;
        foreach (var start in starts)
        {
            var battery = ReadByte(response, start, profile.BatteryOffset);
            if (battery is null or < 1 or > 100)
                continue;

            var charging = ReadFlag(response, start, profile.ChargingOffset, defaultValue: false);
            var full = ReadFlag(response, start, profile.FullOffset, defaultValue: false);
            var online = ReadFlag(response, start, profile.OnlineOffset, defaultValue: true);
            if (charging == null || full == null || online == null)
                continue;

            var cableConnected = charging.Value || full.Value || IsCableDevicePresent(profile, device, inventory);
            return new BatteryReading
            {
                BatteryPercentage = battery.Value,
                IsCharging = charging.Value || full.Value,
                IsFullyCharged = full.Value,
                IsOnline = online.Value,
                IsCableConnected = cableConnected,
                PowerState = !online.Value
                    ? DevicePowerState.Offline
                    : full.Value
                        ? DevicePowerState.FullyCharged
                        : charging.Value
                            ? DevicePowerState.Charging
                            : cableConnected
                                ? DevicePowerState.PendingCharge
                                : DevicePowerState.Discharging,
                ExternalPowerConnected = cableConnected,
                DeviceName = Safe(() => device.GetProductName()),
                DeviceId = Safe(() => device.DevicePath),
                DeviceSerial = Safe(() => device.GetSerialNumber()),
                VendorId = $"0x{device.VendorID:X4}",
                ProductId = $"0x{device.ProductID:X4}",
                Source = string.IsNullOrWhiteSpace(profile.Name) ? Name : profile.Name
            };
        }

        return null;
    }

    private static bool IsCableDevicePresent(DeviceProfile profile, HidDevice currentDevice, HidInventorySnapshot inventory)
    {
        if (profile.CableProductIds.Length == 0)
            return false;
        if (profile.CableProductIds.Any(value => ParseInt(value) == currentDevice.ProductID))
            return true;

        var vendorId = ParseInt(profile.VendorId);
        if (vendorId == null)
            return false;

        foreach (var productText in profile.CableProductIds)
        {
            var productId = ParseInt(productText);
            if (productId != null && inventory.Find(vendorId.Value, productId.Value).Any())
                return true;
        }

        return false;
    }

    private IEnumerable<HidDevice> EnumerateMatchingDevices(DeviceProfile profile, HidInventorySnapshot inventory)
    {
        var vendorId = ParseInt(profile.VendorId);
        if (vendorId == null)
            yield break;

        foreach (var productText in profile.ProductIds)
        {
            var productId = ParseInt(productText);
            if (productId == null)
                continue;

            foreach (var device in inventory.Find(vendorId.Value, productId.Value))
            {
                if (SafeInt(device.GetMaxFeatureReportLength) <= 1)
                    continue;
                if (!ProductNameMatches(device, profile))
                    continue;
                yield return device;
            }
        }
    }

    private IReadOnlyList<DeviceProfile> LoadProfiles()
    {
        try
        {
            _paths.Ensure();
            if (!Directory.Exists(_paths.ProfilesDirectory))
                return Array.Empty<DeviceProfile>();

            var profiles = new List<DeviceProfile>();
            foreach (var file in Directory.GetFiles(_paths.ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
                    if (profile != null && profile.Enabled && ValidateProfile(profile, out _) && !IsHandledByBuiltInProvider(profile))
                        profiles.Add(profile);
                }
                catch (Exception ex)
                {
                    _log?.Write("warn", "profile.validation_failed", nameof(KnownDeviceProfileProvider), "failure", new
                    {
                        file_name = Path.GetFileName(file)
                    }, ex);
                }
            }
            return profiles;
        }
        catch (Exception ex)
        {
            _log?.Write("warn", "profile.load_failed", nameof(KnownDeviceProfileProvider), "failure", exception: ex);
            return Array.Empty<DeviceProfile>();
        }
    }

    private static ProfileValidationStatus ReadProfileStatus(string file)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
            if (profile == null)
                return new ProfileValidationStatus { FileName = Path.GetFileName(file), IsValid = false, Error = "empty_or_invalid_json" };

            var valid = ValidateProfile(profile, out var error);
            if (valid && IsHandledByBuiltInProvider(profile))
            {
                valid = false;
                error = "handled_by_builtin_provider";
            }

            return new ProfileValidationStatus
            {
                FileName = Path.GetFileName(file),
                Name = profile.Name,
                IsEnabled = profile.Enabled,
                IsValid = valid,
                Error = error
            };
        }
        catch (Exception ex)
        {
            return new ProfileValidationStatus { FileName = Path.GetFileName(file), IsValid = false, Error = ex.GetType().Name };
        }
    }

    private static bool ValidateProfile(DeviceProfile profile, out string error)
    {
        if (ParseInt(profile.VendorId) == null || profile.ProductIds.Length == 0 || profile.ProductIds.Any(value => ParseInt(value) == null))
        {
            error = "invalid_device_ids";
            return false;
        }
        if (ParseByte(profile.ReportId) == null || (!string.IsNullOrWhiteSpace(profile.ResponseReportId) && ParseByte(profile.ResponseReportId) == null))
        {
            error = "invalid_report_id";
            return false;
        }
        if (profile.RequestBytes.Any(value => ParseByte(value) == null))
        {
            error = "invalid_request_bytes";
            return false;
        }
        if (!profile.ReportType.Equals("Feature", StringComparison.OrdinalIgnoreCase) || profile.BatteryOffset == null || profile.BatteryOffset < 0)
        {
            error = "unsupported_profile";
            return false;
        }
        if (profile.MinFeatureLength > 0 && profile.BatteryOffset >= profile.MinFeatureLength)
        {
            error = "battery_offset_out_of_range";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsHandledByBuiltInProvider(DeviceProfile profile)
    {
        return ParseInt(profile.VendorId) == 0x373B
            || NinjutsoSoraOfficialProvider.HandlesProfile(profile.VendorId, profile.ProductIds);
    }

    private static bool ProductNameMatches(HidDevice device, DeviceProfile profile)
    {
        if (profile.ProductNameContains.Length == 0)
            return true;

        var name = Safe(() => device.GetProductName());
        return profile.ProductNameContains.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static int? ReadByte(byte[] bytes, int start, int? offset)
    {
        if (offset == null)
            return null;
        var index = start + offset.Value;
        return index >= 0 && index < bytes.Length ? bytes[index] : null;
    }

    private static bool? ReadFlag(byte[] bytes, int start, int? offset, bool defaultValue)
    {
        if (offset == null)
            return defaultValue;
        var value = ReadByte(bytes, start, offset);
        return value is 0 or 1 ? value == 1 : null;
    }

    private static byte[] ParseBytes(string[] values)
    {
        return values.Select(ParseByte).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
    }

    private static byte? ParseByte(string? value)
    {
        var parsed = ParseInt(value);
        return parsed is >= 0 and <= 255 ? (byte)parsed.Value : null;
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

    private static BatteryReading DeviceForLog(HidDevice device, DeviceProfile profile) => new()
    {
        HasBatteryPercentage = false,
        DeviceName = Safe(() => device.GetProductName()),
        DeviceId = Safe(() => device.DevicePath),
        DeviceSerial = Safe(() => device.GetSerialNumber()),
        VendorId = $"0x{device.VendorID:X4}",
        ProductId = $"0x{device.ProductID:X4}",
        Source = string.IsNullOrWhiteSpace(profile.Name) ? "Known device profile" : profile.Name
    };

    private sealed class DeviceProfile
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;
        public string VendorId { get; set; } = "";
        public string[] ProductIds { get; set; } = Array.Empty<string>();
        public string[] CableProductIds { get; set; } = Array.Empty<string>();
        public string[] ProductNameContains { get; set; } = Array.Empty<string>();
        public string ReportType { get; set; } = "Feature";
        public string ReportId { get; set; } = "0x00";
        public string? ResponseReportId { get; set; }
        public string[] RequestBytes { get; set; } = Array.Empty<string>();
        public bool SendRequest { get; set; } = true;
        public int MinFeatureLength { get; set; }
        public int DelayMs { get; set; } = 80;
        public int[] PayloadStarts { get; set; } = Array.Empty<int>();
        public int? BatteryOffset { get; set; }
        public int? ChargingOffset { get; set; }
        public int? FullOffset { get; set; }
        public int? OnlineOffset { get; set; }
        public string Notes { get; set; } = "";
    }
}
