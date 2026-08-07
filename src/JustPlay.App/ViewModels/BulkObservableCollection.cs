using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JustPlay.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can remove or replace many items with a SINGLE
/// <see cref="NotifyCollectionChangedAction.Reset"/> notification instead of one event per item.
///
/// <para><b>Why this exists:</b> deleting a large multi-selection (e.g. Ctrl+A -> Delete on a 10-hour
/// queue) via a per-item <c>Remove</c> loop is pathologically slow: each <c>Remove</c> is an O(n)
/// search AND raises its own <c>CollectionChanged</c>, so the bound queue list re-lays-out once per
/// removed row -> O(n^2) work and N UI re-renders. <see cref="RemoveRange"/> mutates the backing list
/// in one O(n) pass and raises a single <c>Reset</c>, so the list rebuilds its (virtualized) rows
/// exactly once. Same for <see cref="ReplaceAll"/> when bulk-loading.</para>
///
/// Behaves exactly like <see cref="ObservableCollection{T}"/> for all normal add/remove/clear use;
/// only the two bulk helpers are new.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }
    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>
    /// Remove every item contained in <paramref name="items"/> in one pass, raising a single Reset.
    /// No-op (no notification) when nothing actually matches.
    /// </summary>
    public void RemoveRange(IEnumerable<T> items)
    {
        var set = items as ISet<T> ?? new HashSet<T>(items);
        if (set.Count == 0) return;

        // Items is the backing List<T> (ObservableCollection's default ctor wraps one). Mutating it
        // directly bypasses the per-item events; we raise one Reset afterwards.
        var backing = (List<T>)Items;
        int removed = backing.RemoveAll(set.Contains);
        if (removed == 0) return;

        RaiseReset();
    }

    /// <summary>Replace the entire contents with <paramref name="items"/> in one pass (single Reset).</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var backing = (List<T>)Items;
        backing.Clear();
        backing.AddRange(items);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
