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
        "Settings" => "设置",
        "Charging" => "充电中",
        "Online" => "在线",
        "Offline" => "离线",
        "CheckNow" => "立即检测",
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
        "Refresh" => "刷新",
        "Play" => "播放",
        "OpenFolder" => "打开音效文件夹",
        "Default" => "恢复默认",
        "Save" => "保存",
        "Close" => "关闭",
        "SoundHelp" => "把 .wav 文件放到音效目录，点击刷新后选择。",
        _ => key
    };

    private static string En(string key) => key switch
    {
        "NotReady" => "SORA V2: not detected",
        "Checking" => "SORA V2: checking...",
        "Settings" => "Settings",
        "Charging" => "charging",
        "Online" => "online",
        "Offline" => "offline",
        "CheckNow" => "Check Now",
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
        "Refresh" => "Refresh",
        "Play" => "Play",
        "OpenFolder" => "Open Sound Folder",
        "Default" => "Reset Default",
        "Save" => "Save",
        "Close" => "Close",
        "SoundHelp" => "Put .wav files into the sound folder, then refresh and select one.",
        _ => key
    };
}

