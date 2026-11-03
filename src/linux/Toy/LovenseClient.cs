using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HapticLink.Core;

namespace HapticLink.Toy
{
    public class ToyInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string NickName { get; set; } = "";
        public bool Connected { get; set; }
        public int Battery { get; set; } = -1;
        public List<string> Functions { get; set; } = new();

        public string Display
        {
            get
            {
                var label = string.IsNullOrWhiteSpace(NickName) ? Name : $"{NickName} ({Name})";
                var batt = Battery >= 0 ? $" — {Battery}%" : "";
                return $"{label}{batt}{(Connected ? "" : " — offline")}";
            }
        }
    }

    /// <summary>
    /// Client for the Lovense Remote "Game Mode" local API.
    /// Tries plain HTTP on :20010 first, then the CA-signed https://&lt;dashed-ip&gt;.lovense.club:30010,
    /// then raw-IP HTTPS with validation relaxed (some app builds ship a stale cert).
    /// </summary>
    public class LovenseClient : IDisposable
    {
        private const string Platform = "HapticLink";

        private readonly HttpClient _strict;
        private readonly HttpClient _relaxed;

        public string BaseUrl { get; private set; } = "";
        public bool IsConnected => !string.IsNullOrEmpty(BaseUrl);
        public List<ToyInfo> Toys { get; private set; } = new();

        private int _lastSentLevel = -1;
        private DateTime _lastSendUtc = DateTime.MinValue;
        // per toy id ("" = broadcast): the action string last sent and when, for change detection and keepalive
        private readonly ConcurrentDictionary<string, (string action, DateTime at)> _lastPerToy = new();

        public LovenseClient()
        {
            _strict = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            _strict.DefaultRequestHeaders.Add("X-platform", Platform);

            var relaxedHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _relaxed = new HttpClient(relaxedHandler) { Timeout = TimeSpan.FromSeconds(4) };
            _relaxed.DefaultRequestHeaders.Add("X-platform", Platform);
        }

        private HttpClient ClientFor(string url) =>
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? _relaxed : _strict;

        public static string DashedHost(string ip) => ip.Replace('.', '-') + ".lovense.club";

        /// <summary>Candidate base URLs for a host, in preference order.</summary>
        public static List<string> Candidates(string host, int httpPort, int httpsPort, bool preferHttps)
        {
            var http = $"http://{host}:{httpPort}";
            var httpsDomain = $"https://{DashedHost(host)}:{httpsPort}";
            var httpsRaw = $"https://{host}:{httpsPort}";
            return preferHttps
                ? new List<string> { httpsDomain, httpsRaw, http }
                : new List<string> { http, httpsDomain, httpsRaw };
        }

        /// <summary>Probe each candidate URL with a GetToys call; the first that answers wins.</summary>
        public async Task<bool> ConnectAsync(Settings s, CancellationToken ct = default)
        {
            BaseUrl = "";
            if (string.IsNullOrWhiteSpace(s.Host)) return false;

            foreach (var baseUrl in Candidates(s.Host.Trim(), s.HttpPort, s.HttpsPort, s.PreferHttps))
            {
                if (ct.IsCancellationRequested) return false;
                try
                {
                    var json = await PostAsync(baseUrl, "{\"command\":\"GetToys\"}", ct).ConfigureAwait(false);
                    if (json == null) continue;

                    Toys = ParseToys(json);
                    BaseUrl = baseUrl;
                    Log.Write($"Connected via {baseUrl} — {Toys.Count} toy(s): " +
                              string.Join(", ", Toys.Select(t => t.Display)));
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Write($"  probe {baseUrl} failed: {Short(ex)}");
                }
            }
            Log.Write("No Lovense endpoint answered. Is Game Mode on and the phone on this network?");
            return false;
        }

        public async Task<bool> RefreshToysAsync(CancellationToken ct = default)
        {
            if (!IsConnected) return false;
            try
            {
                var json = await PostAsync(BaseUrl, "{\"command\":\"GetToys\"}", ct).ConfigureAwait(false);
                if (json == null) return false;
                Toys = ParseToys(json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("RefreshToys failed: " + Short(ex));
                return false;
            }
        }

        /// <summary>
        /// Send an intensity 0..1. Returns false if nothing was sent (unchanged and keepalive not due).
        /// timeSec acts as a dead-man switch: if this app stops, the toy stops by itself.
        /// </summary>
        public async Task<bool> SetLevelAsync(double level01, Settings s, bool force = false, CancellationToken ct = default)
        {
            if (!IsConnected) return false;

            var lvl = (int)Math.Round(Sig.Clamp01(level01) * 20.0);
            var keepaliveDue = (DateTime.UtcNow - _lastSendUtc).TotalSeconds >= Math.Max(1, s.DeadManSeconds) / 2.0;
            if (!force && lvl == _lastSentLevel && !keepaliveDue) return false;

            // timeSec must be 0 or >= 1. Use the dead-man window for any non-zero level.
            var timeSec = lvl > 0 ? Math.Max(1, s.DeadManSeconds) : 0;
            var action = lvl > 0 ? $"{s.Action}:{lvl}" : $"{s.Action}:0";

            var sb = new StringBuilder();
            sb.Append("{\"command\":\"Function\",\"action\":\"").Append(action)
              .Append("\",\"timeSec\":").Append(timeSec)
              .Append(",\"apiVer\":1");
            // stopPrevious defaults to 1, which makes the Remote app halt the motor before every
            // new level — an audible stop/start on each change. 0 lets the level glide. Zero still
            // goes out with the default so silence clears anything left running.
            if (lvl > 0) sb.Append(",\"stopPrevious\":0");
            if (!string.IsNullOrWhiteSpace(s.ToyId))
                sb.Append(",\"toy\":\"").Append(s.ToyId).Append('"');
            sb.Append('}');

            try
            {
                await PostAsync(BaseUrl, sb.ToString(), ct).ConfigureAwait(false);
                _lastSentLevel = lvl;
                _lastSendUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Send failed: " + Short(ex));
                return false;
            }
        }

        /// <summary>
        /// Send every toy its own levels — one command per toy, its motors joined into a single action
        /// ("Vibrate1:6,Vibrate2:14"). A toy is skipped while its action is unchanged and its keepalive
        /// isn't due. Every non-zero command carries the dead-man timeSec, as SetLevelAsync does.
        /// </summary>
        public async Task SetOutputsAsync(IReadOnlyList<Channel> channels, Settings s, CancellationToken ct = default)
        {
            if (!IsConnected || channels == null) return;

            foreach (var toy in channels.GroupBy(c => c.ToyId ?? ""))
            {
                var parts = toy.Select(c => (c.Function, lvl: (int)Math.Round(Sig.Clamp01(c.Output) * 20.0))).ToList();
                var action = string.Join(",", parts.Select(x => $"{x.Function}:{x.lvl}"));
                var running = parts.Any(x => x.lvl > 0);

                _lastPerToy.TryGetValue(toy.Key, out var last);
                var keepaliveDue = (DateTime.UtcNow - last.at).TotalSeconds >= Math.Max(1, s.DeadManSeconds) / 2.0;
                if (action == last.action && !keepaliveDue) continue;

                var sb = new StringBuilder();
                sb.Append("{\"command\":\"Function\",\"action\":\"").Append(action)
                  .Append("\",\"timeSec\":").Append(running ? Math.Max(1, s.DeadManSeconds) : 0)
                  .Append(",\"apiVer\":1");
                if (running) sb.Append(",\"stopPrevious\":0"); // see SetLevelAsync
                if (toy.Key.Length > 0) sb.Append(",\"toy\":\"").Append(toy.Key).Append('"');
                sb.Append('}');

                try
                {
                    await PostAsync(BaseUrl, sb.ToString(), ct).ConfigureAwait(false);
                    _lastPerToy[toy.Key] = (action, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    Log.Write("Send failed: " + Short(ex));
                }
            }

            // A toy that dropped out of the list keeps no entry, so it is sent afresh if it comes back.
            foreach (var gone in _lastPerToy.Keys.Where(k => channels.All(c => (c.ToyId ?? "") != k)).ToList())
                _lastPerToy.TryRemove(gone, out _);
        }

        /// <summary>Hard stop — used by panic, disconnect and app shutdown.</summary>
        public async Task StopAsync(CancellationToken ct = default)
        {
            if (!IsConnected) return;
            try
            {
                await PostAsync(BaseUrl, "{\"command\":\"Function\",\"action\":\"Stop\",\"timeSec\":0,\"apiVer\":1}", ct)
                    .ConfigureAwait(false);
                _lastSentLevel = 0;
                _lastSendUtc = DateTime.UtcNow;
                _lastPerToy.Clear();
            }
            catch (Exception ex)
            {
                Log.Write("Stop failed: " + Short(ex));
            }
        }

        public void Disconnect()
        {
            BaseUrl = "";
            Toys = new List<ToyInfo>();
            _lastSentLevel = -1;
            _lastPerToy.Clear();
        }

        private async Task<string> PostAsync(string baseUrl, string body, CancellationToken ct)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await ClientFor(baseUrl)
                .PostAsync(baseUrl.TrimEnd('/') + "/command", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The GetToys envelope differs across app builds. Observed shapes include a bare
        /// id-keyed map, {"data":{...}}, {"data":"{...}"} and — what the iOS Remote app
        /// actually sends — {"data":{"toys":"{...}"}} with the map double-encoded as a string.
        /// Rather than special-case each, walk the response and take the first id-keyed toy
        /// map we find, decoding embedded JSON strings on the way down.
        /// </summary>
        public static List<ToyInfo> ParseToys(string json)
        {
            var result = new List<ToyInfo>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                Collect(doc.RootElement, result, 0);
            }
            catch (Exception ex)
            {
                Log.Write("Toy list parse failed: " + ex.Message);
            }
            return result;
        }

        private static void Collect(JsonElement el, List<ToyInfo> outp, int depth)
        {
            if (depth > 5 || outp.Count > 0) return;

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s) || !s.TrimStart().StartsWith("{")) return;
                try
                {
                    using var inner = JsonDocument.Parse(s);
                    Collect(inner.RootElement, outp, depth + 1);
                }
                catch { /* not JSON after all */ }
                return;
            }

            if (el.ValueKind != JsonValueKind.Object) return;

            var direct = ParseToyMap(el);
            if (direct.Count > 0) { outp.AddRange(direct); return; }

            foreach (var prop in el.EnumerateObject())
            {
                Collect(prop.Value, outp, depth + 1);
                if (outp.Count > 0) return;
            }
        }

        private static List<ToyInfo> ParseToyMap(JsonElement map)
        {
            var list = new List<ToyInfo>();
            if (map.ValueKind != JsonValueKind.Object) return list;

            foreach (var prop in map.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var v = prop.Value;
                // skip envelope fields that aren't toys
                if (!v.TryGetProperty("id", out _) && !v.TryGetProperty("name", out _)) continue;

                var toy = new ToyInfo { Id = GetStr(v, "id") ?? prop.Name };
                toy.Name = GetStr(v, "name") ?? "";
                toy.NickName = GetStr(v, "nickName") ?? "";
                toy.Connected = (GetStr(v, "status") ?? "1") == "1";

                if (v.TryGetProperty("battery", out var b))
                {
                    if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var bi)) toy.Battery = bi;
                    else if (b.ValueKind == JsonValueKind.String && int.TryParse(b.GetString(), out var bs)) toy.Battery = bs;
                }

                foreach (var key in new[] { "fullFunctionNames", "shortFunctionNames" })
                {
                    if (toy.Functions.Count == 0 && v.TryGetProperty(key, out var fn) && fn.ValueKind == JsonValueKind.Array)
                        foreach (var f in fn.EnumerateArray())
                            if (f.ValueKind == JsonValueKind.String) toy.Functions.Add(f.GetString());
                }
                list.Add(toy);
            }
            return list;
        }

        private static string GetStr(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null
            };
        }

        private static string Short(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        // ---------------- LAN discovery ----------------

        /// <summary>
        /// Scan the local /24 for a host listening on the Lovense ports, so the user doesn't
        /// have to read the IP off their phone. Returns responsive hosts.
        /// </summary>
        public static async Task<List<string>> DiscoverAsync(int httpPort, int httpsPort,
            IProgress<string> progress = null, CancellationToken ct = default)
        {
            var found = new List<string>();
            var localIps = LocalIPv4Addresses();
            if (localIps.Count == 0)
            {
                Log.Write("Discovery: no usable IPv4 interface found.");
                return found;
            }

            foreach (var local in localIps)
            {
                var parts = local.Split('.');
                if (parts.Length != 4) continue;
                var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";
                progress?.Report($"Scanning {prefix}0/24 …");

                var tasks = new List<Task<string>>();
                for (int i = 1; i <= 254; i++)
                {
                    var ip = prefix + i;
                    tasks.Add(ProbeHost(ip, httpPort, httpsPort, ct));
                }
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var r in results)
                    if (r != null && !found.Contains(r)) found.Add(r);
            }

            Log.Write(found.Count > 0
                ? "Discovery found: " + string.Join(", ", found)
                : "Discovery found nothing — check Game Mode is on and both devices are on the same Wi-Fi/LAN.");
            return found;
        }

        private static async Task<string> ProbeHost(string ip, int httpPort, int httpsPort, CancellationToken ct)
        {
            foreach (var port in new[] { httpPort, httpsPort })
            {
                try
                {
                    using var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(ip, port, ct).AsTask();
                    var done = await Task.WhenAny(connect, Task.Delay(700, ct)).ConfigureAwait(false);
                    if (done == connect && tcp.Connected) return ip;
                }
                catch { /* closed / unreachable */ }
            }
            return null;
        }

        private static List<string> LocalIPv4Addresses()
        {
            var list = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254")) continue;
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            return list;
        }

        public void Dispose()
        {
            _strict?.Dispose();
            _relaxed?.Dispose();
        }
    }
}
