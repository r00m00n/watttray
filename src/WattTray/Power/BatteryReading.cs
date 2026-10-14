namespace WattTray.Power;

internal enum BatteryPowerState
{
    Unknown,
    Charging,
    Discharging,
    OnAc
}

internal sealed record BatteryReading(double Watts, int RateMilliwatts, BatteryPowerState State);
