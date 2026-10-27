using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PillManager;

public record DiscoveredPill(ulong Address, string Name, short Rssi, BluetoothAddressType AddressType)
{
    public string Label => $"{Name}  ·  {Rssi} dBm  ·  …{Address & 0xFFFF:X4}";
    public override string ToString() => Label;
}

public sealed class BleTransport : IAsyncDisposable
{
    public static readonly Guid GaiaService = Guid.Parse("00001100-d102-11e1-9b23-00025b00a5a5");
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattSession? session;
    private GattCharacteristic? tx, rx;
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private readonly object pendingLock = new();
    private (int feature, int command, TaskCompletionSource<GaiaPacket> result)? pending;
    private bool ready;
    public bool Connected => ready && device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public event Action<string>? Log;
    public event Action? Disconnected;
    public event Action<GaiaPacket>? Notification;

    public static async Task<List<DiscoveredPill>> ScanAsync(CancellationToken token = default)
    {
        var found = new ConcurrentDictionary<ulong, DiscoveredPill>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            string name = e.Advertisement.LocalName;
            if (name.Contains("PILL", StringComparison.OrdinalIgnoreCase))
                found[e.BluetoothAddress] = new(e.BluetoothAddress, name, e.RawSignalStrengthInDBm, e.BluetoothAddressType);
        };
        watcher.Start();
        try { await Task.Delay(7000, token); }
        finally { watcher.Stop(); }
        if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
            throw new IOException("蓝牙扫描失败。请检查 Windows 蓝牙是否开启。");
        return found.Values.OrderByDescending(x => x.Rssi).ToList();
    }

    public async Task ConnectAsync(DiscoveredPill selected, CancellationToken token = default)
    {
        await DisposeAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(selected.Address, selected.AddressType).AsTask(timeout.Token)
                ?? throw new IOException("Windows 无法打开耳机。请关闭手机上的 MOONDROP App 后重试。");
            device.ConnectionStatusChanged += OnConnectionStatus;
            var result = await device.GetGattServicesForUuidAsync(GaiaService, BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count != 1)
                throw new IOException($"未找到可用的 PILL 管理接口：{result.Status}。");
            service = result.Services[0];
            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            if (chars.Status != GattCommunicationStatus.Success) throw new IOException("读取管理接口失败。");
            tx = chars.Characteristics.FirstOrDefault(c => c.Uuid == Guid.Parse("00001101-d102-11e1-9b23-00025b00a5a5"));
            rx = chars.Characteristics.FirstOrDefault(c => c.Uuid == Guid.Parse("00001102-d102-11e1-9b23-00025b00a5a5"));
            if (tx == null || rx == null) throw new IOException("耳机接口不完整。");
            rx.ValueChanged += OnValue;
            var status = await rx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(timeout.Token);
            if (status != GattCommunicationStatus.Success) throw new IOException($"无法接收耳机回复：{status}。");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(timeout.Token);
            if (session.CanMaintainConnection) session.MaintainConnection = true;
            ready = true;
            Log?.Invoke("管理连接已建立。");
        }
        catch { await DisposeAsync(); throw; }
    }

    private void OnConnectionStatus(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            ready = false;
            lock (pendingLock) pending?.result.TrySetException(new IOException("耳机连接已断开。"));
            Disconnected?.Invoke();
        }
    }

    private void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] bytes = args.CharacteristicValue.ToArray();
        Log?.Invoke("RX " + Convert.ToHexString(bytes));
        GaiaPacket packet;
        try { packet = GaiaProtocol.Parse(bytes); }
        catch (FormatException) { return; }
        if (packet.Type == 1) { Notification?.Invoke(packet); return; }
        lock (pendingLock)
        {
            if (pending is { } p && p.feature == packet.Feature && p.command == packet.Command && packet.Type is 2 or 3)
                p.result.TrySetResult(packet);
        }
    }

    public async Task<byte[]> QueryAsync(int feature, int command, byte[]? payload = null, CancellationToken token = default)
    {
        await requestLock.WaitAsync(token);
        try
        {
            var result = new TaskCompletionSource<GaiaPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingLock) pending = (feature, command, result);
            await WritePacketAsync(feature, command, payload ?? [], token);
            GaiaPacket packet;
            try { packet = await result.Task.WaitAsync(TimeSpan.FromSeconds(4), token); }
            catch (TimeoutException)
            {
                // GAIA has no transaction ID. A timed-out session must not match a late reply to a new request.
                ready = false;
                throw new IOException("耳机未及时回复，请断开管理连接后重新连接。");
            }
            if (packet.Type == 3) throw new IOException($"耳机拒绝操作，错误码 {Convert.ToHexString(packet.Payload)}。");
            return packet.Payload;
        }
        finally { lock (pendingLock) pending = null; requestLock.Release(); }
    }

    public async Task SendAsync(int feature, int command, byte[] payload, CancellationToken token = default)
    {
        await requestLock.WaitAsync(token);
        try { await WritePacketAsync(feature, command, payload, token); await Task.Delay(220, token); }
        finally { requestLock.Release(); }
    }

    private async Task WritePacketAsync(int feature, int command, byte[] payload, CancellationToken token)
    {
        if (!ready || tx == null) throw new IOException("请先连接耳机。");
        byte[] data = GaiaProtocol.Command(feature, command, payload);
        if (data.Length > 20) throw new ArgumentException("PILL 管理报文超过 20 字节，必须分段发送。");
        Log?.Invoke("TX " + Convert.ToHexString(data));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var status = await tx.WriteValueAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(deadline.Token);
        if (status != GattCommunicationStatus.Success) throw new IOException($"蓝牙写入失败：{status}。");
    }

    public ValueTask DisposeAsync()
    {
        ready = false;
        lock (pendingLock) { pending?.result.TrySetException(new IOException("管理连接已关闭。")); pending = null; }
        if (rx != null) rx.ValueChanged -= OnValue;
        if (session != null) { session.MaintainConnection = false; session.Dispose(); session = null; }
        service?.Dispose(); service = null;
        if (device != null) { device.ConnectionStatusChanged -= OnConnectionStatus; device.Dispose(); device = null; }
        tx = rx = null;
        return ValueTask.CompletedTask;
    }
}
