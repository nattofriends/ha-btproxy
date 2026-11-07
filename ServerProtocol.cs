using System.Net.Sockets;
using Google.Protobuf;
using Serilog;

namespace HABluetoothProxy;

internal static class ServerProtocol
{

    public static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0) return false;
            totalRead += read;
        }
        return true;
    }

    public static async Task<(int TypeId, byte[] Payload)?> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // 1. Read preamble byte (0x00)
        byte[] preambleBuf = new byte[1];
        if (!await ReadExactAsync(stream, preambleBuf, cancellationToken)) return null;

        if (preambleBuf[0] != 0x00)
        {
            Log.Error("Invalid ESPHome frame preamble received: 0x{Preamble:X2}", preambleBuf[0]);
            throw new InvalidDataException($"Invalid ESPHome frame preamble: 0x{preambleBuf[0]:X2}");
        }

        // 2. Read payload length varint
        int payloadLength = (int)await ReadVarintAsync(stream, cancellationToken);
        Log.Debug("Parsed payload length: {Length} bytes", payloadLength);

        // 3. Read message Type ID varint
        int typeId = (int)await ReadVarintAsync(stream, cancellationToken);
        Log.Debug("Parsed message TypeId: {TypeId}", typeId);

        // 4. Read exact Protobuf payload bytes
        byte[] payload = new byte[payloadLength];
        if (!await ReadExactAsync(stream, payload, cancellationToken)) return null;

        Log.Debug("Successfully read frame: TypeId={TypeId}, PayloadLen={PayloadLen}", typeId, payloadLength);
        return (typeId, payload);
    }

    public static async Task<ulong> ReadVarintAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        ulong result = 0;
        int shift = 0;
        byte[] buffer = new byte[1];
        while (true)
        {
            bool success = await ReadExactAsync(stream, buffer, cancellationToken);
            if (!success) throw new EndOfStreamException("Stream ended while reading varint.");
            byte b = buffer[0];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 64) throw new FormatException("Varint too long.");
        }
    }
    public static async Task SendFrameAsync(NetworkStream stream, int typeId, IMessage message, CancellationToken cancellationToken)
    {
        byte[] protoBytes = message.ToByteArray();
        byte[] typeVarint = WriteVarint((ulong)typeId);

        // The frame payload length is ONLY the serialized protobuf message bytes.
        // (The message type ID varint is sent separately between length and payload).
        int payloadLength = protoBytes.Length;
        byte[] lengthVarint = WriteVarint((ulong)payloadLength);

        Log.Debug("Sending frame TypeId {TypeId}, PayloadLen={PayloadLen}", typeId, payloadLength);

        await stream.WriteAsync(new byte[] { 0x00 }, cancellationToken);
        await stream.WriteAsync(lengthVarint, cancellationToken);
        await stream.WriteAsync(typeVarint, cancellationToken);
        await stream.WriteAsync(protoBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static byte[] WriteVarint(ulong value)
    {
        using var ms = new MemoryStream();
        while (value >= 0x80)
        {
            ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)(value & 0x7F));
        return ms.ToArray();
    }
}