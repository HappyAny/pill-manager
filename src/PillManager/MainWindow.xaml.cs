using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PillManager;

public record Choice(int Value, string Label) { public override string ToString() => Label; }
public sealed class TouchRow
{
    public string Function { get; set; } = "";
    public int Ear { get; set; }
    public int Gesture { get; set; }
}

public partial class MainWindow : Window
{
    private readonly PillController controller = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<string> log = new();
    private EqConfiguration? editorEq;
    private List<TouchRow> touches = [];
    private PillState? imported;
    private bool busy;
    private bool closing;
    private static readonly Choice[] Ears = [new(16,"左耳"), new(32,"右耳"), new(48,"任一耳")];
    private static readonly Choice[] Gestures = [new(0,"未分配"), new(1,"单击"), new(2,"双击"), new(3,"三击"), new(4,"长按 1 秒"), new(5,"长按 3 秒")];
    private static readonly string[] TouchFunctions = ["播放 / 暂停", "上一曲", "下一曲", "音量增加", "音量减小", "接听 / 挂断", "拒接来电", "唤醒语音助手", "取消语音助手"];

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme("System");
        SystemEvents.UserPreferenceChanged += ThemePreferenceChanged;
        SourceInitialized += (_, _) => ApplyTheme(selectedTheme);
        GainChoice.ItemsSource = new Choice[] {new(0,"设备档位 0"), new(1,"设备档位 1"), new(2,"设备档位 2")};
        GainChoice.SelectedValuePath = nameof(Choice.Value);
        PresetChoice.SelectedValuePath = nameof(Choice.Value);
        TouchGrid.Columns.Add(new DataGridTextColumn { Header = "功能", Binding = new Binding(nameof(TouchRow.Function)), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        TouchGrid.Columns.Add(new DataGridComboBoxColumn { Header = "耳机", ItemsSource = Ears, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), SelectedValueBinding = new Binding(nameof(TouchRow.Ear)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        TouchGrid.Columns.Add(new DataGridComboBoxColumn { Header = "手势", ItemsSource = Gestures, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), SelectedValueBinding = new Binding(nameof(TouchRow.Gesture)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        controller.Log += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        controller.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            ConnectionText.Text = "管理连接已断开，请扫描或重新连接。";
            LeftBattery.Text = RightBattery.Text = "—";
            LeftGauge.Percent = RightGauge.Percent = double.NaN;
            SetAvailable();
        });
        timer.Tick += async (_, _) =>
        {
            if (busy || !controller.Connected) return;
            try { await controller.RefreshBatteryAsync(lifetime.Token); RenderBattery(); }
            catch (Exception ex) { StatusText.Text = ex.Message; SetAvailable(); }
        };
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--preview-ui")) { await CaptureUiPreviewsAsync(); return; }
            RefreshAudio(); timer.Start();
            if (Environment.GetCommandLineArgs().Contains("--connect")) await Run(async () =>
            {
                await Scan();
                if (Devices.Items.Count == 1 && Devices.SelectedItem is DiscoveredPill selected)
                { await controller.ConnectAsync(selected, lifetime.Token); RenderState(); }
            }, "已读取耳机设置。");
        };
        Closing += (_, e) =>
        {
            if (busy) { e.Cancel = true; StatusText.Text = "正在完成操作，请稍后关闭窗口。"; return; }
            closing = true; lifetime.Cancel(); timer.Stop();
            SystemEvents.UserPreferenceChanged -= ThemePreferenceChanged;
            controller.DisposeAsync().GetAwaiter().GetResult();
        };
    }

    private void AppendLog(string line)
    {
        if (closing) return;
        // A peer-list reply contains host names and MAC addresses. Keep it out of the diagnostic log.
        if (line.StartsWith("RX 001D2905") || line.StartsWith("RX 001D2906")) line = "RX 双设备列表（标识已省略）";
        log.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
        while (log.Count > 250) log.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, log); LogBox.ScrollToEnd();
    }

    private void SetAvailable()
    {
        bool active = controller.Connected && !busy;
        bool write = active && controller.State?.Firmware == "1.6.0";
        EqPanel.IsEnabled = TouchPanel.IsEnabled = SettingsPanel.IsEnabled = write;
        ScanButton.IsEnabled = !busy && !controller.Connected;
        ConnectButton.IsEnabled = !busy;
        ConnectButton.Content = controller.Connected ? "断开连接" : "连接设备";
        DeviceStatus.Text = controller.Connected ? "已连接" : "未连接";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, controller.Connected ? "AccentBrush" : "SecondaryBrush");
        Devices.IsEnabled = !busy && !controller.Connected;
        RestoreButton.IsEnabled = write && imported != null;
    }

    private async Task Run(Func<Task> action, string success)
    {
        if (busy) return;
        busy = true; SetAvailable(); StatusText.Text = "正在处理，请稍候…";
        try { await action(); StatusText.Text = success; }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消或连接超时。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; AppendLog("错误：" + ex.Message); }
        finally { busy = false; SetAvailable(); }
    }

    private async Task Scan()
    {
        ConnectionText.Text = "正在扫描 PILL（约 7 秒）…";
        var found = await BleTransport.ScanAsync(lifetime.Token);
        Devices.ItemsSource = found; Devices.SelectedIndex = found.Count > 0 ? 0 : -1;
        ConnectionText.Text = found.Count == 0 ? "未发现 PILL。请取出耳机、靠近电脑，并关闭手机上的 MOONDROP App 后重试。" : $"发现 {found.Count} 台 PILL，请选择后连接管理。";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Run(Scan, "扫描完成。");
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        bool disconnect = controller.Connected;
        await Run(async () =>
        {
            if (disconnect)
            {
                await controller.DisposeAsync(); LeftBattery.Text = RightBattery.Text = FirmwareText.Text = "—";
                LeftGauge.Percent = RightGauge.Percent = double.NaN; UpdatedText.Text = "等待连接";
                ConnectionText.Text = "管理连接已断开，音频连接由 Windows 管理。";
            }
            else
            {
                if (Devices.SelectedItem is not DiscoveredPill selected) throw new InvalidOperationException("请先扫描并选择耳机。");
                await controller.ConnectAsync(selected, lifetime.Token); RenderState(); RefreshAudio();
            }
        }, disconnect ? "管理连接已关闭。" : "设置已读取，原配置已备份。");
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(async () => { await controller.RefreshAsync(lifetime.Token); RenderState(); }, "已从耳机重新读取。");

    private void RenderBattery()
    {
        if (controller.State is not { } state) return;
        RenderBatteryValues(state);
    }

    private void RenderBatteryValues(PillState state)
    {
        LeftBattery.Text = state.Batteries.TryGetValue(1, out int l) ? $"{l}%" : "未上报";
        RightBattery.Text = state.Batteries.TryGetValue(2, out int r) ? $"{r}%" : "未上报";
        LeftGauge.Percent = state.Batteries.TryGetValue(1, out l) ? l : double.NaN;
        RightGauge.Percent = state.Batteries.TryGetValue(2, out r) ? r : double.NaN;
        UpdatedText.Text = $"更新于 {state.ReadAt.ToLocalTime():HH:mm:ss}";
    }

    private void RenderState(PillState? preview = null)
    {
        if ((preview ?? controller.State) is not { } state) return;
        RenderBatteryValues(state); FirmwareText.Text = state.Firmware;
        ConnectionText.Text = state.Firmware == "1.6.0" ? "设置已同步 · 修改后点击应用" : $"固件 {state.Firmware} 尚未验证写入，当前仅供读取";
        PresetChoice.ItemsSource = state.Presets.Select(x => new Choice(x, x switch {0 => "EQ 关闭", 63 => "自定义 EQ", _ => $"设备预设 {x}"})).ToArray();
        PresetChoice.SelectedValue = (int)state.Preset;
        editorEq = state.Eq?.Copy(); EqGrid.ItemsSource = editorEq?.Bands;
        PreGain.Text = editorEq?.PreGain.ToString("F2", CultureInfo.InvariantCulture) ?? "";
        GainChoice.SelectedValue = (int)state.Gain;
        touches = [];
        if (state.Touch.Length == 12)
            for (int i = 0; i < 9; i++) touches.Add(new() { Function = TouchFunctions[i], Ear = state.Touch[i + 1] & 0xF0, Gesture = state.Touch[i + 1] & 15 });
        TouchGrid.ItemsSource = touches; TouchEnabled.IsChecked = state.Touch.Length > 0 && state.Touch[0] == 1;
        MultipointEnabled.IsChecked = state.Multipoint;
        PeersText.Text = state.ConnectedDevices.Length > 0 ? "耳机报告的已连接设备：" + string.Join("、", state.ConnectedDevices) : "耳机未返回设备名称。";
        if (state.Voice.Length == 3) { VoiceEnabled.IsChecked = state.Voice[0] == 1; VoiceVolume.Value = VoiceVolumeCodec.Step(state.Voice[1]); VoiceReadback.Text = $"耳机读回音量：{state.Voice[1]}%"; }
        SetAvailable();
    }

    private async void PresetApply_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (PresetChoice.SelectedItem is not Choice choice) throw new ArgumentException("请选择预设。");
        await controller.SetPresetAsync((byte)choice.Value); RenderState();
    }, "预设已切换，读回一致。");
    private async void GainApply_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (GainChoice.SelectedItem is not Choice choice) throw new ArgumentException("请选择增益档位。");
        await controller.SetGainAsync((byte)choice.Value); RenderState();
    }, "增益已设置，读回一致。");
    private async void EqApply_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        EqGrid.CommitEdit(DataGridEditingUnit.Cell, true); EqGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (editorEq == null || !double.TryParse(PreGain.Text, CultureInfo.InvariantCulture, out double value)) throw new ArgumentException("请填写有效的前级增益。");
        if (HasValidationError(EqGrid)) throw new ArgumentException("请修正 EQ 表格中的无效输入。");
        editorEq.PreGain = value; await controller.SetEqAsync(editorEq); RenderState();
    }, "自定义 EQ 已应用，所有参数读回一致。");

    private static bool HasValidationError(DependencyObject obj)
    {
        if (Validation.GetHasError(obj)) return true;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj);
        for (int i = 0; i < count; i++) if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(obj, i))) return true;
        return false;
    }

    private async void TouchApply_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        TouchGrid.CommitEdit(DataGridEditingUnit.Cell, true); TouchGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (controller.State?.Touch.Length != 12 || touches.Count != 9) throw new IOException("触控配置尚未读取。");
        byte[] value = (byte[])controller.State.Touch.Clone(); value[0] = (byte)(TouchEnabled.IsChecked == true ? 1 : 0);
        for (int i = 0; i < touches.Count; i++) value[i + 1] = (byte)(touches[i].Ear | touches[i].Gesture);
        for (int i = 0; i < 5; i++) for (int j = i + 1; j < 5; j++)
            if (touches[i].Gesture != 0 && touches[i].Gesture == touches[j].Gesture && (touches[i].Ear & touches[j].Ear) != 0)
                throw new ArgumentException($"“{touches[i].Function}”与“{touches[j].Function}”使用了相同手势，请调整。");
        await controller.SetTouchAsync(value); RenderState();
    }, "触控设置已写入，读回一致。");
    private async void MultipointApply_Click(object sender, RoutedEventArgs e) => await Run(async () => { await controller.SetMultipointAsync(MultipointEnabled.IsChecked == true); RenderState(); }, "双设备设置已写入，读回一致。");
    private async void VoiceApply_Click(object sender, RoutedEventArgs e) => await Run(async () => { await controller.SetVoiceAsync(VoiceEnabled.IsChecked == true, VoiceVolumeCodec.ReadPercent((int)VoiceVolume.Value)); RenderState(); }, "提示音设置已写入，读回一致。");

    private void RefreshAudio()
    {
        try
        {
            var endpoints = WindowsAudio.List(); AudioDevices.ItemsSource = endpoints;
            AudioDevices.SelectedIndex = endpoints.FindIndex(x => !x.IsMicrophone);
            if (AudioDevices.SelectedIndex < 0 && endpoints.Count > 0) AudioDevices.SelectedIndex = 0;
            AudioStatus.Text = endpoints.Count == 0 ? "请先在 Windows 蓝牙设置中连接 PILL 音频。" : "仅调整所选 PILL 设备。音频连接由 Windows 管理。";
        }
        catch (Exception ex) { AudioStatus.Text = ex.Message; }
    }
    private void AudioRefresh_Click(object sender, RoutedEventArgs e) => RefreshAudio();
    private void AudioDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (AudioDevices.SelectedItem is AudioEndpoint endpoint) { AudioVolume.Value = endpoint.Volume; AudioMute.IsChecked = endpoint.Muted; } }
    private void AudioApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AudioDevices.SelectedItem is not AudioEndpoint endpoint) throw new IOException("请选择活动的 PILL 音频设备。");
            WindowsAudio.Set(endpoint.Id, (int)AudioVolume.Value, AudioMute.IsChecked == true); StatusText.Text = "Windows 音频设置已更新，读回一致。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    private void SoundSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:sound");
    private void BluetoothSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:bluetooth");
    private void Previous_Click(object sender, RoutedEventArgs e) => WindowsAudio.MediaKey(0xB1);
    private void Play_Click(object sender, RoutedEventArgs e) => WindowsAudio.MediaKey(0xB3);
    private void Next_Click(object sender, RoutedEventArgs e) => WindowsAudio.MediaKey(0xB0);

    private async void Export_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await controller.RefreshAsync(lifetime.Token);
        var dialog = new SaveFileDialog { Filter = "PILL 配置 (*.json)|*.json", FileName = $"PILL-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(controller.State, LocalFiles.JsonOptions));
    }, "导出操作完成。");
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = "PILL 配置 (*.json)|*.json", InitialDirectory = Path.Combine(LocalFiles.Root, "backups") };
            if (dialog.ShowDialog(this) != true) return;
            imported = LocalFiles.Load(dialog.FileName);
            ImportText.Text = $"已载入 {Path.GetFileName(dialog.FileName)}\n固件 {imported.Firmware} · {imported.Eq?.Bands.Count} 段 EQ · 双设备 {(imported.Multipoint ? "开" : "关")}。点击“应用导入设置”后才会修改耳机。";
            SetAvailable();
        }
        catch (Exception ex) { imported = null; SetAvailable(); StatusText.Text = ex.Message; }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var data = imported ?? throw new IOException("请先导入备份。");
        if (controller.State?.DeviceAddress != data.DeviceAddress || controller.State.Firmware != data.Firmware)
            throw new IOException("备份的设备或固件与当前耳机不一致，未进行恢复。");
        if (!controller.State.Presets.Contains(data.Preset)) throw new IOException("备份预设不受当前耳机支持。");
        if (data.Eq != null) await controller.SetEqAsync(data.Eq);
        await controller.SetPresetAsync(data.Preset); await controller.SetGainAsync(data.Gain);
        await controller.SetTouchAsync(data.Touch); await controller.SetVoiceAsync(data.Voice[0] == 1, data.Voice[1]);
        await controller.SetMultipointAsync(data.Multipoint); await controller.RefreshAsync(lifetime.Token); RenderState();
    }, "备份已恢复，各项设置均已读回核对。");
    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    { string path = Path.Combine(LocalFiles.Root, "backups"); Directory.CreateDirectory(path); Open(path); }
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "诊断报告 (*.json)|*.json", FileName = $"PILL-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = controller.State?.Copy();
        if (snapshot != null) { snapshot.DeviceAddress = "省略"; snapshot.ConnectedDevices = []; }
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { version = "0.2.1", snapshot, log = log.ToArray() }, LocalFiles.JsonOptions));
        StatusText.Text = "诊断报告已保存到本地。";
    }
}
