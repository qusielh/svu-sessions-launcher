using System.Windows.Input;
using SvuSessionsLauncher.Services;

namespace SvuSessionsLauncher.ViewModels;

/// <summary>
/// A basic ICommand implementation for binding UI actions to view-model
/// methods, with an optional async handler and a can-execute predicate.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action? _execute;
    private readonly Func<Task>? _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _executeAsync = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            if (_executeAsync is not null)
            {
                await _executeAsync();
            }
            else
            {
                _execute!();
            }
        }
        catch (Exception ex)
        {
            // An async command must never crash the dispatcher; surface the
            // failure in the diagnostics log instead.
            Log.Write($"command failed: {ex}");
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
