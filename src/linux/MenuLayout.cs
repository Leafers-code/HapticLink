using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HapticLink;

public static class MenuLayout
{
    private static bool _installed;
    public static void Install()
    {
        if (_installed) return; _installed = true;
        Expander.ExpandedEvent.AddClassHandler<Expander>((expander, e) =>
        {
            if (ReferenceEquals(e.Source, expander)) Dispatcher.UIThread.Post(() => Reveal(expander), DispatcherPriority.Loaded);
        });
    }
    private static void Reveal(Expander expander)
    {
        if (!expander.IsExpanded || !expander.IsEffectivelyVisible) return;
        var scroll = expander.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll == null) return;
        scroll.UpdateLayout();
        var viewport = expander.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
        var point = viewport == null ? null : expander.TranslatePoint(new Point(), viewport);
        if (!point.HasValue) return;
        // Reveal a short section completely; start a tall section at its header.
        var height = Math.Min(expander.Bounds.Height, Math.Max(0, viewport.Bounds.Height - 16));
        var top = point.Value.Y; var bottom = top + height;
        var delta = top < 8 ? top - 8 : Math.Max(0, bottom - viewport.Bounds.Height + 8);
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + delta));
    }
}
