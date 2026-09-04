namespace SoraV2BatteryTip;

internal static class DevicePowerSemantics
{
    public static bool IsExternallyPowered(BatteryReading reading)
    {
        return reading.PowerState is DevicePowerState.Charging or DevicePowerState.FullyCharged or DevicePowerState.PendingCharge
            || reading.ExternalPowerConnected == true
            || reading.IsCableConnected
            || reading.IsCharging
            || reading.IsFullyCharged;
    }

    public static bool IsFullyCharged(BatteryReading reading)
    {
        return reading.PowerState == DevicePowerState.FullyCharged
            || reading.IsFullyCharged
            || (IsExternallyPowered(reading) && reading.BatteryPercentage >= 100);
    }
}
