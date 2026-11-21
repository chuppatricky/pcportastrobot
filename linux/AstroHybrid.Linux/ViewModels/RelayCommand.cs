using System;
using System.Windows.Input;

namespace AstroHybrid.App.ViewModels;

// Linux replacement for upstream's RelayCommand.cs (same names, so upstream's page view models compile
// unchanged). WPF re-asks every command's CanExecute after any input through CommandManager; Avalonia has
// no such thing, so all live commands listen to one static event the window raises after input and after
// every view-model change (MainWindow / MainWindowViewModel).
public static class CommandRequery
{
    public static event EventHandler Suggested;
    public static void Raise() => Suggested?.Invoke(null, EventArgs.Empty);
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged
    {
        add => CommandRequery.Suggested += value;
        remove => CommandRequery.Suggested -= value;
    }

    public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object parameter) => _execute();
    public void RaiseCanExecuteChanged() => CommandRequery.Raise();
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool> _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged
    {
        add => CommandRequery.Suggested += value;
        remove => CommandRequery.Suggested -= value;
    }

    public bool CanExecute(object parameter) => parameter is T t ? _canExecute?.Invoke(t) ?? true : _canExecute == null;

    public void Execute(object parameter)
    {
        if (parameter is T t) _execute(t);
    }
}
