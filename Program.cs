using System.Threading.Channels;
using Serilog;
using Windows.Devices.Bluetooth.Advertisement;

namespace HABluetoothProxy;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        ConfigureLogging();

        Log.Information("Starting HA Bluetooth Proxy...");

        CancellationTokenSource cts = new();
        EsphomeServer server = new();
        TrayApplicationContext context = new();

        Application.ApplicationExit += (sender, e) =>
        {
            Log.Information("Application.ApplicationExit fired. Cleaning up...");
            cts.Cancel();
            server.Stop();

            cts.Dispose();
            Log.CloseAndFlush();
        };

        server.Start(cts.Token);

        Application.Run(context);
    }

    static void ConfigureLogging()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ha-btproxy",
            "app.log"
        );

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 90)
            .CreateLogger();
    }
}