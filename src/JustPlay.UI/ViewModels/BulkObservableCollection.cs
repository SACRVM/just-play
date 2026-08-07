using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be refilled in ONE notification.
///
/// <para>Why it exists: replacing the contents the ordinary way - <c>Clear()</c> then <c>Add</c> per
/// item - raises one CollectionChanged per row, and every one of those invalidates the bound list's
/// layout. At a few hundred rows nobody notices. At the ~15,000 files under a library root it is
/// seconds of frozen UI on top of whatever the disk cost (Chloe 2026-08-06: "waehle links den ordner
/// GENRES und baemmm... app ist tot und non responding"). <see cref="Reset"/> mutates the backing list
/// directly and raises a single Reset, which the list handles as one rebuild.</para>
///
/// <para>Reset is the right shape here rather than a diff: the callers are "show this folder" and
/// "show what the search matched", and both are wholesale replacements. A Reset does lose the
/// selection - the caller restores what it means to keep.</para>
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }

    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>Replace everything, in one notification.</summary>
    public void Reset(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        // Count and the indexer both changed; Avalonia's bindings read them, so say so before the
        // collection event - the same order ObservableCollection uses for its own mutations.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
