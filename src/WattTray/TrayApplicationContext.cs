using WattTray.Power;

namespace WattTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly BatteryPowerReader _powerReader = new();

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
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
        var reading = _powerReader.Read();
        _notifyIcon.Text = reading is null
            ? "WattTray - power unavailable"
            : $"WattTray - {reading.Watts:F1} W ({reading.State})";
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _powerReader.Dispose();
        base.ExitThreadCore();
    }
}
