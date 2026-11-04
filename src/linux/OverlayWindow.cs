using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using HapticLink.Core;

namespace HapticLink;

public sealed class OverlayWindow : Window
{
    private readonly Settings _s;
    private readonly Engine _engine;
    private readonly TextBlock _game = Label("No game selected"), _state = Label("DISARMED"), _event = Label("Waiting for events");
    private readonly ProgressBar[] _meters = new ProgressBar[5];
    private readonly StackPanel _buttons;
    private readonly EqualizerPanel _equalizer;
    private readonly ScrollViewer _eqContainer;
    private bool _showEq;
    private int _anchorX, _anchorY;
    private bool? _lastInteractive;
    public string InputStatus { get; private set; } = "";
    public bool InputAvailable { get; private set; } = true;
    public OverlayWindow(Settings settings, Engine engine)
    {
        _s = settings; _engine = engine;
        Title = "HapticLink overlay"; Width = 355; Height = 275; CanResize = false;
        SystemDecorations = SystemDecorations.None; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = Brushes.Transparent; TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        var content = new StackPanel { Spacing = 7, Margin = new Thickness(15) };
        var header = new TextBlock { Text = "HAPTICLINK", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Brushes.DeepSkyBlue };
        header.PointerPressed += (_, e) => { if (_s.OverlayInteractive && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        content.Children.Add(header); content.Children.Add(_game); content.Children.Add(_state);
        var names = new[] { "Pad", "Audio", "OSC", "Manual", "Events" };
        for (int i = 0; i < 5; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*") };
            row.Children.Add(Label(names[i]));
            _meters[i] = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_meters[i], 1); row.Children.Add(_meters[i]); content.Children.Add(row);
        }
        content.Children.Add(_event);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var arm = new Button { Content = "Arm / disarm" };
        arm.Click += (_, _) => { if (_engine.Panicked) _engine.ClearPanic(); _engine.Arm(!_engine.Armed); };
        var stop = new Button { Content = "PANIC", Background = Brushes.DarkRed };
        stop.Click += (_, _) => _engine.Panic(); _buttons.Children.Add(arm); _buttons.Children.Add(stop);
        var eqButton = new Button { Content = "EQ" };
        eqButton.Click += (_, _) => { _showEq = !_showEq; _eqContainer.IsVisible = _showEq; Width = _showEq ? 520 : 355; Height = _showEq ? 695 : 275; };
        _buttons.Children.Add(eqButton); content.Children.Add(_buttons);
        _equalizer = new EqualizerPanel(_s, _engine.Audio);
        _eqContainer = new ScrollViewer { Content = _equalizer, Height = 405, IsVisible = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        content.Children.Add(_eqContainer);
        Content = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.Parse("#F018202C")), BorderBrush = new SolidColorBrush(Color.Parse("#607DA5CF")), BorderThickness = new Thickness(1), Child = content };
        Opened += (_, _) => ApplyInputMode();
        SizeChanged += (_, _) => { if (IsVisible) ApplyInputMode(); };
        PositionChanged += (_, _) => { _s.OverlayOffsetX = Position.X - _anchorX; _s.OverlayOffsetY = Position.Y - _anchorY; };
    }
    public void Refresh(EngineState state, FocusState focus)
    {
        _game.Text = focus.Window?.Title ?? focus.Status;
        _state.Text = state.FocusPaused ? "PAUSED · game unfocused" : (state.Panicked ? "PANIC" : state.Armed ? "ARMED" : "DISARMED") + " · output " + state.Output.ToString("P0");
        for (int i = 0; i < 5; i++) _meters[i].Value = state.Shaped[i] * 100;
        _event.Text = _engine.Events.LastEvent; _buttons.IsVisible = _s.OverlayInteractive;
        if (!_s.OverlayInteractive && _showEq) { _showEq = false; _eqContainer.IsVisible = false; Width = 355; Height = 275; }
        _equalizer.Refresh();
        Opacity = Math.Clamp(_s.OverlayOpacity, .2, 1);
        _anchorX = focus.Window?.X ?? 0; _anchorY = focus.Window?.Y ?? 0;
        var point = new PixelPoint(_anchorX + _s.OverlayOffsetX, _anchorY + _s.OverlayOffsetY);
        if (Position != point) Position = point;
        if (_lastInteractive != _s.OverlayInteractive) ApplyInputMode();
    }
    private void ApplyInputMode()
    {
        _lastInteractive = _s.OverlayInteractive;
        try { OverlayInput.Set(this, !_s.OverlayInteractive); InputAvailable = true; InputStatus = _s.OverlayInteractive ? "Interactive — drag the header to move" : "Click-through"; }
        catch (Exception ex) { InputAvailable = false; InputStatus = "Click-through unavailable: " + ex.Message; if (!_s.OverlayInteractive) Hide(); }
    }
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
}

internal static class OverlayInput
{
    public static void Set(Window window, bool through)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.HandleDescriptor == "Headless") return;
        if (OperatingSystem.IsWindows())
        {
            var flags = GetWindowLongPtr(handle.Handle, -20).ToInt64();
            flags = through ? flags | 0x20 | 0x08000000 : flags & ~0x20 & ~0x08000000;
            Marshal.SetLastPInvokeError(0); SetWindowLongPtr(handle.Handle, -20, new IntPtr(flags));
            if (Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
            return;
        }
        if (!OperatingSystem.IsLinux() || handle.HandleDescriptor != "X11Window") throw new NotSupportedException("Use interactive mode on this window backend");
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) throw new InvalidOperationException("No X11 display");
        try
        {
            if (through) XShapeCombineRectangles(display, handle.Handle, 2, 0, 0, IntPtr.Zero, 0, 0, 0);
            else
            {
                var rectangle = new XRectangle { Width = (ushort)Math.Min(ushort.MaxValue, window.Bounds.Width * window.RenderScaling), Height = (ushort)Math.Min(ushort.MaxValue, window.Bounds.Height * window.RenderScaling) };
                XShapeCombineRectangle(display, handle.Handle, 2, 0, 0, ref rectangle, 1, 0, 0);
            }
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct XRectangle { public short X, Y; public ushort Width, Height; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr rectangles, int count, int op, int order);
    [DllImport("libXext.so.6", EntryPoint = "XShapeCombineRectangles")] private static extern void XShapeCombineRectangle(IntPtr display, IntPtr window, int kind, int x, int y, ref XRectangle rectangle, int count, int op, int order);
}
