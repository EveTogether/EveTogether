using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace EveUtils.Client.Opsec;

/// <summary>
/// The text edge of OPSEC mode (ET-417): every <see cref="TextBlock"/> and <see cref="Run"/> in every window shows its
/// text through <see cref="IOpsecService.Render"/> once that text carries an <see cref="OpsecText"/> marker. One
/// class-level hook instead of a converter per binding: most locations sit inside composite strings, tooltips, menu
/// headers and toasts that no binding converter would ever see.
/// </summary>
/// <remarks>The rendering replaces the current value only (<c>SetCurrentValue</c>), so the binding stays in place and
/// its next value comes through here again. The marked source is kept beside it, so a toggle can render what is
/// already on screen again without anything being rebuilt.</remarks>
public sealed class OpsecTextRenderer : IDisposable
{
    private readonly IOpsecService _opsec;
    private readonly IDisposable[] _classHandlers;
    private readonly ConditionalWeakTable<AvaloniaObject, string> _sources = new();
    private AvaloniaObject? _renderingTarget;

    public OpsecTextRenderer(IOpsecService opsec)
    {
        _opsec = opsec;
        _classHandlers =
        [
            TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((target, e) => _OnTextChanged(target, TextBlock.TextProperty, e)),
            Run.TextProperty.Changed.AddClassHandler<Run>((target, e) => _OnTextChanged(target, Run.TextProperty, e)),
        ];
        _opsec.Changed += _RenderAll;
    }

    public void Dispose()
    {
        _opsec.Changed -= _RenderAll;
        foreach (IDisposable handler in _classHandlers)
            handler.Dispose();
    }

    private void _OnTextChanged(AvaloniaObject target, StyledProperty<string?> property, AvaloniaPropertyChangedEventArgs e)
    {
        // Our own rendering arriving back here.
        if (ReferenceEquals(target, _renderingTarget))
            return;

        if (e.NewValue is string source && OpsecText.IsMarked(source))
        {
            _sources.AddOrUpdate(target, source);
            _Render(target, property, source);
        }
        else
            _sources.Remove(target);
    }

    private void _RenderAll()
    {
        List<KeyValuePair<AvaloniaObject, string>> marked = _sources.ToList();
        foreach ((AvaloniaObject target, string source) in marked)
            _Render(target, target is Run ? Run.TextProperty : TextBlock.TextProperty, source);
    }

    private void _Render(AvaloniaObject target, StyledProperty<string?> property, string source)
    {
        _renderingTarget = target;
        try
        {
            target.SetCurrentValue(property, _opsec.Render(source));
        }
        finally
        {
            _renderingTarget = null;
        }
    }
}
