using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HapticLink.Core;

public sealed record GameWindow(string Id, int ProcessId, string Title, int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Title} · PID {ProcessId}";
}
public sealed record FocusState(bool Available, bool Focused, GameWindow Window, string Status)
{
    public bool Pauses(Settings s) => s.PauseWhenGameUnfocused && (!Available || !Focused);
}

public sealed class GameFocus : IDisposable
{
    private readonly Settings _s;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop;
    private FocusState _state = new(false, false, null, "No game selected");
    public FocusState State => Volatile.Read(ref _state);
    public GameFocus(Settings settings) => _s = settings;
    public void Start()
    {
        if (_loop != null) return;
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                FocusState state;
                try { state = Snapshot(_s); }
                catch (Exception ex) { state = new(false, false, null, "Focus unavailable: " + ex.Message); }
                Volatile.Write(ref _state, state);
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        });
    }

    public static List<GameWindow> ListWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = new List<GameWindow>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                var text = new StringBuilder(1024); GetWindowText(window, text, text.Capacity);
                if (text.Length == 0) return true;
                GetWindowThreadProcessId(window, out var pid); GetWindowRect(window, out var rect);
                result.Add(new(window.ToInt64().ToString("x"), (int)pid, text.ToString(), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                return true;
            }, IntPtr.Zero);
            return result;
        }
        if (!OperatingSystem.IsLinux()) return new();
        return ParseWindows(LinuxProcess.Run("wmctrl", "-lpG"));
    }

    public static List<GameWindow> ParseWindows(string text)
    {
        var result = new List<GameWindow>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split((char[])null, 9, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 9 || !int.TryParse(p[2], out var pid)) continue;
            if (!int.TryParse(p[3], out var x) || !int.TryParse(p[4], out var y) || !int.TryParse(p[5], out var width) || !int.TryParse(p[6], out var height)) continue;
            result.Add(new(NormalizeId(p[0]), pid, p[8], x, y, width, height));
        }
        return result;
    }
    public static string NormalizeId(string id) => ulong.TryParse(id?.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value.ToString("x") : "";

    private static FocusState Snapshot(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.GameWindowId)) return new(false, false, null, "Select a game window");
        var window = ListWindows().FirstOrDefault(w => w.Id == NormalizeId(s.GameWindowId));
        if (window == null) return new(false, false, null, "Selected game window closed — select it again after restarting");
        var active = OperatingSystem.IsWindows() ? GetForegroundWindow().ToInt64().ToString("x")
            : NormalizeId(LinuxProcess.Run("xprop", "-root", "_NET_ACTIVE_WINDOW").Split(' ').Last().Trim());
        var focused = active == window.Id;
        return new(true, focused, window, focused ? "Game focused" : "Game unfocused");
    }
    public void Dispose() { _cts.Cancel(); try { _loop?.Wait(4500); } catch { } _cts.Dispose(); }
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
}
