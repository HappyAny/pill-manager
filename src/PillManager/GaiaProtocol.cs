using System.Buffers.Binary;

namespace PillManager;

public record GaiaPacket(int Feature, int Command, int Type, byte[] Payload);

public static class GaiaProtocol
{
    public static byte[] Command(int feature, int command, params byte[] payload)
    {
        if (feature is < 0 or > 127 || command is < 0 or > 127 || payload.Length > 240)
            throw new ArgumentOutOfRangeException(nameof(feature));
        var result = new byte[4 + payload.Length];
        result[1] = 0x1D;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)((feature << 9) | command));
        payload.CopyTo(result, 4);
        return result;
    }

    public static GaiaPacket Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4 || raw[0] != 0 || raw[1] != 0x1D)
            throw new FormatException("无效的 GAIA 数据包。");
        int word = BinaryPrimitives.ReadUInt16BigEndian(raw[2..]);
        return new(word >> 9, word & 127, (word >> 7) & 3, raw[4..].ToArray());
    }

    public static Dictionary<int, int> Batteries(byte[] payload)
    {
        if (payload.Length == 0 || payload.Length % 2 != 0) throw new FormatException("电量回复不完整。");
        var values = new Dictionary<int, int>();
        for (int i = 0; i < payload.Length; i += 2)
            if (payload[i] is >= 0 and <= 3 && payload[i + 1] <= 100) values[payload[i]] = payload[i + 1];
        return values;
    }
}
