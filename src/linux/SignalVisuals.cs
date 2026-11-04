using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HapticLink;

internal static class Classic
{
    public static readonly IBrush Background = Brush("#12141A"), Panel = Brush("#1A1D26"), Line = Brush("#2E3444"),
        Text = Brush("#D8DCE6"), Muted = Brush("#8A92A6"), Accent = Brush("#4EA1FF"),
        Good = Brush("#46D07A"), Warn = Brush("#FFB454"), Danger = Brush("#FF5C5C"), Track = Brush("#0E1016");
    public static readonly FontFamily Mono = new("Consolas, DejaVu Sans Mono");
    public static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}

// The gradient stays fixed to the full scale, so quiet signals remain green.
internal sealed class SignalMeter : Control
{
    private double _value;
    private static readonly IBrush Fill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops { new(Color.Parse("#3AD07A"), 0), new(Color.Parse("#8BD649"), .45), new(Color.Parse("#FFB454"), .75), new(Color.Parse("#FF5C5C"), 1) }
    };
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(Classic.Track, null, rect, 3, 3);
        using (context.PushClip(new Rect(0, 0, rect.Width * _value, rect.Height))) context.DrawRectangle(Fill, null, rect, 3, 3);
    }
}

internal sealed class HistoryChart : Control
{
    private double[] _values = Array.Empty<double>();
    public void Update(double[] values) { _values = values; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(Classic.Track, new Pen(Classic.Line), rect, 3, 3);
        var grid = new Pen(Classic.Line, .5);
        for (int i = 1; i < 4; i++) context.DrawLine(grid, new Point(1, rect.Height * i / 4), new Point(rect.Width - 1, rect.Height * i / 4));
        if (_values.Length < 2) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (int i = 0; i < _values.Length; i++)
            {
                var point = new Point(1 + (rect.Width - 2) * i / (_values.Length - 1), 1 + (rect.Height - 2) * (1 - Math.Clamp(_values[i], 0, 1)));
                if (i == 0) path.BeginFigure(point, false); else path.LineTo(point);
            }
            path.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Classic.Accent, 1.4), geometry);
    }
}
