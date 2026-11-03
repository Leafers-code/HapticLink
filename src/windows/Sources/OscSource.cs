using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HapticLink.Core;

namespace HapticLink.Sources
{
    /// <summary>
    /// Listens for VRChat's OSC output (default port 9001) and maps chosen avatar
    /// parameters to intensity. Every address seen is recorded so the UI can offer
    /// a pick-list instead of making you type parameter names.
    /// </summary>
    public partial class OscSource : SourceBase
    {
        public override string Name => "OSC";

        private readonly Settings _s;
        private UdpClient _udp;
        private Thread _rx;
        private volatile bool _stop;

        /// <summary>Every address seen this session, with its latest value — drives the UI pick-list.</summary>
        public ConcurrentDictionary<string, double> Seen { get; } = new();

        private readonly ConcurrentDictionary<string, double> _values = new();
        private DateTime _lastPacketUtc = DateTime.MinValue;
        private long _packets;

        // VRCFury SPS2 avatars hold every OGB/* contact parameter at 0 until an app sets
        // OGB_ENABLED. VRChat forgets it whenever the avatar reloads, so it goes out as a
        // slow heartbeat rather than once.
        private static readonly byte[] OgbEnableMsg = BuildBoolMessage("/avatar/parameters/OGB_ENABLED", true);
        private const double OgbHeartbeatMs = 2000;
        private double _sinceOgbMs = OgbHeartbeatMs;

        private const string AvatarChange = "/avatar/change";

        private volatile string _top;          // what is currently driving Raw, for the status line

        private volatile string _avatarId;     // null until VRChat reports one or AskVrchat finds it
        private volatile string _avatarName;
        private volatile bool _avatarFileRead;
        private int _avatarLookupTries;
        private double _sinceLookupMs;

        /// <summary>The avatar VRChat is wearing, or null while that isn't known yet.</summary>
        public string AvatarId => _avatarId;
        /// <summary>Its display name once VRChat's parameter file has been read; null before that.</summary>
        public string AvatarName => _avatarName;

        private const double AskVrchatMs = 5000;
        private double _sinceAskMs = AskVrchatMs;
        private int _asking;                   // 0/1 guard: one OSCQuery lookup at a time
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

        public OscSource(Settings s) => _s = s;

        public override void Start()
        {
            if (Running) return;
            _stop = false;
            _packets = 0;
            _sinceOgbMs = OgbHeartbeatMs; // send on the first tick
            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork);
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _s.OscPort));

                Available = true;
                Running = true;
                Status = $"Listening on UDP {_s.OscPort}";
                Log.Write("OSC: " + Status);

                _rx = new Thread(Receive) { IsBackground = true, Name = "HapticLink.OscRx" };
                _rx.Start();
            }
            catch (SocketException ex)
            {
                Available = false;
                Status = $"Port {_s.OscPort} unavailable: {ex.SocketErrorCode}";
                Log.Write("OSC: " + Status + " (another app may already own it)");
                Cleanup();
            }
            catch (Exception ex)
            {
                Available = false;
                Status = "Failed: " + ex.Message;
                Log.Write("OSC: " + ex.Message);
                Cleanup();
            }
        }

        private void Receive()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                try
                {
                    var data = _udp.Receive(ref any);
                    _lastPacketUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref _packets);
                    ParsePacket(data, 0, data.Length);
                }
                catch (SocketException) { if (!_stop) Thread.Sleep(50); }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log.Write("OSC receive error: " + ex.Message); Thread.Sleep(100); }
            }
        }

        // ---------------- OSC 1.0 parsing ----------------

        private void ParsePacket(byte[] b, int start, int end)
        {
            if (end - start < 4) return;

            if (b[start] == '#') // "#bundle\0" <timetag:8> then [int32 size][element]...
            {
                var p = start + 8 + 8;
                while (p + 4 <= end)
                {
                    var size = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p, 4));
                    p += 4;
                    if (size < 0 || p + size > end) break;
                    ParsePacket(b, p, p + size);
                    p += size;
                }
                return;
            }

            var pos = start;
            var address = ReadOscString(b, ref pos, end);
            if (address == null || pos >= end) return;

            var tags = ReadOscString(b, ref pos, end);
            if (tags == null || !tags.StartsWith(",")) return;

            double? value = null;
            string text = null;
            for (int i = 1; i < tags.Length; i++)
            {
                switch (tags[i])
                {
                    case 'f':
                        if (pos + 4 > end) return;
                        value ??= BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(pos, 4));
                        pos += 4;
                        break;
                    case 'i':
                        if (pos + 4 > end) return;
                        value ??= BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos, 4));
                        pos += 4;
                        break;
                    case 'd':
                        if (pos + 8 > end) return;
                        value ??= BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(pos, 8));
                        pos += 8;
                        break;
                    case 'T': value ??= 1.0; break;
                    case 'F': value ??= 0.0; break;
                    case 's':
                    case 'S':
                        var str = ReadOscString(b, ref pos, end);
                        text ??= str;
                        break;
                    case 'b':
                        if (pos + 4 > end) return;
                        var blob = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos, 4));
                        pos += 4 + Pad4(blob);
                        break;
                }
            }

            if (address == AvatarChange)
            {
                if (text != null) OnAvatarChanged(text);
                return;
            }

            if (value == null) return;
            var v = Sig.Clamp01(value.Value);
            Seen[address] = v;
            _values[address] = v;
            NotePartValue(address);
        }

        /// <summary>
        /// VRChat only announces the avatar when it changes, so launching after VRChat would leave it
        /// unknown until the next switch. VRChat's OSCQuery server (HTTP, on the one TCP port the
        /// process listens on) answers the question directly.
        /// </summary>
        private void AskVrchatForAvatar(double dtMs)
        {
            if (_avatarId != null) return;
            _sinceAskMs += dtMs;
            if (_sinceAskMs < AskVrchatMs) return;
            _sinceAskMs = 0;
            if (Interlocked.Exchange(ref _asking, 1) == 1) return;

            Task.Run(async () =>
            {
                try
                {
                    foreach (var vrc in Process.GetProcessesByName("VRChat"))
                        foreach (var port in ListeningTcpPorts(vrc.Id))
                        {
                            var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/avatar/change").ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(json);
                            if (!doc.RootElement.TryGetProperty("VALUE", out var val) || val.ValueKind != JsonValueKind.Array) continue;
                            var id = val.EnumerateArray().Select(e => e.GetString()).FirstOrDefault();
                            if (!string.IsNullOrEmpty(id) && _avatarId == null && Running)
                            {
                                OnAvatarChanged(id);
                                return;
                            }
                        }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                               or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // VRChat not up yet, or OSCQuery still starting — asked again in a few seconds
                }
                finally { Interlocked.Exchange(ref _asking, 0); }
            });
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        private static List<int> ListeningTcpPorts(int pid)
        {
            const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3, RowSize = 24;
            var ports = new List<int>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return ports;
                var rows = Marshal.ReadInt32(buf);
                for (int i = 0; i < rows; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid
                    var row = buf + 4 + i * RowSize;
                    if (Marshal.ReadInt32(row, 20) != pid) continue;
                    var p = Marshal.ReadInt32(row, 8);              // network byte order in the low word
                    ports.Add(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return ports;
        }

        private void OnAvatarChanged(string avatarId)
        {
            // Every parameter belongs to the avatar that sent it; none of it carries over.
            _values.Clear();
            ClearParts();
            Seen.Clear();
            _top = null;
            Raw = 0;

            _avatarId = avatarId;
            _avatarName = null;
            _avatarFileRead = false;
            _avatarLookupTries = 0;
            _sinceLookupMs = AvatarLookupMs;   // look on the next tick
            _sinceOgbMs = OgbHeartbeatMs;      // the new avatar starts with OGB_ENABLED off
            Log.Write("OSC: avatar changed to " + avatarId);
        }

        private const double AvatarLookupMs = 1000;

        /// <summary>
        /// VRChat writes each avatar's parameter list to LocalLow\VRChat\VRChat\OSC\usr_*\Avatars\{id}.json.
        /// Reading it gives the avatar's name and its sensors up front, instead of waiting for a touch
        /// to find out whether the avatar has any. The file can lag the change event, hence the retries.
        /// </summary>
        private void TryReadAvatarFile(double dtMs)
        {
            var id = _avatarId;
            if (id == null || _avatarFileRead || _avatarLookupTries >= 10) return;
            _sinceLookupMs += dtMs;
            if (_sinceLookupMs < AvatarLookupMs) return;
            _sinceLookupMs = 0;
            _avatarLookupTries++;

            try
            {
                var oscDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                    "VRChat", "VRChat", "OSC");
                if (!Directory.Exists(oscDir)) return;
                var file = Directory.EnumerateFiles(oscDir, id + ".json", SearchOption.AllDirectories).FirstOrDefault();
                if (file == null) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(file)); // ReadAllText eats VRChat's BOM
                var root = doc.RootElement;
                var found = new List<string>();
                if (root.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var p in ps.EnumerateArray())
                    {
                        // "name" keeps spaces; the address is what actually arrives (Dragon Penis → Dragon_Penis)
                        if (!p.TryGetProperty("output", out var o) || !o.TryGetProperty("address", out var a)) continue;
                        var addr = a.GetString();
                        if (addr != null && PartPattern.IsMatch(addr)) found.Add(addr);
                    }

                if (id != _avatarId) return; // changed again while reading
                foreach (var addr in found) RegisterPart(addr);
                _avatarName = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                _avatarFileRead = true;
                Log.Write(_parts.IsEmpty
                    ? $"OSC: {_avatarName ?? id} has no SPS parts."
                    : $"OSC: {_avatarName ?? id} has {_parts.Count} SPS part(s): {PartSummary()}");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // half-written file or a sharing violation — the next try will get it
            }
        }

        private static int Pad4(int n) => (n + 3) & ~3;

        private static string ReadOscString(byte[] b, ref int pos, int end)
        {
            int s = pos;
            while (pos < end && b[pos] != 0) pos++;
            if (pos >= end) return null;
            var str = Encoding.ASCII.GetString(b, s, pos - s);
            pos = s + Pad4(pos - s + 1);
            return str;
        }

        /// <summary>Says in plain words whether the current avatar can drive the toy, and what is doing it.</summary>
        private string Describe()
        {
            var byHand = _s.OscParams?.Length ?? 0;
            if (!_s.OscAutoSensors)
                return $"Auto-detect is off — {Seen.Count} parameters seen, {byHand} ticked by hand";

            var who = _avatarName ?? "This avatar";
            var usable = _parts.Count;
            if (usable == 0)
            {
                if (_avatarFileRead)
                    return $"{who} has no SPS parts, so it can't drive the toy — switch to an SPS avatar"
                         + (byHand > 0 ? $" ({byHand} ticked by hand still apply)" : "");
                return "Connected to VRChat — working out which avatar you're wearing…";
            }

            var top = _top;
            var line = $"{who}: {usable} SPS part{(usable == 1 ? "" : "s")} ready";
            var build = _s.OscBuildUp ? $" · build-up {_arousal * 100:0}%" : "";
            return top != null && Raw > 0.01
                ? $"{line} — {ShortName(top)} {Raw * 100:0}%{build}"
                : $"{line} — waiting for a touch{build}";
        }

        private static string ShortName(string address)
        {
            const string ogb = "/avatar/parameters/OGB/", any = "/avatar/parameters/";
            if (address.StartsWith(ogb, StringComparison.Ordinal)) return address.Substring(ogb.Length);
            return address.StartsWith(any, StringComparison.Ordinal) ? address.Substring(any.Length) : address;
        }

        private void SendOgbEnable(double dtMs)
        {
            if (!_s.OscOgbEnable) return;
            _sinceOgbMs += dtMs;
            if (_sinceOgbMs < OgbHeartbeatMs) return;
            _sinceOgbMs = 0;
            try { _udp?.Send(OgbEnableMsg, OgbEnableMsg.Length, new IPEndPoint(IPAddress.Loopback, _s.OscSendPort)); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private static byte[] BuildBoolMessage(string address, bool value)
        {
            var a = Encoding.ASCII.GetBytes(address);
            var tagsAt = Pad4(a.Length + 1);
            var msg = new byte[tagsAt + 4]; // address, then ",T" or ",F" padded to 4
            a.CopyTo(msg, 0);
            msg[tagsAt] = (byte)',';
            msg[tagsAt + 1] = (byte)(value ? 'T' : 'F');
            return msg;
        }

        public override void Tick(double dtMs)
        {
            if (!Running) return;
            SendOgbEnable(dtMs);
            if (Interlocked.Read(ref _packets) == 0)
            {
                Status = $"Waiting for VRChat on port {_s.OscPort} — in VRChat: Action menu → Options → OSC → Enabled";
                return;
            }
            AskVrchatForAvatar(dtMs);
            TryReadAvatarFile(dtMs);
            Recompute(dtMs);
            var quiet = (DateTime.UtcNow - _lastPacketUtc).TotalSeconds;
            Status = quiet > 5 ? $"Nothing from VRChat for {quiet:0}s — is it still running?" : Describe();
        }

        public List<string> SeenAddresses() => Seen.Keys.OrderBy(k => k).ToList();

        public override void Stop()
        {
            _stop = true;
            Cleanup();
            try { _rx?.Join(200); } catch { }
            _rx = null;
            Running = false;
            Raw = 0;
            _values.Clear();
            ClearParts();
            _arousal = 0;
            _top = null;
            _avatarId = null;
            _avatarName = null;
            _avatarFileRead = false;
            if (Available) Status = "Stopped";
        }

        private void Cleanup()
        {
            try { _udp?.Close(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _udp = null;
        }
    }
}
