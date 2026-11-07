using System;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Serilog;

namespace HABluetoothProxy;

public sealed class BleReceiver : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly Channel<BluetoothLEAdvertisementReceivedEventArgs> _channel;

    public ChannelReader<BluetoothLEAdvertisementReceivedEventArgs> Reader => _channel.Reader;

    public BleReceiver()
    {
        // SingleWriter = true since only WinRT watcher raises events here
        _channel = Channel.CreateUnbounded<BluetoothLEAdvertisementReceivedEventArgs>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Passive
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
    }

    public void Start()
    {
        Log.Information("Starting WinRT BluetoothLEAdvertisementWatcher...");
        _watcher.Start();
    }

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            Log.Information("Stopping WinRT BluetoothLEAdvertisementWatcher...");
            _watcher.Stop();
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        //Log.Debug("[BLE] {Address:X12} | RSSI: {Rssi} dBm | Name: '{Name}'",
        //    args.BluetoothAddress,
        //    args.RawSignalStrengthInDBm,
        //    args.Advertisement.LocalName);

        // Immediate non-blocking write to channel
        _channel.Writer.TryWrite(args);
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
        {
            Log.Error("WinRT BLE Watcher stopped unexpectedly: {Error}", args.Error);
        }
        else
        {
            Log.Information("WinRT BLE Watcher stopped gracefully.");
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;
        Stop();
    }
}