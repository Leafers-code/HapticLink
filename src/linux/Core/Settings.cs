using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HapticLink.Core
{
    public enum MixMode { Max, Average, Sum, Priority }
    public enum AudioBand { Loudness, Bass, Treble, Custom }
    public enum PatternMode { None, Pulse, Wave, Ramp, Random }
    public enum AudioCaptureMode { OutputMonitor, Application, SelectedGame }

    public class GameEventRule
    {
        public string Event { get; set; } = "event";
        public bool Enabled { get; set; } = true;
        public double Level { get; set; } = .5;
        public int DurationMs { get; set; } = 300;
        public int CooldownMs { get; set; } = 50;
    }

    /// <summary>Per-source shaping. Every source produces 0..1 and is shaped the same way.</summary>
    public class SourceSettings
    {
        public bool Enabled { get; set; }
        public double Gain { get; set; } = 1.0;
        /// <summary>Response exponent. 1 = linear, &gt;1 = softer low end, &lt;1 = hotter low end.</summary>
        public double Curve { get; set; } = 1.0;
        public double Deadzone { get; set; } = 0.02;
        public double AttackMs { get; set; } = 40;
        public double ReleaseMs { get; set; } = 250;
        /// <summary>Higher wins under MixMode.Priority.</summary>
        public int Priority { get; set; }
    }

    /// <summary>
    /// The VRChat settings that can differ per avatar. Settings.Osc / OscParams / OscAuto* stay the
    /// live working copy the engine reads; a profile is where that copy is loaded from and stored to.
    /// </summary>
    public class OscProfile
    {
        public string Name { get; set; } = "";
        public double Gain { get; set; } = 1.0;
        public double Curve { get; set; } = 1.0;
        public double Deadzone { get; set; } = 0.02;
        public double AttackMs { get; set; } = 20;
        public double ReleaseMs { get; set; } = 150;
        public bool AutoSensors { get; set; } = true;
        public bool AutoSelf { get; set; } = true;
        public string[] Params { get; set; } = Array.Empty<string>();
    }

    public class Settings
    {
        // ---- connection ----
        public string Host { get; set; } = "";
        public int HttpPort { get; set; } = 20010;
        public int HttpsPort { get; set; } = 30010;
        public bool PreferHttps { get; set; }
        /// <summary>Empty = broadcast to every connected toy.</summary>
        public string ToyId { get; set; } = "";
        /// <summary>Vibrate / Rotate / Pump / Thrusting / Suction / Oscillate / All.</summary>
        public string Action { get; set; } = "Vibrate";

        // ---- output loop ----
        public int UpdateHz { get; set; } = 10;
        /// <summary>timeSec sent with every command, so the toy stops on its own if this app dies.</summary>
        public int DeadManSeconds { get; set; } = 4;
        public double MasterCap { get; set; } = 1.0;
        /// <summary>Force-stop after this many minutes of continuous non-zero output. 0 = off.</summary>
        public int MaxContinuousMinutes { get; set; } = 20;
        public MixMode Mix { get; set; } = MixMode.Max;

        // ---- sources ----
        public SourceSettings Rumble { get; set; } = new SourceSettings { AttackMs = 10, ReleaseMs = 120, Priority = 3 };
        public SourceSettings Audio { get; set; } = new SourceSettings { AttackMs = 25, ReleaseMs = 180, Priority = 1 };
        public SourceSettings Osc { get; set; } = new SourceSettings { AttackMs = 20, ReleaseMs = 150, Priority = 2 };
        public SourceSettings Manual { get; set; } = new SourceSettings { Enabled = true, AttackMs = 0, ReleaseMs = 0, Priority = 4 };
        public SourceSettings Events { get; set; } = new() { AttackMs = 0, ReleaseMs = 80, Priority = 5 };

        // Intiface clients connect here instead of directly to Intiface Central.
        public int IntegrationPort { get; set; } = 12346;
        public int IntegrationTimeoutMs { get; set; } = 10000;
        public List<GameEventRule> EventRules { get; set; } = new()
        {
            new() { Event = "kill", Level = .8, DurationMs = 400 },
            new() { Event = "damage", Level = .6, DurationMs = 300 },
            new() { Event = "death", Level = 1, DurationMs = 800 },
            new() { Event = "shot", Level = .25, DurationMs = 60 },
            new() { Event = "round_win", Level = .6, DurationMs = 1200 }
        };

        public AudioCaptureMode AudioCaptureMode { get; set; }
        public int AudioProcessId { get; set; }
        public int AudioStreamIndex { get; set; } = -1;
        public bool AudioEqEnabled { get; set; } = true;
        public bool AudioFrequencyCaptureEnabled { get; set; } = true;
        public Dictionary<string, bool> PluginEnabled { get; set; } = new();
        public bool GamePluginsInitialized { get; set; }
        public Dictionary<string, double> PluginGains { get; set; } = new();
        public Dictionary<string, string> PluginApplications { get; set; } = new();
        public double AudioCaptureLowHz { get; set; } = 20;
        public double AudioCaptureHighHz { get; set; } = 20000;
        public double AudioEqPreampDb { get; set; }
        public double[] AudioEqGainsDb { get; set; } = new double[10];

        public string GameWindowId { get; set; } = "";
        public int GameProcessId { get; set; }
        public bool PauseWhenGameUnfocused { get; set; }
        public bool OverlayEnabled { get; set; }
        public bool OverlayInteractive { get; set; }
        public bool OverlayOnlyWhenGameFocused { get; set; } = true;
        public double OverlayOpacity { get; set; } = .9;
        public int OverlayOffsetX { get; set; } = 20;
        public int OverlayOffsetY { get; set; } = 20;

        // ---- audio ----
        public AudioBand AudioBand { get; set; } = AudioBand.Loudness;
        public double AudioFloorDb { get; set; } = -48;
        public double AudioCeilDb { get; set; } = -12;
        public string AudioDeviceId { get; set; } = "";

        // ---- Linux integration ----
        public string RumbleDevice { get; set; } = "";
        public bool RumbleGrab { get; set; }
        public string PythonCommand { get; set; } = "python3";
        public string VrchatOscDirectory { get; set; } = "";
        public int OscQueryPort { get; set; }

        // ---- rumble ----
        public bool RumblePassthrough { get; set; } = true;
        public double RumbleLargeWeight { get; set; } = 1.0;
        public double RumbleSmallWeight { get; set; } = 1.0;
        /// <summary>Controller poll rate. Unrelated to UpdateHz, which paces the toy, not the pad.</summary>
        public int RumblePollHz { get; set; } = 500;

        // ---- osc ----
        public int OscPort { get; set; } = 9001;
        public string[] OscParams { get; set; } = Array.Empty<string>();
        /// <summary>Use every SPS/OGB touch sensor the avatar sends, so nothing needs ticking per avatar.</summary>
        public bool OscAutoSensors { get; set; } = true;
        /// <summary>Auto-detect also counts the wearer's own hands (the ...Self sensors).</summary>
        public bool OscAutoSelf { get; set; } = true;
        /// <summary>What every avatar without its own entry gets. Null only until Load() migrates an old config.</summary>
        public OscProfile OscShared { get; set; }
        /// <summary>Avatar id → its own settings, for avatars the user chose to remember.</summary>
        public Dictionary<string, OscProfile> OscAvatars { get; set; } = new();
        /// <summary>VRChat's OSC input port — only used for the OGB_ENABLED heartbeat.</summary>
        public int OscSendPort { get; set; } = 9000;
        /// <summary>Tell SPS2 avatars a haptics app is listening; without it their OGB/* params stay at 0.</summary>
        public bool OscOgbEnable { get; set; } = true;

        // ---- osc feel (auto-detected sensors only; hand-ticked parameters stay position-only) ----
        /// <summary>0 = strength follows how deep/close the contact is; 1 = it follows how fast it is moving.</summary>
        public double OscMotion { get; set; } = 0.7;
        /// <summary>Sensor travel per second that counts as full strength. A full-depth stroke at 1.5 Hz averages 3.</summary>
        public double OscMotionFullSpeed { get; set; } = 3.0;
        /// <summary>Share of the position signal that remains while contact is held still, so stillness isn't silence.</summary>
        public double OscIdle { get; set; } = 0.3;
        /// <summary>How strong a touch can get next to penetration (1 = equal, which is how it used to be).</summary>
        public double OscTouchWeight { get; set; } = 0.45;
        public double OscFrotWeight { get; set; } = 0.75;

        // ---- osc part picking ----
        /// <summary>Let each toy follow the body parts that sit where it is worn (see PartRouter). Off = every part drives every toy.</summary>
        public bool OscAutoParts { get; set; } = true;
        /// <summary>How strongly parts outside a toy's own zones still drive it. 1 = no preference, 0 = own zones only.</summary>
        public double OscSympathy { get; set; } = 0.6;
        /// <summary>Two-motor toys: move penetration from the entrance motor to the deep one as it goes in.</summary>
        public bool OscTravel { get; set; } = true;
        /// <summary>Edge: which of Vibrate1/Vibrate2 is the inner arm isn't documented — tick if travel feels backwards.</summary>
        public bool OscEdgeSwap { get; set; }

        // ---- osc build-up ----
        /// <summary>Start below full strength and climb with sustained contact, easing back down when left alone.</summary>
        public bool OscBuildUp { get; set; } = true;
        public double OscBuildStart { get; set; } = 0.6;
        /// <summary>Minutes of continuous contact to climb from OscBuildStart to full.</summary>
        public double OscBuildMinutes { get; set; } = 5;

        // ---- manual ----
        public double ManualLevel { get; set; }
        public PatternMode Pattern { get; set; } = PatternMode.None;
        public double PatternPeriodSec { get; set; } = 2.0;
        public double PatternDepth { get; set; } = 1.0;

        // ---- persistence ----
        // HAPTICLINK_HOME points a test run at a scratch folder, keeping it off the real config and log.
        public static string Dir => Environment.GetEnvironmentVariable("HAPTICLINK_HOME") is { Length: > 0 } home
            ? home
            : OperatingSystem.IsLinux()
                ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                    ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "HapticLink")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HapticLink");
        public static string FilePath => Path.Combine(Dir, "config.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static Settings Load()
        {
            Settings s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts);
            }
            catch (Exception ex)
            {
                Log.Write("Config load failed, using defaults: " + ex.Message);
            }
            s ??= new Settings();
            if (!Enum.IsDefined(s.AudioCaptureMode)) s.AudioCaptureMode = AudioCaptureMode.OutputMonitor;
            if (!Enum.IsDefined(s.AudioBand)) s.AudioBand = AudioBand.Loudness;
            (s.AudioCaptureLowHz, s.AudioCaptureHighHz) = AudioFrequencyRange.Normalize(s.AudioCaptureLowHz, s.AudioCaptureHighHz);
            s.AudioEqGainsDb = s.AudioEqGainsDb?.Length == 10 ? s.AudioEqGainsDb.Select(v => double.IsFinite(v) ? Math.Clamp(v, -12, 12) : 0).ToArray() : new double[10];
            s.EventRules = (s.EventRules ?? new()).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Event)).Take(64).ToList();
            s.IntegrationPort = s.IntegrationPort is >= 1024 and <= 65535 ? s.IntegrationPort : 12346;
            s.GameWindowId ??= "";

            // The avatar isn't known yet at launch, so start from the shared profile — the file may
            // have been written while some avatar's own profile was the live copy.
            s.OscAvatars ??= new();
            if (s.OscShared == null) s.OscShared = s.CaptureOsc();
            else s.ApplyOsc(s.OscShared);
            return s;
        }

        // ---- per-avatar OSC profiles ----

        public OscProfile CaptureOsc(string name = null) => new OscProfile
        {
            Name = name ?? "",
            Gain = Osc.Gain, Curve = Osc.Curve, Deadzone = Osc.Deadzone,
            AttackMs = Osc.AttackMs, ReleaseMs = Osc.ReleaseMs,
            AutoSensors = OscAutoSensors, AutoSelf = OscAutoSelf,
            Params = (string[])(OscParams ?? Array.Empty<string>()).Clone()
        };

        public void ApplyOsc(OscProfile p)
        {
            Osc.Gain = p.Gain; Osc.Curve = p.Curve; Osc.Deadzone = p.Deadzone;
            Osc.AttackMs = p.AttackMs; Osc.ReleaseMs = p.ReleaseMs;
            OscAutoSensors = p.AutoSensors; OscAutoSelf = p.AutoSelf;
            OscParams = (string[])(p.Params ?? Array.Empty<string>()).Clone();
        }

        /// <summary>Which avatar the live copy belongs to; null = the shared profile. Never persisted.</summary>
        [JsonIgnore] public string OscLiveAvatarId { get; private set; }

        [JsonIgnore] public bool OscLiveIsOwnProfile =>
            OscLiveAvatarId != null && OscAvatars.ContainsKey(OscLiveAvatarId);

        /// <summary>Write the live copy back to where it came from: the current avatar's profile, else the shared one.</summary>
        public void StoreOsc()
        {
            if (OscLiveAvatarId != null && OscAvatars.TryGetValue(OscLiveAvatarId, out var own))
                OscAvatars[OscLiveAvatarId] = CaptureOsc(own.Name);
            else
                OscShared = CaptureOsc();
        }

        /// <summary>Store the live copy, then make avatarId's profile (or the shared one) live.</summary>
        public void SwitchOsc(string avatarId)
        {
            StoreOsc();
            OscLiveAvatarId = avatarId;
            ReloadOsc();
        }

        /// <summary>Re-read the live copy from its slot, dropping whatever was live.</summary>
        public void ReloadOsc() =>
            ApplyOsc(OscLiveIsOwnProfile ? OscAvatars[OscLiveAvatarId] : OscShared ?? new OscProfile());

        /// <summary>Give the current avatar its own profile, starting as a copy of what is live now.</summary>
        public void RememberOscAvatar(string name)
        {
            if (OscLiveAvatarId == null) return;
            StoreOsc();
            OscAvatars[OscLiveAvatarId] = CaptureOsc(name);
        }

        /// <summary>Drop the current avatar's own profile and fall back to the shared one.</summary>
        public void ForgetOscAvatar()
        {
            if (OscLiveAvatarId == null) return;
            OscAvatars.Remove(OscLiveAvatarId);
            ReloadOsc();
        }

        private string _lastWritten;

        /// <summary>No-ops when nothing changed, so it is safe to call on a timer.</summary>
        public void Save()
        {
            try
            {
                StoreOsc(); // the sliders edit the live copy; the profile it belongs to is what persists
                var json = JsonSerializer.Serialize(this, JsonOpts);
                if (json == _lastWritten) return;
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, json);
                _lastWritten = json;
            }
            catch (Exception ex)
            {
                Log.Write("Config save failed: " + ex.Message);
            }
        }
    }
}
