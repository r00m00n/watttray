using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

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
    private const uint IoctlBatteryQueryStatus = (FileDeviceBattery << 16) | (FileReadAccess << 14) | (0x13 << 2) | MethodBuffered;

    private const uint BatteryPowerOnLine = 0x00000001;
    private const uint BatteryDischarging = 0x00000002;
    private const uint BatteryCharging = 0x00000004;
    private const int BatteryUnknownRate = int.MinValue;

    private static readonly Guid GuidDeviceBattery = new(0x72631e54, 0x78a4, 0x11d0, 0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a);

    public BatteryReading? Read()
    {
        foreach (var path in EnumerateBatteryPaths())
        {
            using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (handle.IsInvalid)
                continue;

            if (!TryQueryTag(handle, out var tag) || tag == 0)
                continue;

            if (!TryQueryStatus(handle, tag, out var status) || status.Rate == BatteryUnknownRate)
                continue;

            // Windows battery class reports Rate in mW for batteries that use absolute capacity units.
            // Positive means charging; negative means discharging.
            var state = (status.PowerState & BatteryCharging) != 0 ? BatteryPowerState.Charging
                : (status.PowerState & BatteryDischarging) != 0 ? BatteryPowerState.Discharging
                : (status.PowerState & BatteryPowerOnLine) != 0 ? BatteryPowerState.OnAc
                : BatteryPowerState.Unknown;

            return new BatteryReading(Math.Abs(status.Rate) / 1000.0, status.Rate, state);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBatteryPaths()
    {
        var set = SetupDiGetClassDevs(ref GuidDeviceBattery, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
            yield break;

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GuidDeviceBattery, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) yield break; // ERROR_NO_MORE_ITEMS
                    yield break;
                }

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA has a platform-specific header size.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                        continue;

                    var pathOffset = IntPtr.Size == 8 ? 8 : 4;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, pathOffset));
                    if (!string.IsNullOrWhiteSpace(path)) yield return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool TryQueryTag(SafeFileHandle handle, out uint tag)
    {
        uint timeout = 0;
        return DeviceIoControl(handle, IoctlBatteryQueryTag, ref timeout, sizeof(uint), out tag, sizeof(uint), out _, IntPtr.Zero);
    }

    private static bool TryQueryStatus(SafeFileHandle handle, uint tag, out BatteryStatus status)
    {
        var wait = new BatteryWaitStatus { BatteryTag = tag };
        return DeviceIoControl(handle, IoctlBatteryQueryStatus, ref wait, (uint)Marshal.SizeOf<BatteryWaitStatus>(),
            out status, (uint)Marshal.SizeOf<BatteryStatus>(), out _, IntPtr.Zero);
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref uint inBuffer, uint inBufferSize,
        out uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryWaitStatus inBuffer, uint inBufferSize,
        out BatteryStatus outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
