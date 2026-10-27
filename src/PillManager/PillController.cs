using System.Text;

namespace PillManager;

public sealed class PillController : IAsyncDisposable
{
    private readonly BleTransport transport = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    public PillState? State { get; private set; }
    public bool Connected => transport.Connected;
    public string? LastBackup { get; private set; }
    public event Action<string>? Log;
    public event Action? Disconnected;

    public PillController()
    {
        transport.Log += line => Log?.Invoke(line);
        transport.Disconnected += () => Disconnected?.Invoke();
    }

    public async Task ConnectAsync(DiscoveredPill selected, CancellationToken token = default)
    {
        await operation.WaitAsync(token);
        try
        {
            State = null;
            await transport.ConnectAsync(selected, token);
            string model = Encoding.UTF8.GetString(await transport.QueryAsync(0, 4, token: token));
            if (model != "MOONDROP PILL") throw new IOException($"设备回复型号为 {model}，未启用 PILL 设置。");
            State = new() { Model = model, DeviceAddress = selected.Address.ToString("X12") };
            State.Firmware = Encoding.UTF8.GetString(await transport.QueryAsync(0, 5, token: token));
            var features = await transport.QueryAsync(0, 1, token: token);
            if (features.Length < 3 || features.Length % 2 != 1 || features[0] != 0)
                throw new IOException("设备能力列表格式未知。");
            for (int i = 1; i < features.Length; i += 2) State.Features[features[i]] = features[i + 1];
            await RefreshCoreAsync(token);
            LastBackup = LocalFiles.Backup(State, "connected");
        }
        catch { State = null; await transport.DisposeAsync(); throw; }
        finally { operation.Release(); }
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await operation.WaitAsync(token);
        try { await RefreshCoreAsync(token); }
        finally { operation.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken token)
    {
        if (State == null) throw new IOException("请先连接耳机。");
        var next = State.Copy();
        if (next.Features.ContainsKey(13)) next.Batteries = GaiaProtocol.Batteries(await transport.QueryAsync(13, 1, [1, 2], token));
        if (next.Features.ContainsKey(5))
        {
            var presets = await transport.QueryAsync(5, 1, token: token);
            if (presets.Length < 1 || presets.Length != presets[0] + 1) throw new FormatException("EQ 预设列表不完整。");
            next.Presets = presets[1..];
            next.Preset = One(await transport.QueryAsync(5, 2, token: token));
            int count = One(await transport.QueryAsync(5, 4, token: token));
            if (count is >= 1 and <= 7) next.Eq = EqConfiguration.Decode(await transport.QueryAsync(5, 5, [0, (byte)(count - 1)], token));
        }
        if (next.Features.ContainsKey(15)) next.Gain = One(await transport.QueryAsync(15, 1, token: token));
        if (next.Features.ContainsKey(20))
        {
            next.Multipoint = One(await transport.QueryAsync(20, 1, token: token)) == 1;
            next.MultipointTimeout = One(await transport.QueryAsync(20, 3, token: token));
            var peers = new List<string>();
            foreach (int cmd in new[] {5, 6})
            {
                byte[] peer = await transport.QueryAsync(20, cmd, token: token);
                if (peer.Length > 7) peers.Add(Encoding.UTF8.GetString(peer[7..]).TrimEnd('\0'));
            }
            next.ConnectedDevices = peers.Distinct().ToArray();
        }
        if (next.Features.ContainsKey(1)) next.Touch = await transport.QueryAsync(1, 2, token: token);
        if (next.Features.ContainsKey(14)) next.Voice = await transport.QueryAsync(14, 1, token: token);
        next.ReadAt = DateTimeOffset.Now;
        State = next;
    }

    public async Task RefreshBatteryAsync(CancellationToken token = default)
    {
        if (!await operation.WaitAsync(0, token)) return;
        try
        {
            if (State != null && Connected)
            {
                var batteries = GaiaProtocol.Batteries(await transport.QueryAsync(13, 1, [1, 2], token));
                State.Batteries = batteries;
                State.ReadAt = DateTimeOffset.Now;
            }
        }
        finally { operation.Release(); }
    }

    public Task SetPresetAsync(byte value) => ChangeAsync("preset", 5, async () =>
    {
        if (!State!.Presets.Contains(value)) throw new ArgumentException("该预设未由耳机报告。");
        await transport.SendAsync(5, 3, [value]);
        Equal([value], await transport.QueryAsync(5, 2));
        State.Preset = value;
    });

    public Task SetGainAsync(byte value) => ChangeAsync("gain", 15, async () =>
    {
        if (value > 2) throw new ArgumentException("增益档位无效。");
        await transport.SendAsync(15, 2, [value]);
        Equal([value], await transport.QueryAsync(15, 1));
        State!.Gain = value;
    });

    public Task SetMultipointAsync(bool value) => ChangeAsync("multipoint", 20, async () =>
    {
        await transport.SendAsync(20, 2, [(byte)(value ? 1 : 0)]);
        Equal([(byte)(value ? 1 : 0)], await transport.QueryAsync(20, 1));
        State!.Multipoint = value;
    });

    public Task SetEqAsync(EqConfiguration eq) => ChangeAsync("eq", 5, async () =>
    {
        if (State!.Eq == null || eq.Bands.Count != State.Eq.Bands.Count) throw new ArgumentException("EQ 频段数与耳机不一致。");
        if (eq.Bands.Any(b => b.Filter != 0)) throw new ArgumentException("此版本只支持 PILL 已验证的原生滤波器类型 0。");
        byte[] packet = eq.Encode();
        var previous = State.Eq.Copy(); byte previousPreset = State.Preset;
        try
        {
            await WriteEqCoreAsync(eq);
            await transport.SendAsync(5, 3, [63]);
            Equal([63], await transport.QueryAsync(5, 2));
            State.Eq = EqConfiguration.Decode(packet); State.Preset = 63;
        }
        catch
        {
            if (Connected)
                try
                {
                    await WriteEqCoreAsync(previous);
                    await transport.SendAsync(5, 3, [previousPreset]);
                    Equal([previousPreset], await transport.QueryAsync(5, 2));
                    State.Eq = previous; State.Preset = previousPreset;
                    Log?.Invoke("EQ 操作失败，原 EQ 和预设已恢复并读回验证。");
                }
                catch (Exception restoreError) { Log?.Invoke("自动恢复未完成：" + restoreError.Message); }
            throw;
        }
    });

    private async Task WriteEqCoreAsync(EqConfiguration eq)
    {
        // PILL silently truncates long ATT writes. One band fits in a 20-byte ATT value.
        for (int i = 0; i < eq.Bands.Count; i++) await transport.SendAsync(5, 6, eq.EncodeBand(i));
        Equal(eq.Encode(), await transport.QueryAsync(5, 5, [0, (byte)(eq.Bands.Count - 1)]));
    }

    public Task SetTouchAsync(byte[] value) => ChangeAsync("touch", 1, async () =>
    {
        if (value.Length != 12 || State!.Touch.Length != 12 || value[0] > 1 ||
            value.Skip(1).Any(x => (x & 15) > 5 || (x >> 4) is < 1 or > 3)) throw new ArgumentException("触控配置无效。");
        if (!value.AsSpan(10).SequenceEqual(State.Touch.AsSpan(10))) throw new ArgumentException("此版本保留耳机的模式切换动作。");
        await transport.SendAsync(1, 3, value);
        Equal(value, await transport.QueryAsync(1, 2));
        State.Touch = (byte[])value.Clone();
    });

    public Task SetVoiceAsync(bool enabled, byte volume) => ChangeAsync("voice", 14, async () =>
    {
        if (volume > 100 || State!.Voice.Length != 3) throw new ArgumentException("提示音参数无效。");
        byte[] current = await transport.QueryAsync(14, 1);
        if (current.Length != 3) throw new FormatException("提示音格式未知。");
        int step = VoiceVolumeCodec.Step(volume);
        byte[] value = [(byte)(enabled ? 1 : 0), VoiceVolumeCodec.WritePercent(step), current[2]];
        await transport.SendAsync(14, 2, value);
        byte[] expected = [(byte)(enabled ? 1 : 0), VoiceVolumeCodec.ReadPercent(step), current[2]];
        byte[] actual = await transport.QueryAsync(14, 1);
        Equal(expected, actual);
        State.Voice = actual;
    });

    private async Task ChangeAsync(string reason, int feature, Func<Task> action)
    {
        await operation.WaitAsync();
        try
        {
            if (!Connected || State == null) throw new IOException("请先连接耳机。");
            if (State.Firmware != "1.6.0") throw new IOException("此固件尚未验证写入，当前仅支持读取。已验证版本为 1.6.0。");
            if (!State.Features.ContainsKey(feature)) throw new IOException("耳机未声明支持此功能。");
            await RefreshCoreAsync(CancellationToken.None);
            LastBackup = LocalFiles.Backup(State, reason);
            await action();
            State.ReadAt = DateTimeOffset.Now;
            Log?.Invoke("设置已写入，读回验证通过。备份：" + LastBackup);
        }
        catch
        {
            Log?.Invoke("操作未完成。可能已部分写入，请刷新或从备份恢复。");
            throw;
        }
        finally { operation.Release(); }
    }

    private static byte One(byte[] payload) => payload.Length == 1 ? payload[0] : throw new FormatException("设备回复长度不符。");
    private static void Equal(byte[] expected, byte[] actual)
    {
        if (!expected.SequenceEqual(actual)) throw new IOException("耳机读回的设置与请求不符，未确认成功。请刷新查看实际值。");
    }
    public async ValueTask DisposeAsync() { await transport.DisposeAsync(); State = null; }
}
