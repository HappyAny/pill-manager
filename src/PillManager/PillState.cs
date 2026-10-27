using System.Buffers.Binary;
using System.Text.Json;

namespace PillManager;

public static class VoiceVolumeCodec
{
    // Firmware 1.6.0 uses 16 intervals. SET truncates percent to a step;
    // GET truncates that step back to percent. Use ceiling only for SET.
    public static int Step(int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return (int)Math.Round(percent * 16.0 / 100);
    }
    public static byte ReadPercent(int step) => step is >= 0 and <= 16 ? (byte)(step * 100 / 16) : throw new ArgumentOutOfRangeException(nameof(step));
    public static byte WritePercent(int step) => step is >= 0 and <= 16 ? (byte)((step * 100 + 15) / 16) : throw new ArgumentOutOfRangeException(nameof(step));
}

public sealed class EqBand
{
    public int Index { get; set; }
    public int Frequency { get; set; }
    public double Q { get; set; }
    public double Gain { get; set; }
    public byte Filter { get; set; }
}

public sealed class EqConfiguration
{
    public double PreGain { get; set; }
    public List<EqBand> Bands { get; set; } = [];

    public static EqConfiguration Decode(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0 || data[1] > 6 || data.Length != 4 + 7 * (data[1] + 1))
            throw new FormatException("当前固件的 EQ 格式尚未适配，已停止写入。");
        var eq = new EqConfiguration { PreGain = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(2)) / 60.0 };
        for (int i = 0; i <= data[1]; i++)
        {
            var band = data.AsSpan(4 + i * 7, 7);
            eq.Bands.Add(new EqBand { Index = i, Frequency = BinaryPrimitives.ReadUInt16BigEndian(band),
                Q = BinaryPrimitives.ReadUInt16BigEndian(band[2..]) / 4096.0, Filter = band[4],
                Gain = BinaryPrimitives.ReadInt16BigEndian(band[5..]) / 60.0 });
        }
        return eq;
    }

    public byte[] Encode()
    {
        Validate();
        byte[] data = new byte[4 + Bands.Count * 7];
        data[1] = (byte)(Bands.Count - 1);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(2), (short)Math.Round(PreGain * 60));
        for (int i = 0; i < Bands.Count; i++)
        {
            var b = Bands[i]; var dest = data.AsSpan(4 + i * 7, 7);
            BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)b.Frequency);
            BinaryPrimitives.WriteUInt16BigEndian(dest[2..], (ushort)Math.Round(b.Q * 4096));
            dest[4] = b.Filter;
            BinaryPrimitives.WriteInt16BigEndian(dest[5..], (short)Math.Round(b.Gain * 60));
        }
        return data;
    }

    public byte[] EncodeBand(int index)
    {
        byte[] full = Encode();
        if (index < 0 || index >= Bands.Count) throw new ArgumentOutOfRangeException(nameof(index));
        byte[] part = new byte[11];
        part[0] = part[1] = (byte)index;
        full.AsSpan(2, 2).CopyTo(part.AsSpan(2));
        full.AsSpan(4 + index * 7, 7).CopyTo(part.AsSpan(4));
        return part;
    }

    public void Validate()
    {
        if (Bands == null || Bands.Count is < 1 or > 7 || !double.IsFinite(PreGain) || PreGain is < -24 or > 0)
            throw new ArgumentException("前级增益须为 -24～0 dB，EQ 须为 1～7 段。");
        for (int i = 0; i < Bands.Count; i++)
        {
            var b = Bands[i];
            if (b == null || b.Index != i || b.Frequency is < 20 or > 20000 || !double.IsFinite(b.Q) || b.Q is < 0.1 or > 10 ||
                !double.IsFinite(b.Gain) || b.Gain is < -12 or > 12 || b.Filter > 13)
                throw new ArgumentException($"第 {i + 1} 段参数无效：频率 20～20000 Hz，Q 0.1～10，增益 -12～12 dB。");
        }
    }

    public EqConfiguration Copy() => Decode(Encode());
}

public sealed class PillState
{
    public int FormatVersion { get; set; } = 1;
    public string Model { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string DeviceAddress { get; set; } = "";
    public DateTimeOffset ReadAt { get; set; }
    public Dictionary<int, int> Batteries { get; set; } = [];
    public Dictionary<int, int> Features { get; set; } = [];
    public byte[] Presets { get; set; } = [];
    public byte Preset { get; set; }
    public EqConfiguration? Eq { get; set; }
    public byte Gain { get; set; }
    public bool Multipoint { get; set; }
    public byte MultipointTimeout { get; set; }
    public string[] ConnectedDevices { get; set; } = [];
    public byte[] Touch { get; set; } = [];
    public byte[] Voice { get; set; } = [];
    public PillState Copy() => JsonSerializer.Deserialize<PillState>(JsonSerializer.Serialize(this))!;
}

public static class LocalFiles
{
    private static string? fallbackRoot;
    public static string Root => Environment.GetEnvironmentVariable("PILL_MANAGER_DATA_DIR") ?? fallbackRoot ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PillManager");
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    public static string Backup(PillState state, string reason)
    {
        string Save()
        {
            string dir = Path.Combine(Root, "backups"); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}-{reason}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
            return path;
        }
        try { return Save(); }
        catch (UnauthorizedAccessException) when (fallbackRoot == null && Environment.GetEnvironmentVariable("PILL_MANAGER_DATA_DIR") == null)
        {
            fallbackRoot = Path.Combine(AppContext.BaseDirectory, "PillManager-data");
            return Save();
        }
    }

    public static PillState Load(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new FormatException("配置文件过大。");
        var state = JsonSerializer.Deserialize<PillState>(File.ReadAllText(path)) ?? throw new FormatException("配置文件为空。");
        if (state.FormatVersion != 1 || state.Model != "MOONDROP PILL") throw new FormatException("这不是 PILL 桌面助手的配置文件。");
        state.Eq?.Validate();
        if (state.Gain > 2 || state.Touch == null || state.Touch.Length != 12 || state.Touch[0] > 1 ||
            state.Touch.Skip(1).Any(x => (x & 15) > 5 || (x >> 4) is < 1 or > 3) ||
            state.Voice == null || state.Voice.Length != 3 || state.Voice[0] > 1 || state.Voice[1] > 100)
            throw new FormatException("配置中的设备参数无效。");
        return state;
    }
}
