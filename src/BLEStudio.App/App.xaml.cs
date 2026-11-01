using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BLEStudio.Bluetooth;
using BLEStudio.Core;
using BLEStudio.Protocols;

namespace BLEStudio.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            if (MainWindow?.DataContext is StudioViewModel vm) { vm.ReportError(args.Exception); args.Handled = true; }
        };
        try
        {
            if (e.Args.Contains("--tracker-hardware-test"))
            {
                await TrackerHardwareTestAsync(OutputDirectory(e.Args)); Shutdown(); return;
            }
            if (e.Args.Contains("--hardware-test"))
            {
                await HardwareTestAsync(OutputDirectory(e.Args)); Shutdown(); return;
            }
            var smoke = e.Args.Contains("--smoke-test");
            var output = smoke ? OutputDirectory(e.Args) : null;
            var vm = new StudioViewModel(smoke || e.Args.Contains("--mock"), smoke ? Path.Combine(output!, "local-data") : null);
            MainWindow = new MainWindow(vm, !smoke); MainWindow.Show();
            await vm.InitializeAsync();
            if (smoke) await SmokeTestAsync((MainWindow)MainWindow, vm, output!);
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--smoke-test") || e.Args.Contains("--hardware-test"))
            {
                var output = OutputDirectory(e.Args); Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Environment.ExitCode = 1; Shutdown(1);
            }
            else if (MainWindow?.DataContext is StudioViewModel vm) vm.ReportError(ex);
            else { MessageBox.Show(ex.Message, "BLE Studio startup error", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
        }
    }
    private static string OutputDirectory(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        return Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Path.GetTempPath(), "BLEStudio-test-" + Guid.NewGuid().ToString("N")));
    }
    private async Task SmokeTestAsync(MainWindow window, StudioViewModel vm, string output)
    {
        Directory.CreateDirectory(output); var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("UI smoke check failed: " + name); checks.Add(name); }
        await Task.Delay(500);
        Check(vm.Adapter.IsMock, "Explicit mock backend selected"); Check(vm.Profiles.Count >= 6, "Six embedded profiles seeded");
        vm.InputHex = "07 FF FF FF 01 02 03 04";
        Check(vm.Structures.Count == 1, "Hex input decoded in view model");
        vm.InputHex = "03 FF"; Check(vm.PayloadStatus.Contains("Invalid"), "Malformed hex surfaced");
        vm.InputHex = "07 FF FF FF 01 02 03 04"; vm.Duration = "0";
        vm.StartAdvertisingCommand.Execute(null); await Task.Delay(150); Check(vm.AdvertisingState == "Started", "Mock publisher Start reaches UI");
        vm.StopAdvertisingCommand.Execute(null); await Task.Delay(150); Check(vm.AdvertisingState == "Stopped", "Mock publisher Stop reaches UI");
        vm.StartScanningCommand.Execute(null); await Task.Delay(1300); Check(vm.DeviceCount == 4, "Mock scan packets reach bounded UI aggregation");
        vm.SelectedDevice = vm.Devices[0]; Check(vm.RssiPoints.Count > 0, "RSSI chart receives samples");
        vm.Search = "not-a-beacon"; Check(vm.DeviceView.IsEmpty, "Scanner search filters devices"); vm.Search = "";
        vm.StopScanningCommand.Execute(null); await Task.Delay(200);
        vm.NewProfileCommand.Execute(null); vm.ProfileName = "UI smoke profile"; vm.SaveProfileCommand.Execute(null); Check(vm.Profiles.Any(x => x.Name == "UI smoke profile"), "Profile saved by GUI command");
        vm.ProfileName = "UI smoke revision"; vm.SaveProfileCommand.Execute(null); Check(vm.ProfileRevisions.Count > 0, "Revision retained by GUI save");
        vm.DuplicateProfileCommand.Execute(null); Check(vm.ProfileName.EndsWith("(copy)", StringComparison.Ordinal), "Independent duplicate saved");
        vm.SectionKind = "Service data"; vm.SectionData = "01"; vm.AddSectionCommand.Execute(null); Check(vm.Validation.Contains("reserved", StringComparison.OrdinalIgnoreCase), "Reserved service data blocked before transmission");
        vm.InputHex = "07 FF FF FF 01 02 03 04";
        vm.ResearchConsent = true; vm.ResearchInput = string.Join(" ", Enumerable.Range(1, 20).Select(x => x.ToString("X2"))); vm.GooglePreviewCommand.Execute(null);
        vm.AnalyzeCommand.Execute(null); Check(vm.ResearchReport.Contains("FHN"), "Google research parser connected to GUI");
        vm.ResearchInput = string.Join(" ", Enumerable.Range(1, 28).Select(x => x.ToString("X2"))); vm.ApplePreviewCommand.Execute(null);
        Check(vm.ResearchReport.Contains("Required static-random address"), "Apple address incompatibility displayed");
        vm.SelectedProfile = vm.Profiles.First(x => x.Name == "Generic BLE Beacon");
        vm.SelectedDevice = vm.Devices.FirstOrDefault();
        for (var i = 0; i < window.Sections.Items.Count; i++)
        {
            window.Sections.SelectedIndex = i; await Task.Delay(150); window.UpdateLayout(); RenderWindow(window, Path.Combine(output, $"page-{i + 1}.png"));
        }
        vm.ToggleThemeCommand.Execute(null); window.Sections.SelectedIndex = 0; await Task.Delay(150); window.UpdateLayout(); RenderWindow(window, Path.Combine(output, "dashboard-light.png"));
        Check(window.Sections.Items.Count == 8, "All eight pages and light/dark themes rendered");
        vm.Tracker.GenerateCommand.Execute(null); await Task.Delay(100);
        Check(vm.Tracker.IdentityStatus.Contains("DPAPI"), "Tracker identity protected with DPAPI");
        Check(vm.Tracker.LocationStatus.Contains("尚無定位回報"), "No synthetic tracker coordinates are displayed");
        Check(vm.Tracker.Capabilities.Contains("NOT verified"), "Tracker network evidence starts unverified");
        File.WriteAllText(Path.Combine(output, "smoke-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, logs = vm.Logs }, ProfileJson.Options));
        window.Close();
    }
    private static void RenderWindow(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static async Task TrackerHardwareTestAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>();
        await using var detection = new WindowsBluetoothBackend(); var adapter = await detection.DetectAsync();
        await Run(new WindowsBluetoothBackend(), new() { Name = "Publisher 28-byte control", PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.Manufacturer(0xFFFF, new byte[24])])) });
        await Run(new WindowsBluetoothBackend(), new() { Name = "Publisher 31-byte Apple-format fixture", Protocol = ResearchProtocol.AppleFindMy, PayloadHex = AppleFindMyResearch.Preview(new byte[28]).PayloadHex });
        foreach (var (bytes, extended) in new[] { (1, false), (21, false), (22, false), (21, true), (33, true) })
        {
            var data = new byte[bytes]; data[0] = 0x40;
            await Run(new WindowsGattBackend(), new() { Name = $"GATT FEAA {bytes}-byte ServiceData / extended={extended}", UseExtendedAdvertising = extended, Scannable = true, PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.ServiceData("FEAA", data)])) });
        }
        File.WriteAllText(Path.Combine(output, "tracker-hardware-results.json"), JsonSerializer.Serialize(new { testedAt = DateTimeOffset.UtcNow, adapter, cases = results,
            externalPacketVerified = false, locationReportVerified = false, fixtures = "Unregistered zero-filled research data only; no account secrets or real tracker identities.",
            limitations = "API lifecycle only. External phone capture must verify UUID width, primary vs Scan Response, flags, full bytes and address. No network tracking is certified." }, ProfileJson.Options));
        async Task Run(IBluetoothBackend backend, AdvertisementProfile profile)
        {
            var events = new List<BackendStatus>(); var sync = new object(); var accepted = false; string? error = null;
            backend.AdvertisingStatusChanged += (_, e) => { lock (sync) events.Add(e); };
            try { await backend.StartAdvertisingAsync(profile); accepted = true; await Task.Delay(700); }
            catch (Exception ex) { error = ex is BluetoothOperationException b ? b.Code + ": " + b.Message : ex.Message; }
            finally { try { await backend.StopAdvertisingAsync(); await backend.DisposeAsync(); } catch (Exception ex) { error ??= ex.Message; } }
            BackendStatus[] snapshot; lock (sync) snapshot = events.ToArray();
            results.Add(new { profile.Name, backend = backend.Name, apiAcceptedAllData = accepted, error, profile.UseExtendedAdvertising, profile.PayloadHex, statuses = snapshot });
        }
    }
    private static async Task HardwareTestAsync(string output)
    {
        Directory.CreateDirectory(output); var events = new List<object>(); var failures = new List<string>(); var sync = new object(); var received = 0;
        await using var backend = new WindowsBluetoothBackend();
        backend.AdvertisingStatusChanged += (_, status) => { lock (sync) events.Add(new { category = "Advertising", time = DateTimeOffset.UtcNow, status }); };
        backend.ScanningStatusChanged += (_, status) => { lock (sync) events.Add(new { category = "Scanning", time = DateTimeOffset.UtcNow, status }); };
        backend.PacketReceived += (_, _) => Interlocked.Increment(ref received);
        var adapter = await backend.DetectAsync(); var cases = new List<object>();
        if (adapter.CanAdvertise)
        {
            await PublishCaseAsync(new() { Name = "Generic legacy manufacturer", PayloadHex = "07 FF FF FF 01 02 03 04" });
            if (adapter.ExtendedAdvertising)
            {
                await PublishCaseAsync(new() { Name = "Extended manufacturer (44 AD bytes)", UseExtendedAdvertising = true,
                    PayloadHex = Hex.Format(AdCodec.Encode([AdCodec.Manufacturer(0xFFFF, new byte[40])])) });
                await PublishCaseAsync(new() { Name = "Extended header TX power", UseExtendedAdvertising = true,
                    IncludeTransmitPower = true, PayloadHex = "07 FF FF FF 01 02 03 04" });
                await PublishCaseAsync(new() { Name = "Extended preferred TX power -8 dBm", UseExtendedAdvertising = true,
                    PreferredTransmitPowerDbm = -8, PayloadHex = "07 FF FF FF 01 02 03 04" });
                await PublishCaseAsync(new() { Name = "Extended 1M primary / 2M secondary", UseExtendedAdvertising = true,
                    PrimaryPhy = PhyPreference.LE1M, SecondaryPhy = PhyPreference.LE2M, PayloadHex = "07 FF FF FF 01 02 03 04" });
                await PublishCaseAsync(new() { Name = "Extended anonymous / Coded PHY", UseExtendedAdvertising = true,
                    Anonymous = true, PrimaryPhy = PhyPreference.LECoded, SecondaryPhy = PhyPreference.LECoded, PayloadHex = "07 FF FF FF 01 02 03 04" });
            }
        }
        else failures.Add("Advertising unavailable according to adapter query; no transmission attempted.");
        if (adapter.CanScan)
        {
            try { await backend.StartScanningAsync(); await Task.Delay(6000); }
            catch (Exception ex) { failures.Add("Scanning: " + ex.Message); }
            finally { await backend.StopScanningAsync(); }
        }
        else failures.Add("Scanning unavailable according to adapter query; watcher not started.");
        object[] snapshot; lock (sync) snapshot = events.ToArray();
        File.WriteAllText(Path.Combine(output, "hardware-results.json"), JsonSerializer.Serialize(new { testedAt = DateTimeOffset.Now, adapter, receivedPackets = received, advertisingCases = cases, events = snapshot, failures,
            externalRfCapture = "Not performed. Publisher Started does not prove over-the-air bytes or network participation.", researchTransmission = "Not attempted; only generic manufacturer test data was used." }, ProfileJson.Options));
        async Task PublishCaseAsync(AdvertisementProfile profile)
        {
            var started = false; string? error = null;
            try { await backend.StartAdvertisingAsync(profile); started = true; await Task.Delay(1000); }
            catch (Exception ex) { error = ex.Message; failures.Add(profile.Name + ": " + ex.Message); }
            finally { try { await backend.StopAdvertisingAsync(); } catch (Exception ex) { error = ex.Message; failures.Add(profile.Name + " Stop: " + ex.Message); } }
            cases.Add(new { name = profile.Name, windowsStarted = started, error, requested = profile,
                rfEffect = "Not independently measured. Started verifies API lifecycle only." });
        }
    }
}
