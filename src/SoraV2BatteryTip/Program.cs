namespace SoraV2BatteryTip;
internal static class Program
{
    private const string MutexName="Global\\SoraV2BatteryTip.Minimal";
    [STAThread] private static void Main(){using var mutex=new Mutex(true,MutexName,out var created); if(!created)return; ApplicationConfiguration.Initialize(); Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.Run(new TrayAppContext());}
}

