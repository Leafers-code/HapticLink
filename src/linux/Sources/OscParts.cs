using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HapticLink.Core;

namespace HapticLink.Sources
{
    // The SPS side of the OSC source: groups an avatar's OGB parameters into parts (one plug or
    // socket each), works out what is happening to every part, and reports it per body zone so the
    // engine can send each toy the parts that belong to it. How a part's parameters are read follows
    // OscGoesBrrr's GameDevice — SPS was designed against that reader, so its gates are the contract.
    public partial class OscSource
    {
        private static readonly Regex PartPattern = new(
            @"^/avatar/parameters/OGB/(Pen|Orf)/(.+)/([^/]+)$", RegexOptions.Compiled);
        private const string OgbPrefix = "/avatar/parameters/OGB/";

        // A sensor's value is a position (how deep, how close). Held contact is a flat line, and a flat
        // line as vibration feels like a switch left on — what reads as touch is the movement. So each
        // source also carries a smoothed speed, and OscMotion sets how much of the output follows it.
        private sealed class Motion { public double Last, Speed; public bool Primed; }
        private const double MotionSmoothMs = 120;

        private const int TouchSelf = 0, TouchOthers = 1, PenSelf = 2, PenOthers = 3, FrotOthers = 4, SourceCount = 5;
        private static readonly string[] SourceNames = { "own touch", "touch", "own pen", "pen", "frot" };

        private sealed class Part
        {
            public string Prefix, Id;
            public bool IsPlug;
            public Zone Zone;
            /// <summary>Which parameters this part has — from VRChat's avatar file, plus any that arrive.</summary>
            public readonly ConcurrentDictionary<string, byte> Keys = new();
            public readonly LengthDetector SelfLength = new(), OthersLength = new();
            public readonly Motion[] Motions = Enumerable.Range(0, SourceCount).Select(_ => new Motion()).ToArray();
            public string Label => (IsPlug ? "" : Zone + "/") + Id;
        }

        private readonly ConcurrentDictionary<string, Part> _parts = new();
        private readonly ConcurrentDictionary<string, (Part part, string key)> _partOf = new();
        private double _arousal;

        /// <summary>Strongest thing happening in each body zone, feel and build-up applied. Indexed by <see cref="Zone"/>.</summary>
        public double[] ZoneLevels { get; private set; } = new double[Zones.Count];
        /// <summary>How deep the penetration driving each zone is, NaN when it is a touch. Two-motor toys travel on this.</summary>
        public double[] ZoneDepths { get; private set; } = NoDepths();
        /// <summary>Which zones the worn avatar has any part in — lets the router notice a toy has nothing to follow.</summary>
        public bool[] ZonesPresent { get; private set; } = new bool[Zones.Count];
        /// <summary>Hand-ticked parameters. They belong to no zone, so they reach every toy in full.</summary>
        public double TickedLevel { get; private set; }

        private static double[] NoDepths() => Enumerable.Repeat(double.NaN, Zones.Count).ToArray();

        private void ClearParts()
        {
            _parts.Clear();
            _partOf.Clear();
            ZoneLevels = new double[Zones.Count];
            ZoneDepths = NoDepths();
            ZonesPresent = new bool[Zones.Count];
            TickedLevel = 0;
        }

        private Part RegisterPart(string address) => RegisterPart(address, out _);

        private Part RegisterPart(string address, out string key)
        {
            key = null;
            if (!address.StartsWith(OgbPrefix, StringComparison.Ordinal)) return null;
            if (_partOf.TryGetValue(address, out var known)) { key = known.key; return known.part; }

            var m = PartPattern.Match(address);
            if (!m.Success) return null;
            var isPlug = m.Groups[1].Value == "Pen";
            var id = m.Groups[2].Value;
            key = m.Groups[3].Value;
            var prefix = address.Substring(0, address.Length - key.Length);

            var part = _parts.GetOrAdd(prefix, p => new Part
            {
                Prefix = p, Id = id, IsPlug = isPlug, Zone = Zones.Classify(isPlug, id)
            });
            part.Keys[key] = 0;
            _partOf[address] = (part, key);
            return part;
        }

        /// <summary>Receive thread. The plug-length detector has to see every root/tip update as it lands.</summary>
        private void NotePartValue(string address)
        {
            var part = RegisterPart(address, out var key);
            if (part == null || part.IsPlug) return;
            if (key is "PenSelfNewRoot" or "PenSelfNewTip")
                part.SelfLength.Update(Received(part, "PenSelfNewRoot"), Received(part, "PenSelfNewTip"));
            else if (key is "PenOthersNewRoot" or "PenOthersNewTip")
                part.OthersLength.Update(Received(part, "PenOthersNewRoot"), Received(part, "PenOthersNewTip"));
        }

        private double? Received(Part p, string key) => _values.TryGetValue(p.Prefix + key, out var v) ? v : null;
        private double Value(Part p, string key) => _values.TryGetValue(p.Prefix + key, out var v) ? v : 0;

        /// <summary>The ...Close flag says the contact is real; the float beside it already rises from a metre away.</summary>
        private bool Close(Part p, string closeKey) => !p.Keys.ContainsKey(closeKey) || Value(p, closeKey) > 0.5;

        /// <summary>
        /// Most sockets only report how near the visiting plug's root and tip are (1 m receivers). Depth is
        /// how much of the plug's measured length has gone past the socket. Null = nothing of this kind
        /// nearby, so fall back to the legacy parameter.
        /// </summary>
        private double? NewPenDepth(Part p, bool self)
        {
            var rootKey = self ? "PenSelfNewRoot" : "PenOthersNewRoot";
            var tipKey = self ? "PenSelfNewTip" : "PenOthersNewTip";
            if (!p.Keys.ContainsKey(rootKey) || !p.Keys.ContainsKey(tipKey)) return null;
            var root = Value(p, rootKey);
            var tip = Value(p, tipKey);
            if (root <= 0 && tip <= 0) return null;

            var len = (self ? p.SelfLength : p.OthersLength).Length;
            if (len is > 0 && tip > 0.99) return Sig.Clamp01(1 - (1 - root) / len.Value);
            return 0;
        }

        private void ReadSources(Part p, Span<double> pos)
        {
            pos[TouchSelf] = Close(p, "TouchSelfClose") ? Value(p, "TouchSelf") : 0;
            pos[TouchOthers] = Close(p, "TouchOthersClose") ? Value(p, "TouchOthers") : 0;
            if (p.IsPlug)
            {
                pos[PenSelf] = Value(p, "PenSelf");
                pos[PenOthers] = Value(p, "PenOthers");
                pos[FrotOthers] = Close(p, "FrotOthersClose") ? Value(p, "FrotOthers") : 0;
            }
            else
            {
                pos[PenSelf] = NewPenDepth(p, true) ?? Value(p, "PenSelf");
                pos[PenOthers] = NewPenDepth(p, false) ?? (Close(p, "PenOthersClose") ? Value(p, "PenOthers") : 0);
                pos[FrotOthers] = Value(p, "FrotOthers");
            }
        }

        /// <summary>
        /// Engine thread, once per tick. VRChat only sends a parameter when it changes, so speed is
        /// sampled here on the engine's clock — a sensor that has gone quiet then reads as standing still.
        /// </summary>
        private void Recompute(double dtMs)
        {
            var levels = new double[Zones.Count];
            var depths = NoDepths();
            var present = new bool[Zones.Count];
            double max = 0;
            string top = null;

            var alpha = 1.0 - Math.Exp(-dtMs / MotionSmoothMs);
            Span<double> pos = stackalloc double[SourceCount];

            if (_s.OscAutoSensors)
                foreach (var part in _parts.Values)
                {
                    var z = (int)part.Zone;
                    present[z] = true;
                    ReadSources(part, pos);

                    double best = 0;
                    int bestSrc = -1;
                    for (int i = 0; i < SourceCount; i++)
                    {
                        var m = part.Motions[i];
                        var inst = m.Primed ? Math.Abs(pos[i] - m.Last) / (dtMs / 1000.0) : 0;
                        m.Last = pos[i];
                        m.Primed = true;
                        m.Speed += (inst - m.Speed) * alpha;
                        if (m.Speed < 1e-3) m.Speed = 0;

                        if ((i == TouchSelf || i == PenSelf) && !_s.OscAutoSelf) continue;
                        var level = Level(i, pos[i], m.Speed);
                        if (level > best) { best = level; bestSrc = i; }
                    }

                    if (best > levels[z])
                    {
                        levels[z] = best;
                        depths[z] = bestSrc is PenSelf or PenOthers ? pos[bestSrc] : double.NaN;
                    }
                    if (best > max) { max = best; top = part.Label + " " + SourceNames[bestSrc]; }
                }

            double ticked = 0;
            string tickedTop = null;
            var wanted = _s.OscParams;
            if (wanted != null)
                foreach (var key in wanted)
                {
                    // A ticked SPS parameter is already counted above with feel applied; letting its raw
                    // position in as well would win the max and flatten every stroke back out.
                    if (_s.OscAutoSensors && _partOf.ContainsKey(key)) continue;
                    if (_values.TryGetValue(key, out var v) && v > ticked) { ticked = v; tickedTop = key; }
                }

            var gain = BuildUp(dtMs, Math.Max(max, ticked));
            for (int z = 0; z < levels.Length; z++) levels[z] *= gain;
            ticked *= gain;
            max *= gain;

            ZoneLevels = levels;
            ZoneDepths = depths;
            ZonesPresent = present;
            TickedLevel = ticked;
            _top = ticked > max ? tickedTop : top;
            Raw = Sig.Clamp01(Math.Max(max, ticked));
        }

        /// <summary>One source's contribution: its position, blended toward its speed, scaled by what kind of contact it is.</summary>
        private double Level(int source, double v, double rawSpeed)
        {
            // Pulling away ends on a fast drop to zero; without this that reads as one last thump.
            if (v <= 0) return 0;

            var weight = source is TouchSelf or TouchOthers ? Sig.Clamp01(_s.OscTouchWeight)
                       : source == FrotOthers ? Sig.Clamp01(_s.OscFrotWeight) : 1.0;
            var follow = Sig.Clamp01(_s.OscMotion);
            if (follow <= 0) return v * weight;

            var speed = Sig.Clamp01(rawSpeed / Math.Max(0.1, _s.OscMotionFullSpeed));
            var idle = Sig.Clamp01(_s.OscIdle);
            var moving = idle * v + (1 - idle) * speed;
            return weight * ((1 - follow) * v + follow * moving);
        }

        /// <summary>
        /// Starts below full strength and climbs while contact continues, faster when it is intense, then
        /// eases back at two-thirds of that pace once left alone. Survives avatar changes — it is the
        /// wearer's state, not the avatar's.
        /// </summary>
        private double BuildUp(double dtMs, double activity)
        {
            if (!_s.OscBuildUp) { _arousal = 0; return 1; }

            var fullMs = Math.Max(0.5, _s.OscBuildMinutes) * 60_000.0;
            if (activity > 0.05) _arousal += dtMs / fullMs * (0.5 + 0.5 * activity) * 1.5;
            else _arousal -= dtMs / (fullMs * 1.5);
            _arousal = Sig.Clamp01(_arousal);

            var start = Sig.Clamp01(_s.OscBuildStart);
            return start + (1 - start) * _arousal;
        }

        private string PartSummary()
        {
            var byZone = _parts.Values.GroupBy(p => p.Zone).OrderBy(g => g.Key)
                .Select(g => g.Count() == 1 ? $"{g.Key} ({g.First().Id})" : $"{g.Key} ×{g.Count()}");
            return string.Join(", ", byZone);
        }

        /// <summary>
        /// Measures a visiting plug's length from how near its root and tip are, which arrive as separate
        /// messages at unrelated times — hence several samples and keeping the pair that agree.
        /// </summary>
        private sealed class LengthDetector
        {
            private readonly object _gate = new();
            private readonly List<double> _recent = new();
            private double? _length, _whilePenetrating;

            public double? Length { get { lock (_gate) return _length; } }

            public void Update(double? root, double? tip)
            {
                lock (_gate)
                {
                    if (root == null || tip == null || root < 0.01 || tip < 0.01)
                    {
                        // missing, or nobody in range any more: whoever comes next may be a different size
                        _whilePenetrating = null;
                        _recent.Clear();
                        _length = null;
                        return;
                    }
                    if (root > 0.95) return;       // their root at the centre of our socket — not a real reading

                    var length = tip.Value - root.Value; // receivers are 1 m, so this is metres
                    if (length < 0.02) return;     // backwards or broken; keep what we had

                    if (tip > 0.99)
                    {
                        // Already inside, so the tip reading is saturated and this under-measures.
                        // Only worth having when there is nothing better.
                        if (_whilePenetrating == null || length > _whilePenetrating) _whilePenetrating = length;
                    }
                    else
                    {
                        _recent.Insert(0, length);
                        if (_recent.Count > 8) _recent.RemoveAt(8);
                    }
                    _length = FromSamples();
                }
            }

            private double? FromSamples()
            {
                if (_recent.Count < 4) return _whilePenetrating;
                // The two samples closest to each other are the trustworthy ones; the rest were taken
                // while only one of root/tip had updated.
                var sorted = _recent.OrderBy(x => x).ToList();
                double smallest = 1;
                int at = -1;
                for (int i = 1; i < sorted.Count; i++)
                {
                    var diff = sorted[i] - sorted[i - 1];
                    if (diff < smallest) { smallest = diff; at = i; }
                }
                return at >= 0 ? sorted[at] : _recent[0];
            }
        }
    }
}
