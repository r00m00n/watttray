using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WattTray.Power;

internal sealed class BatteryPowerReader : IDisposable
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint FileDeviceBattery = 0x00000029;
    private const uint MethodBuffered = 0;
    private const uint FileReadAccess = 0x0001;
    private const uint IoctlBatteryQueryTag = (FileDeviceBattery << 16) | (FileReadAccess << 14) | (0x10 << 2) | MethodBuffered;
    private const uint IoctlBatteryQueryInformation = (FileDeviceBattery << 16) | (FileReadAccess << 14) | (0x11 << 2) | MethodBuffered;
    private const uint IoctlBatteryQueryStatus = (FileDeviceBattery << 16) | (FileReadAccess << 14) | (0x13 << 2) | MethodBuffered;

    private const uint BatteryPowerOnLine = 0x00000001;
    private const uint BatteryDischarging = 0x00000002;
    private const uint BatteryCharging = 0x00000004;
    private const uint BatteryCapacityRelative = 0x40000000;
    private const uint BatterySystemBattery = 0x80000000;
    private const uint BatteryUnknownCapacity = uint.MaxValue;
    private const uint BatteryUnknownVoltage = uint.MaxValue;
    private const int BatteryUnknownRate = int.MinValue;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const int BatteryInformationLevel = 0;

    private static readonly Guid BatteryInterfaceClass = new(0x72631e54, 0x78a4, 0x11d0, 0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a);

    public BatteryReadResult Read()
    {
        var report = new StringBuilder();
        report.AppendLine($"WattTray battery diagnostics — {DateTimeOffset.Now:O}");
        report.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");

        var interfaceClass = BatteryInterfaceClass;
        var deviceSet = SetupDiGetClassDevs(ref interfaceClass, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            report.AppendLine($"Enumeration: SetupDiGetClassDevs failed: {FormatError(error)}");
            return new BatteryReadResult(null, null, report.ToString());
        }

        BatteryReading? reading = null;
        double? batteryPercentage = null;
        var deviceCount = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(deviceSet, IntPtr.Zero, ref interfaceClass, index, ref interfaceData))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                        report.AppendLine($"Enumeration: completed normally after {deviceCount} device interface(s) (ERROR_NO_MORE_ITEMS).");
                    else
                        report.AppendLine($"Enumeration: SetupDiEnumDeviceInterfaces failed at index {index}: {FormatError(error)}");
                    break;
                }

                deviceCount++;
                report.AppendLine();
                report.AppendLine($"Battery interface {index}:");
                var path = GetDevicePath(deviceSet, ref interfaceData, report);
                if (path is null)
                    continue;

                report.AppendLine($"  Device path: {path}");
                using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    report.AppendLine($"  CreateFile (read/write, share read/write): failed: {FormatError(error)}");
                    continue;
                }
                report.AppendLine("  CreateFile (read/write, share read/write): succeeded.");

                uint timeout = 0;
                if (!DeviceIoControlTag(handle, IoctlBatteryQueryTag, ref timeout, sizeof(uint), out var tag, sizeof(uint), out var tagBytes, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    report.AppendLine($"  QUERY_TAG: failed, bytesReturned={tagBytes}: {FormatError(error)}");
                    continue;
                }
                report.AppendLine($"  QUERY_TAG: succeeded, bytesReturned={tagBytes}, tag=0x{tag:X8}.");
                if (tag == 0)
                {
                    report.AppendLine("  QUERY_TAG: returned tag 0; this interface has no current battery.");
                    continue;
                }

                var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = BatteryInformationLevel, AtRate = 0 };
                var informationSucceeded = DeviceIoControlInformation(handle, IoctlBatteryQueryInformation, ref query,
                        (uint)Marshal.SizeOf<BatteryQueryInformation>(), out var information,
                        (uint)Marshal.SizeOf<BatteryInformation>(), out var informationBytes, IntPtr.Zero);
                if (!informationSucceeded)
                {
                    var error = Marshal.GetLastWin32Error();
                    report.AppendLine($"  QUERY_INFORMATION: failed, bytesReturned={informationBytes}: {FormatError(error)}");
                    information = default;
                }
                else
                {
                    report.AppendLine($"  QUERY_INFORMATION: succeeded, bytesReturned={informationBytes}, expected={Marshal.SizeOf<BatteryInformation>()} bytes.");
                    report.AppendLine($"    Capabilities=0x{information.Capabilities:X8}, systemBattery={(information.Capabilities & BatterySystemBattery) != 0}, relativeUnits={(information.Capabilities & BatteryCapacityRelative) != 0}");
                    var chemistry = new string(new[] { (char)information.Chemistry0, (char)information.Chemistry1, (char)information.Chemistry2, (char)information.Chemistry3 });
                    report.AppendLine($"    Technology={information.Technology}, chemistry={chemistry}, designedCapacity={information.DesignedCapacity}, fullChargedCapacity={information.FullChargedCapacity}, cycleCount={information.CycleCount}");
                }
                var informationAvailable = informationSucceeded && informationBytes >= Marshal.SizeOf<BatteryInformation>();
                if (!informationAvailable)
                    report.AppendLine("  QUERY_INFORMATION: response was shorter than BATTERY_INFORMATION.");

                var wait = new BatteryWaitStatus { BatteryTag = tag };
                if (!DeviceIoControlStatus(handle, IoctlBatteryQueryStatus, ref wait,
                        (uint)Marshal.SizeOf<BatteryWaitStatus>(), out var status,
                        (uint)Marshal.SizeOf<BatteryStatus>(), out var statusBytes, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    report.AppendLine($"  QUERY_STATUS: failed, bytesReturned={statusBytes}: {FormatError(error)}");
                    continue;
                }

                report.AppendLine($"  QUERY_STATUS: succeeded, bytesReturned={statusBytes}, expected={Marshal.SizeOf<BatteryStatus>()} bytes.");
                report.AppendLine($"    PowerState=0x{status.PowerState:X8} (online={(status.PowerState & BatteryPowerOnLine) != 0}, discharging={(status.PowerState & BatteryDischarging) != 0}, charging={(status.PowerState & BatteryCharging) != 0}), Capacity={FormatUnknown(status.Capacity, BatteryUnknownCapacity)}, Voltage={FormatUnknown(status.Voltage, BatteryUnknownVoltage)} mV, Rate={FormatRate(status.Rate)}");
                if (statusBytes < Marshal.SizeOf<BatteryStatus>())
                {
                    report.AppendLine("  QUERY_STATUS: response was shorter than BATTERY_STATUS; skipping this interface.");
                    continue;
                }
                if (informationAvailable && status.Capacity != BatteryUnknownCapacity && information.FullChargedCapacity > 0)
                {
                    batteryPercentage ??= Math.Clamp(status.Capacity * 100.0 / information.FullChargedCapacity, 0.0, 100.0);
                    report.AppendLine($"  Battery level: {status.Capacity}/{information.FullChargedCapacity} capacity units ({batteryPercentage:F1}%).");
                }
                else
                {
                    report.AppendLine("  Battery level: unavailable (capacity or full-charge capacity is unknown/zero).");
                }
                if (status.Rate == BatteryUnknownRate)
                {
                    report.AppendLine("  Result: rate is BATTERY_UNKNOWN_RATE; no watt reading is available.");
                    continue;
                }
                if (!informationAvailable)
                {
                    report.AppendLine("  Result: capabilities are unavailable, so rate units cannot be verified and watts are withheld.");
                    continue;
                }
                if ((information.Capabilities & BatteryCapacityRelative) != 0)
                {
                    report.AppendLine("  Result: rate uses relative units, so it cannot be displayed as watts.");
                    continue;
                }

                var state = (status.PowerState & BatteryCharging) != 0 ? BatteryPowerState.Charging
                    : (status.PowerState & BatteryDischarging) != 0 ? BatteryPowerState.Discharging
                    : (status.PowerState & BatteryPowerOnLine) != 0 ? BatteryPowerState.OnAc
                    : BatteryPowerState.Unknown;
                reading ??= new BatteryReading(Math.Abs((double)status.Rate) / 1000.0, status.Rate, state);
                report.AppendLine("  Result: absolute rate is reported in mW and is eligible for display as watts.");
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }

        if (deviceCount == 0)
            report.AppendLine("No present battery interfaces were found.");
        report.AppendLine(reading is null
            ? $"Overall result: power unavailable; battery level {FormatPercentage(batteryPercentage)}. See per-interface details above."
            : $"Overall result: {reading.Watts:F1} W ({reading.State}), raw rate {reading.RateMilliwatts} mW, battery level {FormatPercentage(batteryPercentage)}.");
        return new BatteryReadResult(reading, batteryPercentage, report.ToString());
    }

    private static string? GetDevicePath(IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData, StringBuilder report)
    {
        var success = SetupDiGetDeviceInterfaceDetail(deviceSet, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        var error = Marshal.GetLastWin32Error();
        if (success)
        {
            report.AppendLine("  SetupDiGetDeviceInterfaceDetail size query unexpectedly succeeded with a null buffer.");
            return null;
        }
        if (error != ErrorInsufficientBuffer)
        {
            report.AppendLine($"  SetupDiGetDeviceInterfaceDetail size query failed: {FormatError(error)}");
            return null;
        }
        report.AppendLine($"  SetupDiGetDeviceInterfaceDetail size query: ERROR_INSUFFICIENT_BUFFER, required={required} bytes.");
        if (required < 6 || required > int.MaxValue)
        {
            report.AppendLine("  Device detail buffer size is invalid; expected at least the 4-byte header and a UTF-16 terminator.");
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            var cbSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(buffer, cbSize);
            if (!SetupDiGetDeviceInterfaceDetail(deviceSet, ref interfaceData, buffer, required, out var returnedRequired, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                report.AppendLine($"  SetupDiGetDeviceInterfaceDetail data query failed (cbSize={cbSize}, buffer={required}): {FormatError(error)}");
                return null;
            }

            report.AppendLine($"  SetupDiGetDeviceInterfaceDetail data query succeeded (cbSize={cbSize}, required={returnedRequired}, pathOffset=4).");
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string FormatError(int error) => $"Win32 error {error}: {new Win32Exception(error).Message}";

    private static string FormatUnknown(uint value, uint unknown) => value == unknown ? "unknown" : value.ToString();

    private static string FormatPercentage(double? value) => value is null ? "unavailable" : $"{value:F1}%";

    private static string FormatRate(int rate) => rate == BatteryUnknownRate ? "unknown (BATTERY_UNKNOWN_RATE)" : $"{rate} {(rate > 0 ? "charging" : rate < 0 ? "discharging" : "zero")}";

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved0;
        public byte Reserved1;
        public byte Reserved2;
        public byte Chemistry0;
        public byte Chemistry1;
        public byte Chemistry2;
        public byte Chemistry3;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlTag(SafeFileHandle device, uint ioControlCode, ref uint inBuffer, uint inBufferSize,
        out uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlInformation(SafeFileHandle device, uint ioControlCode, ref BatteryQueryInformation inBuffer,
        uint inBufferSize, out BatteryInformation outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlStatus(SafeFileHandle device, uint ioControlCode, ref BatteryWaitStatus inBuffer,
        uint inBufferSize, out BatteryStatus outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
