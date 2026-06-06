namespace SoraV2BatteryTip;

internal sealed class Localizer
{
    private readonly Func<AppSettings> _settings;
    public Localizer(Func<AppSettings> settings) => _settings = settings;
    public bool IsZh => _settings().Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || (_settings().Language == "auto" && Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh"));
    public string this[string key] => IsZh ? Zh(key) : En(key);

    private static string Zh(string key) => key switch
    {
        "NotReady" => "SORA V2: 未检测到",
        "Checking" => "SORA V2: 正在检测...",
        "Charging" => "充电中",
        "CheckNow" => "立即检测",
        "BatteryHistory" => "电量记录",
        "TestSound" => "测试提示音",
        "Exit" => "退出",
        "Uninstall" => "卸载并清理",
        "UninstallTitle" => "卸载 Sora V2 Battery 提示",
        "UninstallConfirm" => "确认彻底卸载？\n\n将清理开机启动项、注册表中的本软件记录、我的文档中的 SoraV2BatteryTip 数据目录，并在程序退出后删除当前程序目录。\n\n此操作不可撤销。",
        "UninstallStartedTitle" => "Sora V2 Battery 提示",
        "UninstallStarted" => "已开始卸载。程序退出后会继续清理当前程序目录。",
        "Threshold" => "低电量阈值 (%)",
        "Interval" => "检测间隔 (分钟)",
        "Cooldown" => "提醒冷却 (分钟)",
        "Startup" => "开机自启",
        "Language" => "语言",
        "Sound" => "提示音",
        "OpenFolder" => "打开音效文件夹",
        "Default" => "恢复默认",
        "Last24Hours" => "24小时",
        "Last7Days" => "7天",
        "Last30Days" => "30天",
        "HistoryDemo" => "演示样板数据",
        "HistoryRealData" => "真实记录数据",
        "HistoryEstimate" => "预计剩余",
        "HistoryUnavailable" => "暂无",
        "HistoryConsumed" => "范围内消耗",
        "HistoryAverage" => "平均耗电",
        "HistoryChargingTimes" => "充电段",
        "HistoryLastFull" => "最近满电/充电",
        _ => key
    };

    private static string En(string key) => key switch
    {
        "NotReady" => "SORA V2: not detected",
        "Checking" => "SORA V2: checking...",
        "Charging" => "charging",
        "CheckNow" => "Check Now",
        "BatteryHistory" => "Battery History",
        "TestSound" => "Test Sound",
        "Exit" => "Exit",
        "Uninstall" => "Uninstall and clean up",
        "UninstallTitle" => "Uninstall Sora V2 Battery Tip",
        "UninstallConfirm" => "Completely uninstall?\n\nThis will remove startup entries, this app's registry records, the SoraV2BatteryTip data directory in Documents, and delete the current program directory after the app exits.\n\nThis cannot be undone.",
        "UninstallStartedTitle" => "Sora V2 Battery Tip",
        "UninstallStarted" => "Uninstall started. The current program directory will be removed after the app exits.",
        "Threshold" => "Low battery threshold (%)",
        "Interval" => "Polling interval (minutes)",
        "Cooldown" => "Alert cooldown (minutes)",
        "Startup" => "Start with Windows",
        "Language" => "Language",
        "Sound" => "Alert sound",
        "OpenFolder" => "Open Sound Folder",
        "Default" => "Reset Default",
        "Last24Hours" => "24h",
        "Last7Days" => "7d",
        "Last30Days" => "30d",
        "HistoryDemo" => "Demo sample data",
        "HistoryRealData" => "Real recorded data",
        "HistoryEstimate" => "Estimated remaining",
        "HistoryUnavailable" => "n/a",
        "HistoryConsumed" => "Used in range",
        "HistoryAverage" => "Average drain",
        "HistoryChargingTimes" => "Charging segments",
        "HistoryLastFull" => "Last full/charge",
        _ => key
    };
}

