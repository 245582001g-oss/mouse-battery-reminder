namespace SoraV2BatteryTip;

internal sealed class BatteryReading
{
    public int BatteryPercentage { get; init; }
    public bool HasBatteryPercentage { get; init; } = true;
    public bool IsCharging { get; init; }
    public bool IsFullyCharged { get; init; }
    public bool IsOnline { get; init; } = true;
    public bool IsCableConnected { get; init; }
    public string DeviceName { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Source { get; init; } = "unknown";
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

internal sealed class BatteryReadResult
{
    public BatteryReading? Reading { get; init; }
    public string Source { get; init; } = "none";
    public string FailureReason { get; init; } = "not_detected";
    public bool Success => Reading != null;
}

internal sealed class BatteryReadAllResult
{
    public IReadOnlyList<BatteryReading> Readings { get; init; } = Array.Empty<BatteryReading>();
    public string Source { get; init; } = "none";
    public string FailureReason { get; init; } = "not_detected";
    public bool Success => Readings.Count > 0;
}

internal sealed class ProviderStatus
{
    public string Name { get; init; } = "";
    public int Priority { get; init; }
    public bool IsAvailable { get; init; }
    public string Error { get; init; } = "";
}

internal sealed class ProfileValidationStatus
{
    public string FileName { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public bool IsValid { get; init; }
    public string Error { get; init; } = "";
}

internal sealed class ProfileDraftImportResult
{
    public int Imported { get; init; }
    public int Rejected { get; init; }
    public int Total { get; init; }
    public int ToleranceUsed { get; init; }
    public int StableReadsRequired { get; init; }
    public int? OfficialBatteryPercentage { get; init; }
    public string SourceDirectory { get; init; } = "";
    public string[] ImportedFiles { get; init; } = Array.Empty<string>();
    public string MessageKey => Total == 0 ? "NoDraftsFound" : Imported > 0 ? "DraftsImported" : "DraftsRejected";
}

internal sealed class AppSettings
{
    public int AlertThreshold { get; set; } = 10;
    public int PollingIntervalMinutes { get; set; } = 10;
    public int AlertCooldownMinutes { get; set; } = 10;
    public bool StartupWithWindows { get; set; }
    public string Language { get; set; } = "auto";
    public string AlertSoundFile { get; set; } = "default.wav";
    public int AlertVolume { get; set; } = 15;
}

internal readonly record struct DeviceConnection(bool WiredPresent, bool WirelessPresent)
{
    public bool IsDetected => WiredPresent || WirelessPresent;
    public bool IsCableConnected => WiredPresent;
}
