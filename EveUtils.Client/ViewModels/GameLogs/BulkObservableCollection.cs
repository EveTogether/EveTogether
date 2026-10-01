using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace EveUtils.Client.ViewModels.GameLogs;

/// <summary>An <see cref="ObservableCollection{T}"/> that can swap its whole content with one notification:
/// tens of thousands of log lines raised one by one would make the list re-measure for every single change.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    // The parameterless constructor backs Items with a List<T>.
    private List<T> Backing => (List<T>)Items;

    public void ReplaceAll(IEnumerable<T> items)
    {
        Backing.Clear();
        Backing.AddRange(items);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
