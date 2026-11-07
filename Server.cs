using System.Net;
using System.Net.Sockets;
using Serilog;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace HABluetoothProxy;

public class EsphomeServer
{
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private readonly BleReceiver _receiver;

    public EsphomeServer(int port = 6053)
    {
        _port = port;
        _receiver = new();
    }

    public void Start(CancellationToken externalToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        Log.Information("ESPHome TCP server listening on port {Port}", _port);
        _ = AcceptClientsAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        Log.Information("ESPHome TCP server stopped.");
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Error accepting TCP client connection");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Log.Information("Client connected: {RemoteEndPoint}", remoteEndPoint);

        using (client)
        using (var stream = client.GetStream())
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var writeLock = new SemaphoreSlim(1, 1);

            // Background task to consume BLE advertisements from the channel and stream them out
            var advertisingTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var args in _receiver.Reader.ReadAllAsync(cts.Token))
                    {
                        var advResponse = new BluetoothLERawAdvertisementsResponse();
                        advResponse.Advertisements.Add(args.ToProto());

                        await writeLock.WaitAsync(cts.Token);
                        try
                        {
                            await ServerProtocol.SendFrameAsync(stream, 93 /* BluetoothLERawAdvertisementsResponse */, advResponse, cancellationToken);
                        }
                        finally
                        {
                            writeLock.Release();
                        }
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error streaming BLE advertisements to {RemoteEndPoint}", remoteEndPoint);
                }
            }, cts.Token);

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var frame = await ServerProtocol.ReadFrameAsync(stream, cts.Token);
                    if (frame == null) break;

                    await ProcessMessageAsync(stream, frame.Value.TypeId, frame.Value.Payload, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Error(ex, "Session error for client {RemoteEndPoint}", remoteEndPoint);
            }
            finally
            {
                // Cancel the background advertising task and ensure the watcher stops when the client disconnects
                cts.Cancel();
                _receiver.Stop();

                Log.Information("Client disconnected: {RemoteEndPoint}", remoteEndPoint);
            }
        }
    }

    private HelloResponse Hello(HelloRequest req)
    {
        Log.Information("Received HelloRequest from {ClientInfo} (API v{Major}.{Minor})", req.ClientInfo, req.ApiVersionMajor, req.ApiVersionMinor);
        var res = new HelloResponse
        {
            ApiVersionMajor = req.ApiVersionMajor,
            ApiVersionMinor = req.ApiVersionMinor,
            ServerInfo = "HABluetoothProxy",
            Name = "HABluetoothProxy",
        };
        return res;
    }

    [Obsolete]
    private AuthenticationResponse Authenticate(AuthenticationRequest req)
    {
        Log.Information("Received AuthenticationRequest");
        var res = new AuthenticationResponse
        {
            InvalidPassword = false,  // everything goes
        };
        return res;
    }

    private DisconnectResponse Disconnect(DisconnectRequest req)
    {
        Log.Information("Received DisconnectRequest");
        var res = new DisconnectResponse();
        return res;
    }

    private PingResponse Ping(PingRequest req)
    {
        Log.Information("Received PingRequest");
        var res = new PingResponse();
        return res;
    }

    private DeviceInfoResponse DeviceInfo(DeviceInfoRequest req)
    {
        Log.Information("Received DeviceInfoRequest");

        var adapter = Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync().GetAwaiter().GetResult();
        var macAddress = string.Join(":",
            BitConverter.GetBytes(adapter.BluetoothAddress)
                .Reverse()
                .Skip(2) // BluetoothAddress is 48-bit stored in a 64-bit uint, skip top 2 bytes
                .Select(b => b.ToString("X2"))
        );

        var res = new DeviceInfoResponse
        {
            Name = System.Environment.MachineName,
            // This should probably be the network MAC
            MacAddress = macAddress,
            EsphomeVersion = "2026.4.0",
            Model = "ha-btproxy",
            Manufacturer = "ha-btproxy",
            FriendlyName = System.Environment.MachineName,

            // Superseded, still required
            BluetoothMacAddress = macAddress,
            // Superseded, still required
            BluetoothProxyFeatureFlags = (1 << 0) | (1 << 5),
        };
        return res;
    }

    private ListEntitiesDoneResponse ListEntities(ListEntitiesRequest req)
    {
        Log.Information("Received ListEntitiesRequest");
        var res = new ListEntitiesDoneResponse();
        return res;
    }

    private void SubscribeBluetoothLEAdvertisements(SubscribeBluetoothLEAdvertisementsRequest req)
    {
        Log.Information("Received SubscribeBluetoothLEAdvertisements");
        _receiver.Start();
        return;
    }

    private void UnsubscribeBluetoothLEAdvertisements(UnsubscribeBluetoothLEAdvertisementsRequest req)
    {
        Log.Information("Rececived UnsubscribeBluetoothLEAdvertisements");
        _receiver.Stop();
        return;
    }

    private DeviceCapabilitiesResponse DeviceCapabilities(DeviceCapabilitiesRequest req)
    {
        Log.Information("Received DeviceCapabilitiesRequest");
        var res = new DeviceCapabilitiesResponse
        {
            BluetoothProxy = new BluetoothProxyCapabilities
            {
                FeatureFlags = (1 << 0) | (1 << 5),
                MacAddress = "AA:BB:CC:DD:EE:FF",
            },
        };
        return res;
    }

    private async Task ProcessMessageAsync(NetworkStream stream, int typeId, byte[] payload, CancellationToken cancellationToken)
    {
        switch (typeId)
        {
            case 1: // HelloRequest
                {
                    var req = HelloRequest.Parser.ParseFrom(payload);
                    var res = Hello(req);
                    await ServerProtocol.SendFrameAsync(stream, 2 /* HelloResponse */, res, cancellationToken);
                    break;
                }
            case 3: // AuthenticationRequest
                {
                    var req = AuthenticationRequest.Parser.ParseFrom(payload);
                    var res = Authenticate(req);
                    await ServerProtocol.SendFrameAsync(stream, 4 /* AuthenticationResponse */, res, cancellationToken);
                    break;
                }
            case 5: // DisconnectRequest
                {
                    var req = DisconnectRequest.Parser.ParseFrom(payload);
                    var res = Disconnect(req);
                    await ServerProtocol.SendFrameAsync(stream, 6 /* DisconnectResponse */, res, cancellationToken);
                    // SPECIAL: Actually disconnect
                    throw new OperationCanceledException("Client requested disconnect.");
                }
            case 7: // PingRequest
                {
                    var req = PingRequest.Parser.ParseFrom(payload);
                    var res = Ping(req);
                    await ServerProtocol.SendFrameAsync(stream, 8 /* PingResponse */, res, cancellationToken);
                    break;
                }
            case 9: // DeviceInfoRequest
                {
                    var req = DeviceInfoRequest.Parser.ParseFrom(payload);
                    var res = DeviceInfo(req);
                    await ServerProtocol.SendFrameAsync(stream, 10 /* DeviceInfoResponse */, res, cancellationToken);
                    break;
                }
            case 11: // ListEntitiesRequest
                {
                    var req = ListEntitiesRequest.Parser.ParseFrom(payload);
                    var res = ListEntities(req);

                    // In general many types to send, but for us...
                    await ServerProtocol.SendFrameAsync(stream, 19 /* ListEntitiesDoneResponse */, res, cancellationToken);
                    break;
                }
            case 66: // SubscribeBluetoothLEAdvertisementsRequest
                {
                    var req = SubscribeBluetoothLEAdvertisementsRequest.Parser.ParseFrom(payload);
                    SubscribeBluetoothLEAdvertisements(req);
                    break;
                }
            case 87: // UnsubscribeBluetoothLEAdvertisementsRequest
                {
                    var req = UnsubscribeBluetoothLEAdvertisementsRequest.Parser.ParseFrom(payload);
                    UnsubscribeBluetoothLEAdvertisements(req);
                    break;
                }
            case 149: // DeviceCapabilitiesRequest
                {
                    var req = DeviceCapabilitiesRequest.Parser.ParseFrom(payload);
                    var res = DeviceCapabilities(req);
                    await ServerProtocol.SendFrameAsync(stream, 150 /* DeviceCapabilitiesResponse */, res, cancellationToken);
                    break;
                }
            default:
                Log.Error("Unhandled ESPHome message type ID: {TypeId}", typeId);
                break;
        }
    }
}

public static class BluetoothAdvertisementExtensions
{
    public static BluetoothLERawAdvertisement ToProto(this BluetoothLEAdvertisementReceivedEventArgs args)
    {
        using var ms = new MemoryStream();

        foreach (var section in args.Advertisement.DataSections)
        {
            byte dataType = section.DataType;
            using var reader = DataReader.FromBuffer(section.Data);
            byte[] dataBytes = new byte[section.Data.Length];
            reader.ReadBytes(dataBytes);

            // BLE advertisement section format: [Length] [Type] [Payload...]
            int totalSectionLength = dataBytes.Length + 1;
            if (ms.Position + totalSectionLength + 1 > 62) break; // Enforce proto fixed_array_size = 62 limit

            ms.WriteByte((byte)totalSectionLength);
            ms.WriteByte(dataType);
            ms.Write(dataBytes, 0, dataBytes.Length);
        }

        return new BluetoothLERawAdvertisement
        {
            Address = args.BluetoothAddress,
            Rssi = args.RawSignalStrengthInDBm,
            AddressType = (uint)args.BluetoothAddressType,
            Data = Google.Protobuf.ByteString.CopyFrom(ms.ToArray())
        };
    }
}