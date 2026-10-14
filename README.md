# WattTray

WattTray is a small native Windows tray application that displays laptop battery power draw.

## Goal

Keep the app tiny, dependency-free and Windows-native. The first milestone reads the battery driver's current charge/discharge rate through the Windows battery class API and exposes it from the notification area.

## Stack

- C# / .NET 10
- Windows Forms `NotifyIcon` for the system tray
- SetupAPI to enumerate battery devices
- `DeviceIoControl` with `IOCTL_BATTERY_QUERY_TAG` and `IOCTL_BATTERY_QUERY_STATUS`

`BATTERY_STATUS.Rate` is reported in milliwatts when the battery exposes absolute capacity units. Positive values mean charging and negative values mean discharging.

## Run

```powershell
dotnet run --project .\src\WattTray\WattTray.csproj
```

Hover over the tray icon to see the current reading. Right-click it and choose **Exit** to quit.

## Status

Early MVP. Hardware support varies by battery/driver; fallback providers and a custom tray rendering are planned after validating the native battery API on real machines.
