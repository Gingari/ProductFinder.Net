using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ProductFinder.Infrastructure;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try { await execute(); }
        catch (Exception ex) { onError(ex); }
    }
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
