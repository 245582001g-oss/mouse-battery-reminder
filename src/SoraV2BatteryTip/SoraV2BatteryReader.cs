using HidSharp;
namespace SoraV2BatteryTip;
internal sealed class SoraV2BatteryReader
{
    private const int VendorId=0x1915, WiredProductId=0xAE12, WirelessProductId=0xAE1C, FeatureReportId=0x05, MainReportId=0x04;
    private const int MinimumValidBatteryPercentage=3, SuspiciousDropThreshold=30;
    private int? _lastGoodBatteryPercentage;
    public Task<BatteryReading?> ReadAsync(CancellationToken token)=>Task.Run(()=>ReadOnce(token),token);
    public (bool WiredPresent,bool WirelessPresent) DetectConnection()
    {
        var wired=DeviceList.Local.GetHidDevices(VendorId,WiredProductId).Any(IsSoraDevice);
        var wireless=DeviceList.Local.GetHidDevices(VendorId,WirelessProductId).Any(IsSoraDevice);
        return (wired,wireless);
    }
    private BatteryReading? ReadOnce(CancellationToken token)
    {
        var devices=new[]{WiredProductId,WirelessProductId}.SelectMany(pid=>DeviceList.Local.GetHidDevices(VendorId,pid).Where(d=>d.GetMaxFeatureReportLength()>1).OrderByDescending(d=>d.GetMaxFeatureReportLength()).Select(d=>new{Device=d,IsCableConnected=pid==WiredProductId})).ToList();
        foreach(var item in devices){ var device=item.Device; token.ThrowIfCancellationRequested(); if(!IsSoraDevice(device))continue; if(!device.TryOpen(out var stream)||stream==null)continue; using(stream){ try{ var profile=ReadProfile(stream,device); if(profile<=0)continue; var first=ReadStatus(stream,device,profile,item.IsCableConnected); if(first==null)continue; Thread.Sleep(120); var second=ReadStatus(stream,device,profile,item.IsCableConnected); if(second==null)continue; if(Math.Abs(first.BatteryPercentage-second.BatteryPercentage)>1)continue; return Stabilize(second);}catch{} } }
        return null;
    }
    private static bool IsSoraDevice(HidDevice device){ var p=string.Empty; try{p=device.GetProductName()??device.ProductName??string.Empty;}catch{} return p.Length==0||p.Contains("Sora",StringComparison.OrdinalIgnoreCase)||p.Contains("Ninjutso",StringComparison.OrdinalIgnoreCase); }
    private static int ReadProfile(HidStream stream,HidDevice device){ var len=Math.Max(32,device.GetMaxFeatureReportLength()); var req=new byte[len]; req[0]=FeatureReportId; req[1]=13; req[3]=1; req[6]=22; for(var i=0;i<2;i++){ stream.SetFeature(req,0,req.Length); Thread.Sleep(25); var resp=new byte[len]; resp[0]=FeatureReportId; stream.GetFeature(resp,0,resp.Length); if(resp.Length>9&&resp[9]!=0)return resp[9]; if(resp.Length>10&&resp[10]!=0)return resp[10]; } return 0; }
    private static BatteryReading? ReadStatus(HidStream stream,HidDevice device,int profile,bool isCableConnected){ var len=Math.Max(73,device.GetMaxFeatureReportLength()); var req=new byte[len]; req[0]=MainReportId; req[1]=38; req[3]=1; req[5]=checked((byte)profile); stream.SetFeature(req,0,req.Length); Thread.Sleep(120); var resp=new byte[len]; resp[0]=MainReportId; stream.GetFeature(resp,0,resp.Length); return TryParseStatus(resp,isCableConnected,out var reading)?reading:null; }
    private static bool TryParseStatus(byte[] response,bool isCableConnected,out BatteryReading reading){ reading=null!; foreach(var payloadStart in new[]{9,10}){ if(response.Length<=payloadStart+11)continue; if(payloadStart==10&&response[0]!=MainReportId)continue; var battery=response[payloadStart+7]; var charging=response[payloadStart+8]; var full=response[payloadStart+9]; var mode=response[payloadStart+10]; var online=response[payloadStart+11]; if(battery is <1 or >100||charging>1||full>1||mode>1||online>1)continue; reading=new BatteryReading{BatteryPercentage=battery,HasBatteryPercentage=true,IsCharging=charging==1||full==1,IsFullyCharged=full==1,IsOnline=online==1,IsCableConnected=isCableConnected,Source="SORA V2 HID"}; return true;} return false; }
    private BatteryReading? Stabilize(BatteryReading reading){ if(reading.BatteryPercentage is >0 and <MinimumValidBatteryPercentage){ return _lastGoodBatteryPercentage.HasValue?new BatteryReading{BatteryPercentage=_lastGoodBatteryPercentage.Value,HasBatteryPercentage=true,IsCharging=false,IsFullyCharged=false,IsOnline=true,IsCableConnected=reading.IsCableConnected,Source=reading.Source}:null; } if(_lastGoodBatteryPercentage.HasValue&&_lastGoodBatteryPercentage.Value-reading.BatteryPercentage>=SuspiciousDropThreshold){ return new BatteryReading{BatteryPercentage=_lastGoodBatteryPercentage.Value,HasBatteryPercentage=true,IsCharging=false,IsFullyCharged=false,IsOnline=true,IsCableConnected=reading.IsCableConnected,Source=reading.Source}; } _lastGoodBatteryPercentage=reading.BatteryPercentage; return reading; }
}

