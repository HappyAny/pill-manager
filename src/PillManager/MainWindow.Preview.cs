using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PillManager;

public partial class MainWindow
{
    // Developer preview. Native discovery is read-only and requires a separate explicit flag.
    private async Task CaptureUiPreviewsAsync()
    {
        string dir = Path.GetFullPath(Environment.GetEnvironmentVariable("PILL_PREVIEW_DIR") ?? "artifacts/ui-0.2.1");
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        bool checkNative = Environment.GetCommandLineArgs().Contains("--verify-native");
        int? audioEndpoints = null, discoveredPills = null;
        try
        {
            if (Icon == null) throw new InvalidDataException("应用图标未加载。");
            foreach (var document in LicenseWindow.Documents)
            {
                using var stream = LicenseWindow.OpenDocument(document.Resource);
                if (stream.Length == 0) throw new InvalidDataException("内置许可证为空。");
            }
            if (checkNative)
            {
                audioEndpoints = WindowsAudio.List().Count;
                discoveredPills = (await BleTransport.ScanAsync()).Count;
            }
            PreviewBadge.Visibility = Visibility.Visible;
            var state = new PillState { Firmware = "1.6.0", Model = "MOONDROP PILL", ReadAt = DateTimeOffset.Now,
                Batteries = new() { [1] = 70, [2] = 90 }, Presets = [0, 1, 2, 3, 4, 63], Preset = 63,
                Eq = EqConfiguration.Decode(Convert.FromHexString("0004FF4C003406660000B401FE1B330000600226100000FFC41130080000FEE61F400B4F000000")),
                Touch = Convert.FromHexString("013213233030323530303535"), Voice = [1, 56, 0], Multipoint = true, ConnectedDevices = ["示例电脑"] };
            RenderState(state);
            Devices.ItemsSource = new[] { "MOONDROP PILL · 界面预览" }; Devices.SelectedIndex = 0;
            DeviceStatus.Text = "模拟显示"; ConnectionText.Text = "本地示例数据 · 未连接耳机";
            UpdatedText.Text = "示例电量";
            StatusText.Text = "界面预览 · 此模式不访问蓝牙或音频设备";
            ScanButton.IsEnabled = ConnectButton.IsEnabled = Devices.IsEnabled = false;
            EqPanel.IsEnabled = TouchPanel.IsEnabled = SettingsPanel.IsEnabled = true;
            Tabs.IsHitTestVisible = false;
            AudioDevices.ItemsSource = new[] { new AudioEndpoint("preview", "耳机 (MOONDROP PILL)", false, 60, false) };
            AudioDevices.SelectedIndex = 0; AudioStatus.Text = "仅调整所选 PILL 设备。音频连接由 Windows 管理。";
            foreach (string mode in new[] { "Light", "Dark" })
            {
                ApplyTheme(mode);
                for (int page = 0; page < Tabs.Items.Count; page++)
                {
                    Tabs.SelectedIndex = page;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(100);
                    Capture($"{mode.ToLowerInvariant()}-{page}.png");
                }
            }
            Width = MinWidth; Height = MinHeight; Tabs.SelectedIndex = 2;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100); Capture("minimum-touch.png");
            using var process = Process.GetCurrentProcess();
            string? coreClrPath = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
            File.WriteAllText(Path.Combine(dir, "receipt.json"), JsonSerializer.Serialize(new { version = "0.2.1", at = DateTimeOffset.Now, width = 620, height = 660, minimumWidth = MinWidth, minimumHeight = MinHeight, files, deviceAccess = checkNative, hardwareWrites = false, audioEndpoints, discoveredPills, windowIconLoaded = Icon != null, embeddedLicenseDocuments = LicenseWindow.Documents.Length, coreClrPath }, LocalFiles.JsonOptions));
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); Application.Current.Shutdown(1);
        }
        void Capture(string name)
        {
            var root = (FrameworkElement)Content; root.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("CanvasBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            image.Render(drawing);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(dir, name)); png.Save(output); files.Add(name);
        }
    }
}
