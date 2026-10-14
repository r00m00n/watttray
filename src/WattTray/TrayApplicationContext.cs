using WattTray.Power;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WattTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly BatteryPowerReader _powerReader = new();
    private string _diagnostics = "No battery query has run yet.";
    private Icon? _currentPowerIcon;
    private string? _currentPowerIconKey;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy diagnostics", null, (_, _) => Clipboard.SetText(_diagnostics));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WattTray",
            Visible = true,
            ContextMenuStrip = menu
        };

        _timer = new System.Windows.Forms.Timer { Interval = 2_000 };
        _timer.Tick += (_, _) => RefreshPower();
        _timer.Start();

        RefreshPower();
    }

    private void RefreshPower()
    {
        var result = _powerReader.Read();
        _diagnostics = result.Diagnostics;
        UpdatePowerIcon(result.Reading);
        _notifyIcon.Text = result.Reading is null
            ? result.BatteryPercentage is null
                ? "WattTray - power unavailable"
                : $"WattTray - {result.BatteryPercentage:F0}% battery"
            : result.BatteryPercentage is null
                ? $"WattTray - {result.Reading.Watts:F1} W ({result.Reading.State})"
                : $"WattTray - {result.Reading.Watts:F1} W ({result.Reading.State}), {result.BatteryPercentage:F0}%";
    }

    private void UpdatePowerIcon(BatteryReading? reading)
    {
        var label = reading is null ? "--" : FormatIconWatts(reading.Watts);
        var state = reading?.State ?? BatteryPowerState.Unknown;
        var key = $"{label}:{state}";
        if (key == _currentPowerIconKey)
            return;

        var replacement = CreatePowerIcon(label, state);
        var previous = _currentPowerIcon;
        _notifyIcon.Icon = replacement;
        _currentPowerIcon = replacement;
        _currentPowerIconKey = key;
        previous?.Dispose();
    }

    private static string FormatIconWatts(double watts) => watts >= 1000
        ? $"{watts / 1000:0.#}k"
        : Math.Round(watts).ToString("0", CultureInfo.InvariantCulture);

    private static Icon CreatePowerIcon(string value, BatteryPowerState state)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            var accent = state switch
            {
                BatteryPowerState.Charging => Color.FromArgb(69, 210, 145),
                BatteryPowerState.Discharging => Color.FromArgb(255, 177, 66),
                BatteryPowerState.OnAc => Color.FromArgb(89, 170, 255),
                _ => Color.FromArgb(145, 155, 170)
            };

            using var unitFont = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            using var unitBrush = new SolidBrush(accent);
            var mainBounds = new RectangleF(0, -1, 32, 30);
            var unitBounds = new RectangleF(23, 22, 9, 9);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            var mainSize = value.Length >= 3 ? 26f : value.Length == 2 ? 32f : 34f;
            Font mainFont;
            while (true)
            {
                mainFont = new Font("Segoe UI", mainSize, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(value, mainFont, int.MaxValue, centered);
                if (measured.Width <= 29 && measured.Height <= 28 || mainSize <= 10)
                    break;
                mainFont.Dispose();
                mainSize -= 1;
            }
            using (mainFont)
            {
                graphics.DrawString(value, mainFont, shadowBrush, new RectangleF(1, 0, 32, 30), centered);
                graphics.DrawString(value, mainFont, textBrush, mainBounds, centered);
            }
            if (value != "--")
                graphics.DrawString("W", unitFont, unitBrush, unitBounds, centered);
        }

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var borrowedIcon = Icon.FromHandle(iconHandle);
            return (Icon)borrowedIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentPowerIcon?.Dispose();
        _powerReader.Dispose();
        base.ExitThreadCore();
    }
}
