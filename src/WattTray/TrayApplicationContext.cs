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

        var replacement = CreatePowerIcon(label, GetAccent(reading));
        var previous = _currentPowerIcon;
        _notifyIcon.Icon = replacement;
        _currentPowerIcon = replacement;
        _currentPowerIconKey = key;
        previous?.Dispose();
    }

    private static string FormatIconWatts(double watts) => watts >= 1000
        ? $"{watts / 1000:0.#}k"
        : Math.Round(watts).ToString("0", CultureInfo.InvariantCulture);

    private static Color GetAccent(BatteryReading? reading)
    {
        if (reading is null)
            return Color.FromArgb(160, 168, 180);

        return reading.State switch
        {
            BatteryPowerState.Discharging => Math.Round(reading.Watts) switch
            {
                <= 12 => Color.FromArgb(76, 217, 100),
                <= 25 => Color.FromArgb(255, 159, 10),
                _ => Color.FromArgb(255, 69, 58)
            },
            BatteryPowerState.Charging => Color.FromArgb(64, 200, 224),
            BatteryPowerState.OnAc => Color.FromArgb(100, 170, 255),
            _ => Color.FromArgb(160, 168, 180)
        };
    }

    // Matches the Windows 11 taskbar clock font, with fallbacks for older systems.
    private static FontFamily GetTrayFontFamily()
    {
        foreach (var name in new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" })
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return FontFamily.GenericSansSerif;
    }

    private static Icon CreatePowerIcon(string value, Color accent)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using var path = new GraphicsPath();
            using var format = StringFormat.GenericTypographic;
            using var family = GetTrayFontFamily();
            path.AddString(value, family, (int)FontStyle.Regular, 100f, PointF.Empty, format);

            // Scale glyphs to fill the icon, keeping aspect ratio.
            var bounds = path.GetBounds();
            const float maxWidth = 30f, maxHeight = 28f;
            var scale = Math.Min(maxWidth / bounds.Width, maxHeight / bounds.Height);
            using var matrix = new Matrix();
            matrix.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
            matrix.Scale(scale, scale, MatrixOrder.Append);
            matrix.Translate(size / 2f, size / 2f, MatrixOrder.Append);
            path.Transform(matrix);

            using var outline = new Pen(Color.FromArgb(200, 15, 15, 20), 2.5f) { LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(accent);
            graphics.DrawPath(outline, path);
            graphics.FillPath(fill, path);
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
