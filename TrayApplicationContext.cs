namespace HABluetoothProxy;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;

    public TrayApplicationContext()
    {
        var contextMenu = new ContextMenuStrip();
        var statusItem = new ToolStripMenuItem("HA Bluetooth Proxy") { Enabled = false };
        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        contextMenu.Items.Add(statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Text = "HA Bluetooth Proxy",
            Visible = true
        };
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }
}