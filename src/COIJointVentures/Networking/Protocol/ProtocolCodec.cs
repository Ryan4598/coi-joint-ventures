using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace COIJointVentures.Networking.Protocol;

internal static class ProtocolCodec
{
    public static byte[] WrapGameCommand(byte[] commandPayload)
    {
        return Wrap(ProtocolMessageType.GameCommand, commandPayload);
    }

    public static byte[] WrapJoinRequest(JoinRequest request)
    {
        return Wrap(ProtocolMessageType.JoinRequest, SerializeJson(request));
    }

    public static byte[] WrapJoinAccepted(JoinResponse response)
    {
        return Wrap(ProtocolMessageType.JoinAccepted, SerializeJson(response));
    }

    public static byte[] WrapJoinRejected(JoinResponse response)
    {
        return Wrap(ProtocolMessageType.JoinRejected, SerializeJson(response));
    }

    // steam supports up to 512KB per message natively, 256KB gives us headroom
    public const int MaxChunkSize = 256 * 1024;

    public static byte[] WrapSaveData(byte[] saveBytes)
    {
        return Wrap(ProtocolMessageType.SaveData, saveBytes);
    }

    // chunk header: [chunkIndex:4][totalChunks:4][totalSize:4][data...]
    public static byte[] WrapSaveChunk(int chunkIndex, int totalChunks, int totalSize, byte[] chunkData)
    {
        var header = new byte[12];
        Buffer.BlockCopy(BitConverter.GetBytes(chunkIndex), 0, header, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(totalChunks), 0, header, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, header, 8, 4);
        var payload = new byte[header.Length + chunkData.Length];
        Buffer.BlockCopy(header, 0, payload, 0, header.Length);
        Buffer.BlockCopy(chunkData, 0, payload, header.Length, chunkData.Length);
        return Wrap(ProtocolMessageType.SaveChunk, payload);
    }

    public static byte[] WrapSaveComplete()
    {
        return Wrap(ProtocolMessageType.SaveComplete, Array.Empty<byte>());
    }

    public static void DecodeSaveChunk(byte[] payload, out int chunkIndex, out int totalChunks, out int totalSize, out byte[] chunkData)
    {
        chunkIndex = BitConverter.ToInt32(payload, 0);
        totalChunks = BitConverter.ToInt32(payload, 4);
        totalSize = BitConverter.ToInt32(payload, 8);
        chunkData = new byte[payload.Length - 12];
        Buffer.BlockCopy(payload, 12, chunkData, 0, chunkData.Length);
    }

    public static byte[] WrapClientReady()
    {
        return Wrap(ProtocolMessageType.ClientReady, Array.Empty<byte>());
    }

    public static byte[] WrapJoinSyncBegin(string joiningPlayerName)
    {
        return Wrap(ProtocolMessageType.JoinSyncBegin, System.Text.Encoding.UTF8.GetBytes(joiningPlayerName));
    }

    public static byte[] WrapJoinSyncEnd()
    {
        return Wrap(ProtocolMessageType.JoinSyncEnd, Array.Empty<byte>());
    }

    public static byte[] WrapChatMessage(ChatMessagePayload msg)
    {
        return Wrap(ProtocolMessageType.ChatMessage, SerializeJson(msg));
    }

    public static ChatMessagePayload DecodeChatMessage(byte[] payload)
    {
        return DeserializeJson<ChatMessagePayload>(payload);
    }

    public static byte[] WrapPlayerList(System.Collections.Generic.List<Session.PlayerInfo> players)
    {
        // simple text format: name\tcolorIndex\tisPending per line
        var sb = new System.Text.StringBuilder();
        foreach (var p in players)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p.Name).Append('\t').Append(p.ColorIndex).Append('\t').Append(p.IsPending ? '1' : '0');
        }
        return Wrap(ProtocolMessageType.PlayerList, System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public static System.Collections.Generic.List<Session.PlayerInfo> DecodePlayerList(byte[] payload)
    {
        var list = new System.Collections.Generic.List<Session.PlayerInfo>();
        var text = System.Text.Encoding.UTF8.GetString(payload);
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            list.Add(new Session.PlayerInfo
            {
                Name = parts[0],
                ColorIndex = int.TryParse(parts[1], out var c) ? c : 0,
                IsPending = parts[2] == "1"
            });
        }
        return list;
    }

    public static byte[] WrapWaypoint(WaypointPayload waypoint)
    {
        return Wrap(ProtocolMessageType.Waypoint, SerializeJson(waypoint));
    }

    public static WaypointPayload DecodeWaypoint(byte[] payload)
    {
        return DeserializeJson<WaypointPayload>(payload);
    }

    public static string DecodeJoinSyncBegin(byte[] payload)
    {
        return System.Text.Encoding.UTF8.GetString(payload);
    }

    public static ProtocolMessageType ReadType(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            throw new ArgumentException("Empty protocol message.");
        }

        return (ProtocolMessageType)data[0];
    }

    public static byte[] ReadPayload(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = new byte[data.Length - 1];
        Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
        return payload;
    }

    public static JoinRequest DecodeJoinRequest(byte[] payload)
    {
        return DeserializeJson<JoinRequest>(payload);
    }

    public static JoinResponse DecodeJoinResponse(byte[] payload)
    {
        return DeserializeJson<JoinResponse>(payload);
    }

    private static byte[] Wrap(ProtocolMessageType type, byte[] payload)
    {
        var result = new byte[1 + payload.Length];
        result[0] = (byte)type;
        Buffer.BlockCopy(payload, 0, result, 1, payload.Length);
        return result;
    }

    private static byte[] SerializeJson<T>(T obj) where T : class
    {
        using (var stream = new MemoryStream())
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            serializer.WriteObject(stream, obj);
            return stream.ToArray();
        }
    }

    private static T DeserializeJson<T>(byte[] payload) where T : class
    {
        using (var stream = new MemoryStream(payload))
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            var result = serializer.ReadObject(stream) as T;
            return result ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
        }
    }
}
