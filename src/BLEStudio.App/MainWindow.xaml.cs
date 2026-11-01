using System.ComponentModel;
using System.Windows;

namespace BLEStudio.App;

public partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow(StudioViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        e.Cancel = true;
        await Task.Yield(); // Return from the first Closing event before requesting Close again.
        try { if (DataContext is StudioViewModel vm) await vm.DisposeAsync(); }
        catch (Exception ex) { if (DataContext is StudioViewModel vm) vm.ReportError(ex); }
        finally { _closing = true; Close(); }
    }
}
