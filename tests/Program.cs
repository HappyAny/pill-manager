using System.Text.Json;
using PillManager;

var results = new List<object>();
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name); results.Add(new { name, passed = true });
}
void Reject(Action action, string name)
{
    bool rejected = false;
    try { action(); } catch (ArgumentException) { rejected = true; } catch (FormatException) { rejected = true; }
    Assert(rejected, name);
}
byte[] Hex(string s) => Convert.FromHexString(s);
Assert(Convert.ToHexString(GaiaProtocol.Command(13, 1, 1, 2)) == "001D1A010102", "battery query matches device capture");
var packet = GaiaProtocol.Parse(Hex("001D1B010146025A"));
Assert(packet.Feature == 13 && packet.Command == 1 && packet.Type == 2, "response routing");
var battery = GaiaProtocol.Batteries(packet.Payload);
Assert(battery[1] == 70 && battery[2] == 90, "left and right battery capture");
Assert(!GaiaProtocol.Batteries(Hex("01FF0200")).ContainsKey(1) && GaiaProtocol.Batteries(Hex("01FF0200"))[2] == 0, "unknown battery is distinct from zero");
Reject(() => GaiaProtocol.Parse(Hex("001D01")), "truncated packet rejected");
Reject(() => GaiaProtocol.Parse(Hex("FFFF0100")), "other vendor rejected");
Reject(() => GaiaProtocol.Batteries(Hex("010201")), "odd battery payload rejected");
Assert(GaiaProtocol.Parse(Hex("001D0B8301")).Type == 3, "error packet distinct from success");
var capturedEq = Hex("0004FF4C003406660000B401FE1B330000600226100000FFC41130080000FEE61F400B4F000000");
var eq = EqConfiguration.Decode(capturedEq);
Assert(eq.Bands.Count == 5 && eq.PreGain == -3 && eq.Bands[0].Frequency == 52 && eq.Bands[3].Gain == -4.7, "Bluetrum EQ capture decoded");
Assert(eq.Encode().SequenceEqual(capturedEq), "all EQ bytes survive round trip including filter and Q");
Assert(Convert.ToHexString(eq.EncodeBand(3)) == "0303FF4C1130080000FEE6", "single band write matches captured values");
Assert(GaiaProtocol.Command(5, 6, eq.EncodeBand(0)).Length <= 20, "EQ writes fit the PILL ATT limit");
Reject(() => EqConfiguration.Decode(capturedEq[..^1]), "truncated EQ rejected");
Reject(() => EqConfiguration.Decode(Hex("00070000")), "oversized EQ rejected");
var bad = eq.Copy(); bad.Bands[0].Q = double.NaN; Reject(() => bad.Encode(), "non-finite Q rejected");
bad = eq.Copy(); bad.Bands[0].Frequency = 0; Reject(() => bad.Encode(), "invalid frequency rejected");
bad = eq.Copy(); bad.PreGain = 0.1; Reject(() => bad.Encode(), "positive pregain rejected");
bad = eq.Copy(); bad.Bands[0].Gain = 30; Reject(() => bad.Encode(), "excess band gain rejected");

Assert(VoiceVolumeCodec.WritePercent(9) == 57 && VoiceVolumeCodec.ReadPercent(9) == 56 && VoiceVolumeCodec.Step(56) == 9, "voice step 9 matches hardware capture");
Assert(VoiceVolumeCodec.WritePercent(10) == 63 && VoiceVolumeCodec.ReadPercent(10) == 62, "voice step 10 matches hardware capture");

if (args.Contains("--live"))
{
    Environment.SetEnvironmentVariable("PILL_MANAGER_DATA_DIR", Path.GetFullPath("artifacts/test-data"));
    await using var controller = new PillController();
    Directory.CreateDirectory("artifacts");
    string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    using var log = new StreamWriter($"artifacts/live-{runId}.log", false) { AutoFlush = true };
    controller.Log += line => log.WriteLine($"{DateTimeOffset.Now:o} {line}");
    try
    {
    var devices = await BleTransport.ScanAsync();
    Assert(devices.Count == 1, "one PILL discovered without a hardcoded address");
    await controller.ConnectAsync(devices.Single());
    int restoreAt = Array.IndexOf(args, "--restore");
    if (restoreAt >= 0)
    {
        var saved = LocalFiles.Load(args[restoreAt + 1]);
        await controller.SetEqAsync(saved.Eq!);
        await controller.SetPresetAsync(saved.Preset);
        await controller.RefreshAsync();
        Assert(controller.State!.Eq!.Encode().SequenceEqual(saved.Eq!.Encode()) && controller.State.Preset == saved.Preset, "saved original EQ fully restored");
        Console.WriteLine("RESTORED");
        return;
    }
    var initial = controller.State!.Copy();
    File.WriteAllText($"artifacts/before-{runId}.json", JsonSerializer.Serialize(initial, LocalFiles.JsonOptions));
    Assert(initial.Model == "MOONDROP PILL" && initial.Firmware == "1.6.0", "hardware identity confirmed");
    Assert(initial.Batteries.Count == 2 && initial.Eq?.Bands.Count == 5 && initial.Touch.Length == 12, "battery EQ and touch read from hardware");
    Console.WriteLine(JsonSerializer.Serialize(new { initial.Firmware, initial.Batteries, initial.Preset, initial.Gain, initial.Multipoint, initial.Touch, initial.Voice }));

    if (args.Contains("--write-tests"))
    {
        var failures = new List<string>();
        async Task Trial(string name, Func<Task> change, Func<Task> restore, Func<PillState, bool> original)
        {
            try
            {
                await change(); Assert(true, name + " changed and read back");
            }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); results.Add(new { name, passed = false, error = ex.Message }); }
            finally
            {
                await restore(); await controller.RefreshAsync();
                Assert(original(controller.State!), name + " original restored and verified");
            }
        }
        await Trial("gain", () => controller.SetGainAsync((byte)(initial.Gain == 0 ? 1 : 0)), () => controller.SetGainAsync(initial.Gain), s => s.Gain == initial.Gain);
        await Trial("preset", () => controller.SetPresetAsync(initial.Presets.First(x => x != initial.Preset)), () => controller.SetPresetAsync(initial.Preset), s => s.Preset == initial.Preset);
        var trialEq = initial.Eq!.Copy(); trialEq.PreGain = initial.Eq.PreGain > -23.9 ? initial.Eq.PreGain - 0.1 : initial.Eq.PreGain + 0.1;
        await Trial("parametric EQ", () => controller.SetEqAsync(trialEq), async () => { await controller.SetEqAsync(initial.Eq); await controller.SetPresetAsync(initial.Preset); }, s => s.Eq!.Encode().SequenceEqual(initial.Eq.Encode()) && s.Preset == initial.Preset);
        var trialTouch = (byte[])initial.Touch.Clone(); trialTouch[4] = (byte)((trialTouch[4] & 0xF0) | ((trialTouch[4] & 15) == 4 ? 0 : 4));
        await Trial("touch", () => controller.SetTouchAsync(trialTouch), () => controller.SetTouchAsync(initial.Touch), s => s.Touch.SequenceEqual(initial.Touch));
        byte trialVoice = VoiceVolumeCodec.ReadPercent(Math.Max(0, VoiceVolumeCodec.Step(initial.Voice[1]) - 1));
        await Trial("voice volume", () => controller.SetVoiceAsync(initial.Voice[0] == 1, trialVoice), () => controller.SetVoiceAsync(initial.Voice[0] == 1, initial.Voice[1]), s => s.Voice.SequenceEqual(initial.Voice));
        await Trial("multipoint", () => controller.SetMultipointAsync(!initial.Multipoint), () => controller.SetMultipointAsync(initial.Multipoint), s => s.Multipoint == initial.Multipoint);

        var audio = WindowsAudio.List();
        Assert(audio.Any(x => !x.IsMicrophone), "PILL playback endpoint found");
        foreach (var endpoint in audio)
        {
            string name = endpoint.IsMicrophone ? "Windows microphone volume" : "Windows output volume";
            try { WindowsAudio.Set(endpoint.Id, endpoint.Volume > 0 ? endpoint.Volume - 1 : 1, endpoint.Muted); Assert(true, name + " changed and read back"); }
            finally { WindowsAudio.Set(endpoint.Id, endpoint.Volume, endpoint.Muted); Assert(true, name + " restored"); }
        }
        await controller.RefreshAsync();
        var final = controller.State!;
        Assert(final.Eq!.Encode().SequenceEqual(initial.Eq.Encode()) && final.Touch.SequenceEqual(initial.Touch) && final.Voice.SequenceEqual(initial.Voice) &&
            final.Preset == initial.Preset && final.Gain == initial.Gain && final.Multipoint == initial.Multipoint, "all original earbud settings restored");
        File.WriteAllText($"artifacts/after-{runId}.json", JsonSerializer.Serialize(final, LocalFiles.JsonOptions));
        if (failures.Count > 0) Environment.ExitCode = 1;
    }
    }
    catch (Exception ex)
    {
        results.Add(new { name = "live run", passed = false, error = ex.Message });
        Console.Error.WriteLine(ex); Environment.ExitCode = 1;
    }
    finally
    {
        // The summary omits addresses, host names, and unrelated devices.
        File.WriteAllText("artifacts/verification.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, results }, LocalFiles.JsonOptions));
    }
}
else
{
    Directory.CreateDirectory("artifacts");
    File.WriteAllText("artifacts/protocol-checks.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, results }, LocalFiles.JsonOptions));
}
