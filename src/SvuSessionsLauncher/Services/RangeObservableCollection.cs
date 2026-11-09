using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can append or replace a whole
/// batch in one shot, raising a single <see cref="NotifyCollectionChangedAction.Reset"/>
/// notification. A bound list therefore lays out once per batch instead of once
/// per item — the difference between a smooth load and a visible stutter when
/// the timetable holds many sessions.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    /// <summary>Appends a batch, notifying a single reset at the end.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppress = true;
        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppress = false;
        }

        RaiseReset();
    }

    /// <summary>Clears and re-fills the collection, notifying a single reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppress = false;
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }
}
