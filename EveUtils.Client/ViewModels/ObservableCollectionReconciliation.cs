using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EveUtils.Client.ViewModels;

public static class ObservableCollectionReconciliation
{
    /// <summary>
    /// Brings <paramref name="shown"/> to <paramref name="target"/> by instance, touching only what differs: an item
    /// already shown stays the same object in the same container, so whatever the view holds against it — an open
    /// row, the scroll offset above it — survives. Clearing and refilling instead rebuilds every container on the
    /// screen for a change to one of them (ET-222).
    /// </summary>
    public static void ReconcileTo<T>(this ObservableCollection<T> shown, IReadOnlyList<T> target) where T : class
    {
        var wanted = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        for (int index = 0; index < target.Count; index++)
        {
            T item = target[index];
            if (index < shown.Count && ReferenceEquals(shown[index], item))
                continue;

            int current = _IndexOf(shown, item, index + 1);
            if (current >= 0)
                shown.Move(current, index);
            else if (index < shown.Count && !wanted.Contains(shown[index]))
                // Replaced in place rather than inserted beside a row about to go: the row after it does not move.
                shown[index] = item;
            else
                shown.Insert(index, item);
        }

        while (shown.Count > target.Count)
            shown.RemoveAt(shown.Count - 1);
    }

    private static int _IndexOf<T>(ObservableCollection<T> shown, T item, int start) where T : class
    {
        for (int index = start; index < shown.Count; index++)
            if (ReferenceEquals(shown[index], item))
                return index;
        return -1;
    }
}
