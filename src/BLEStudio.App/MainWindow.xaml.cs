using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace BLEStudio.App;

public partial class MainWindow : Window
{
    private bool _closing;
    private readonly System.Windows.Forms.NotifyIcon? _tray;
    private readonly bool _trayEnabled;
    public MainWindow(StudioViewModel viewModel, bool trayEnabled = true)
    {
        InitializeComponent(); DataContext = viewModel;
        if (trayEnabled)
        {
            try
            {
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("開啟 / Open", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
                menu.Items.Add("停止追蹤 / Stop tracker", null, async (_, _) => { try { await viewModel.Tracker.StopAsync(); } catch (Exception ex) { viewModel.ReportError(ex); } });
                menu.Items.Add("退出並停止廣播 / Exit", null, (_, _) => Dispatcher.Invoke(() => { _exitRequested = true; Close(); }));
                _tray = new() { Text = "Windows BLE Studio · close to tray, exit stops BLE", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
                _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
                _trayEnabled = true;
            }
            catch (Exception ex) { viewModel.ReportError(ex); }
            SystemEvents.PowerModeChanged += PowerChanged;
        }
    }
    private bool _exitRequested;
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(async () => { try { if (DataContext is StudioViewModel vm) { if (e.Mode == PowerModes.Suspend) await vm.SuspendAsync(); else if (e.Mode == PowerModes.Resume) await vm.ResumeAsync(); } } catch (Exception ex) { if (DataContext is StudioViewModel vm) vm.ReportError(ex); } });
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        if (_trayEnabled && !_exitRequested) { e.Cancel = true; Hide(); return; }
        e.Cancel = true;
        await Task.Yield(); // Return from the first Closing event before requesting Close again.
        try { if (DataContext is StudioViewModel vm) await vm.DisposeAsync(); }
        catch (Exception ex) { if (DataContext is StudioViewModel vm) vm.ReportError(ex); }
        finally { SystemEvents.PowerModeChanged -= PowerChanged; _tray?.Dispose(); _closing = true; Close(); }
    }
}
