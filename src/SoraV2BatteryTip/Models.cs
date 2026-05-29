namespace SoraV2BatteryTip;
internal sealed class BatteryReading { public int BatteryPercentage { get; init; } public bool HasBatteryPercentage { get; init; } = true; public bool IsCharging { get; init; } public bool IsFullyCharged { get; init; } public bool IsOnline { get; init; } = true; public bool IsCableConnected { get; init; } public string Source { get; init; } = "SORA V2 HID"; }
internal sealed class AppSettings { public int AlertThreshold { get; set; } = 10; public int PollingIntervalMinutes { get; set; } = 10; public int AlertCooldownMinutes { get; set; } = 10; public bool StartupWithWindows { get; set; } public string Language { get; set; } = "auto"; public string AlertSoundFile { get; set; } = "default.wav"; public int AlertVolume { get; set; } = 15; }

