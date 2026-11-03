using System;
using System.Collections.Generic;
using System.Linq;
using HapticLink.Toy;

namespace HapticLink.Core
{
    /// <summary>Where on the body an SPS part sits. Parts are sorted into these by name.</summary>
    public enum Zone { Penis, Vagina, Anal, Butt, Mouth, Chest, Thighs, Hands, Feet, Other }

    public static class Zones
    {
        public static readonly int Count = Enum.GetValues(typeof(Zone)).Length;

        // Checked in order, first hit wins. Order matters: "Special_Assjob" must not land in Anal on
        // "ass", and "Paw_Steppies" is a foot before it is a paw.
        private static readonly (Zone zone, string[] words)[] SocketWords =
        {
            (Zone.Butt,   new[] { "assjob", "buttjob", "hotdog", "cheeks" }),
            (Zone.Feet,   new[] { "steppies", "footjob", "feet", "foot", "sole", "toes" }),
            (Zone.Thighs, new[] { "thigh" }),
            (Zone.Chest,  new[] { "titjob", "boob", "breast", "chest", "tits", "nipple", "cleavage" }),
            (Zone.Mouth,  new[] { "blowjob", "mouth", "oral", "throat", "maw", "muzzle" }),
            (Zone.Hands,  new[] { "handjob", "hand", "paw", "finger", "grip" }),
            (Zone.Penis,  new[] { "frot", "sheath", "urethra" }),
            (Zone.Vagina, new[] { "pussy", "vagina", "vag", "cunt", "slit", "cloaca" }),
            (Zone.Anal,   new[] { "anal", "anus", "ass", "butt", "tailhole", "rear" }),
        };

        /// <summary>Every plug is the wearer's penis (or stands in for it); sockets go by their name.</summary>
        public static Zone Classify(bool isPlug, string id)
        {
            if (isPlug) return Zone.Penis;
            var name = (id ?? "").ToLowerInvariant();
            foreach (var (zone, words) in SocketWords)
                if (words.Any(name.Contains)) return zone;
            return Zone.Other;
        }
    }

    /// <summary>One motor of one toy, and how strongly each body zone drives it.</summary>
    public sealed class Channel
    {
        public string ToyId = "";
        public string ToyLabel = "every toy";
        /// <summary>The name used in the Lovense action string — "Vibrate1", or the user's chosen Action.</summary>
        public string Function = "Vibrate";
        public double[] Weights = new double[Zones.Count];
        public bool[] Primary = new bool[Zones.Count];
        /// <summary>Two-motor toys: +1 is the deep motor, -1 the one at the entrance, 0 means no travel.</summary>
        public int Travel;
        public double Output;

        public string Key => ToyId + "/" + Function;
    }

    /// <summary>
    /// Decides which SPS parts drive which toy. A toy is driven fully by the parts that sit where it is
    /// worn and at <see cref="Settings.OscSympathy"/> by everything else, so a plug follows the avatar's
    /// rear rather than whatever happens to be touched. When the avatar has nothing in a toy's own
    /// zones, the avatar's main parts are promoted instead — otherwise that toy would only ever whisper.
    /// </summary>
    public static class PartRouter
    {
        private enum Kind { Generic, Anal, Prostate, Penis, Vaginal, Chest }

        private static readonly (Kind kind, string[] names)[] KnownToys =
        {
            (Kind.Prostate, new[] { "edge" }),
            (Kind.Anal,     new[] { "hush", "ridge" }),
            (Kind.Penis,    new[] { "max", "calor", "gush", "solace", "diamo" }),
            (Kind.Chest,    new[] { "gemini" }),
            (Kind.Vaginal,  new[] { "lush", "nora", "osci", "ferri", "dolce", "flexer", "gravity", "vulse",
                                    "tenera", "ambi", "domi", "hyphy", "exomoon", "lapis", "mission" }),
        };

        private static readonly Zone[] MainZones = { Zone.Penis, Zone.Vagina, Zone.Anal };

        private static Kind KindOf(ToyInfo t)
        {
            var name = (t.Name ?? "").ToLowerInvariant();
            foreach (var (kind, names) in KnownToys)
                if (names.Any(name.Contains)) return kind;
            return Kind.Generic;
        }

        private static Zone[] PrimaryZones(Kind k) => k switch
        {
            Kind.Anal => new[] { Zone.Anal, Zone.Butt },
            Kind.Prostate => new[] { Zone.Anal, Zone.Butt, Zone.Penis },
            Kind.Penis => new[] { Zone.Penis },
            Kind.Vaginal => new[] { Zone.Vagina },
            Kind.Chest => new[] { Zone.Chest },
            _ => null // every zone
        };

        /// <param name="present">Which zones the worn avatar has parts in; null or all-false = not known yet.</param>
        public static List<Channel> Build(IReadOnlyList<ToyInfo> toys, Settings s, bool[] present)
        {
            var list = new List<Channel>();
            var targets = (toys ?? Array.Empty<ToyInfo>())
                .Where(t => t.Connected && (string.IsNullOrWhiteSpace(s.ToyId) || t.Id == s.ToyId))
                .ToList();

            // Nothing to tell apart — one channel that behaves exactly as the app did before routing.
            if (!s.OscAutoParts || targets.Count == 0)
            {
                list.Add(Everything(new Channel { ToyId = s.ToyId ?? "", Function = s.Action }));
                return list;
            }

            var avatarKnown = present != null && present.Any(p => p);
            foreach (var t in targets)
            {
                var kind = KindOf(t);
                var twoMotors = kind == Kind.Prostate
                    && t.Functions.Any(f => f.Equals("Vibrate1", StringComparison.OrdinalIgnoreCase))
                    && t.Functions.Any(f => f.Equals("Vibrate2", StringComparison.OrdinalIgnoreCase));

                if (twoMotors)
                {
                    var inner = s.OscEdgeSwap ? "Vibrate2" : "Vibrate1";
                    var outer = s.OscEdgeSwap ? "Vibrate1" : "Vibrate2";
                    list.Add(Make(t, inner, new[] { Zone.Anal, Zone.Butt }, s, present, avatarKnown, s.OscTravel ? 1 : 0));
                    list.Add(Make(t, outer, new[] { Zone.Penis }, s, present, avatarKnown, s.OscTravel ? -1 : 0));
                }
                else list.Add(Make(t, s.Action, PrimaryZones(kind), s, present, avatarKnown, 0));
            }

            // One toy and none singled out: address it the way the app always has, as a broadcast.
            // That path is proven against the real Remote app; naming the toy only matters with two.
            if (targets.Count == 1 && string.IsNullOrWhiteSpace(s.ToyId))
                foreach (var c in list) c.ToyId = "";
            return list;
        }

        private static Channel Everything(Channel c)
        {
            for (int z = 0; z < Zones.Count; z++) { c.Weights[z] = 1; c.Primary[z] = true; }
            return c;
        }

        private static Channel Make(ToyInfo t, string function, Zone[] primary, Settings s,
                                    bool[] present, bool avatarKnown, int travel)
        {
            var c = new Channel
            {
                ToyId = t.Id,
                ToyLabel = string.IsNullOrWhiteSpace(t.NickName) ? t.Name : t.NickName,
                Function = function,
                Travel = travel
            };
            if (primary == null) return Everything(c);

            if (avatarKnown && !primary.Any(z => present[(int)z]))
            {
                primary = MainZones.Where(z => present[(int)z]).ToArray();
                if (primary.Length == 0) return Everything(c);
            }

            var sympathy = Sig.Clamp01(s.OscSympathy);
            for (int z = 0; z < Zones.Count; z++) c.Weights[z] = sympathy;
            foreach (var z in primary) { c.Weights[(int)z] = 1; c.Primary[(int)z] = true; }
            return c;
        }

        /// <summary>
        /// A zone's weight on a channel. On a two-motor toy, penetration into one of its own zones moves
        /// between the motors with depth: the entrance motor leads while shallow, the deep one takes over.
        /// </summary>
        public static double Weight(Channel c, int zone, double depth, Settings s)
        {
            var w = c.Weights[zone];
            if (c.Travel == 0 || double.IsNaN(depth)) return w;
            var anal = zone == (int)Zone.Anal || zone == (int)Zone.Butt;
            if (!anal) return w;
            var d = Sig.Clamp01(depth);
            return c.Travel > 0 ? 0.4 + 0.6 * d : Math.Max(Sig.Clamp01(s.OscSympathy), 1.0 - 0.7 * d);
        }

        /// <summary>One line per toy for the UI: "Mouse 2 ← Anal, Butt (others 60%)".</summary>
        public static string Describe(List<Channel> channels, bool[] present, Settings s)
        {
            if (channels.Count == 1 && channels[0].Primary.All(p => p))
                return s.OscAutoParts
                    ? $"{channels[0].ToyLabel} ← every part equally"
                    : "Every part drives every toy equally (automatic part picking is off).";

            var lines = new List<string>();
            foreach (var c in channels)
            {
                var zones = Enumerable.Range(0, Zones.Count)
                    .Where(z => c.Primary[z] && (present == null || !present.Any(p => p) || present[z]))
                    .Select(z => ((Zone)z).ToString()).ToList();
                var motor = c.Travel > 0 ? " deep motor" : c.Travel < 0 ? " entrance motor"
                          : c.Function.StartsWith("Vibrate", StringComparison.OrdinalIgnoreCase) && c.Function.Length > 7 ? " " + c.Function : "";
                lines.Add($"{c.ToyLabel}{motor} ← {(zones.Count > 0 ? string.Join(", ", zones) : "nothing on this avatar")}"
                        + $" · other parts {Sig.Clamp01(s.OscSympathy) * 100:0}%");
            }
            return string.Join("\n", lines);
        }
    }
}
