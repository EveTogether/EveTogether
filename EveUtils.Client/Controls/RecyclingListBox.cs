using System;
using Avalonia.Controls;

namespace EveUtils.Client.Controls;

/// <summary>
/// A list box that hands a recycled container only to an item of the same kind (ET-290). The runs list keeps day
/// headers, activity rows and pilots' runs in one virtualised sequence; recycled across kinds, a container would first
/// rebind the template it holds to an item that template does not fit, and then build the right one from scratch.
/// </summary>
public class RecyclingListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        bool needsContainer = base.NeedsContainerOverride(item, index, out recycleKey);
        if (needsContainer && item is not null)
            recycleKey = item.GetType();
        return needsContainer;
    }
}
