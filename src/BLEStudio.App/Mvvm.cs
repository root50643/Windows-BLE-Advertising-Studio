using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BLEStudio.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
}

public sealed class UiCommand(Func<Task> execute, Action<Exception> error) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running;
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); } catch (Exception ex) { error(ex); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
