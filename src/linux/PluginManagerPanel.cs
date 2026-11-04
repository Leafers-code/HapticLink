using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HapticLink.Core;

namespace HapticLink;

public sealed class PluginManagerPanel : StackPanel
{
    private readonly Engine _engine;
    private readonly Settings _s;
    private readonly PluginManager _manager;
    private readonly Action<string> _configure;
    private readonly TextBox _search = new() { Watermark = "Search features or integration profiles", Name = "PluginSearch" };
    private readonly TextBlock _summary = Label(""), _message = Label("");
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Dictionary<string, (CheckBox Toggle, TextBlock Status)> _features = new();
    private readonly Dictionary<string, TextBlock> _profiles = new();
    private readonly Dictionary<string, TextBox> _addresses = new();
    private readonly Dictionary<string, Button> _pages = new();
    private readonly Button _undo;
    private readonly StackPanel _messageRow;
    private (string Json, bool Enabled)? _removed;
    private string _page = "Features";
    private bool _refreshing;
    private sealed record Feature(string Id, string Name, string Description, string Page, int Source = -1, bool Core = false);
    private static readonly Feature[] Features =
    {
        new("controller", "Controller rumble", "Use gamepad rumble as a haptic input.", "Controller", 0),
        new("audio", "Game audio", "Output-device capture, with application capture on Linux.", "Audio", 1),
        new("osc", "VRChat OSC", "Avatar sensors, profiles and per-device routing.", "VRChat OSC", 2),
        new("manual", "Manual and patterns", "Manual level, pulse, wave, ramp and random patterns.", "Manual", 3),
        new("events", "Intiface and event bridge", "Shared local server used by game integrations and profiles.", "Events", 4),
        new("frequency", "Audio frequency capture", "Choose Bass, Mids, Treble or an exact frequency range.", "EQ"),
        new("tone", "Advanced tonal shaping", "Optional ten-band gain and preamp after frequency selection.", "EQ"),
        new("overlay", "Game overlay", "Transparent game overlay, live meters and interactive controls.", "Overlay"),
        new("focus", "Game focus pause", "Pause output when the selected game loses focus.", "Overlay"),
        new("output", "Lovense Remote output", "Connection, device selection and output function.", "Connection", Core: true),
        new("mixer", "Mixer and output limits", "Master cap, mixing, command expiry and auto-stop.", "Output", Core: true),
        new("shortcuts", "Desktop shortcuts", "Panic, arm and overlay commands for desktop key bindings.", "Settings", Core: true)
    };
    public PluginManagerPanel(Engine engine, Action<string> configure)
    {
        _engine = engine; _s = engine.S; _manager = engine.Plugins; _configure = configure; Spacing = 7;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(Label("PLUGIN MANAGER", true)); Grid.SetColumn(_summary, 1); heading.Children.Add(_summary); Children.Add(heading);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        toolbar.Children.Add(_search); var import = ActionButton("Import profile…", Import); Grid.SetColumn(import, 1); toolbar.Children.Add(import);
        var reload = ActionButton("Reload", () => { _manager.Reload(); Build(); return Task.CompletedTask; }); Grid.SetColumn(reload, 2); toolbar.Children.Add(reload); Children.Add(toolbar);
        var pages = Row();
        foreach (var page in new[] { "Features", "Installed", "Available" })
        {
            var button = ActionButton(page, () => { _page = page; Build(); return Task.CompletedTask; }); _pages[page] = button; pages.Children.Add(button);
        }
        Children.Add(pages);
        var message = Row(); _messageRow = message; message.Children.Add(_message);
        _undo = ActionButton("Undo removal", () =>
        {
            if (_removed is { } old) { var profile = _manager.Install(old.Json); EnableProfile(profile.Id, old.Enabled); _removed = null; _message.Text = "Profile restored."; }
            Build(); return Task.CompletedTask;
        }); _undo.IsVisible = false; message.Children.Add(_undo); Children.Add(message); Children.Add(_list);
        _search.TextChanged += (_, _) => Build(); Build();
    }
    private bool FeatureEnabled(Feature f) => f.Core || (f.Source >= 0 ? _engine.SettingsFor(f.Source).Enabled : f.Id switch
    {
        "frequency" => _s.AudioFrequencyCaptureEnabled, "tone" => _s.AudioEqEnabled,
        "overlay" => _s.OverlayEnabled, "focus" => _s.PauseWhenGameUnfocused, _ => false
    });
    private void EnableFeature(Feature f, bool enabled)
    {
        if (_refreshing) return;
        if (f.Source >= 0) { if (_engine.SettingsFor(f.Source).Enabled != enabled) _engine.SetSourceEnabled(f.Source, enabled); }
        else switch (f.Id)
        {
            case "frequency": _s.AudioFrequencyCaptureEnabled = enabled; break;
            case "tone": _s.AudioEqEnabled = enabled; break;
            case "overlay": _s.OverlayEnabled = enabled; break;
            case "focus": _s.PauseWhenGameUnfocused = enabled; break;
        }
        _s.Save(); Refresh();
    }
    private bool Matches(params string[] text) => string.IsNullOrWhiteSpace(_search.Text) || text.Any(t => t.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    private void Build()
    {
        _list.Children.Clear(); _features.Clear(); _profiles.Clear(); _addresses.Clear();
        foreach (var page in _pages) page.Value.Classes.Set("active", page.Key == _page);
        _undo.IsVisible = _removed.HasValue;
        var installed = _manager.Installed;
        if (_page == "Features") foreach (var feature in Features.Where(f => Matches(f.Name, f.Description))) AddFeature(feature);
        if (_page == "Installed")
        {
            if (installed.Length == 0) _list.Children.Add(Label("No profiles installed. Choose Available for bundled integrations, or import a profile JSON file."));
            foreach (var plugin in installed.Where(p => Matches(p.Profile.Name, p.Profile.Description, p.Profile.Id))) AddInstalled(plugin);
            foreach (var error in _manager.Errors) _list.Children.Add(Label(error));
        }
        if (_page == "Available")
        {
            _list.Children.Add(Label("Profiles add event mappings and setup instructions. Install any required game mod or integration app separately."));
            foreach (var profile in PluginManager.Catalog().Where(p => Matches(p.Name, p.Description, p.Id)))
            {
                var local = installed.FirstOrDefault(p => p.Profile.Id == profile.Id);
                var update = local != null && Version.Parse(profile.Version) > Version.Parse(local.Profile.Version);
                var content = new StackPanel { Spacing = 4 };
                var action = ActionButton(local == null ? "Install" : update ? "Update" : "View", () =>
                {
                    if (local == null || update) { _manager.Install(PluginManager.Serialize(profile)); _s.Save(); _message.Text = local == null ? "Installed. Enable the profile when ready." : "Profile updated."; }
                    _page = "Installed"; Build(); return Task.CompletedTask;
                }); action.Name = "Install-" + profile.Id;
                content.Children.Add(TitleRow(profile.Name, profile.Version, action)); content.Children.Add(Label(profile.Description)); _list.Children.Add(Card(content));
            }
            _list.Children.Add(ActionButton("Find Intiface integration apps", async () =>
            {
                var top = TopLevel.GetTopLevel(this); if (top != null) await top.Launcher.LaunchUriAsync(new Uri("https://github.com/Renpona/awesome-intiface-sfw-list/blob/sfw-list/README.md"));
            }));
        }
        if (_list.Children.Count == 0) _list.Children.Add(Label("No matching plugins."));
        Refresh();
    }
    private void AddFeature(Feature feature)
    {
        var content = new StackPanel { Spacing = 3 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        CheckBox toggle = null;
        if (feature.Core) title.Children.Add(Label(feature.Name, true));
        else
        {
            toggle = new CheckBox { Content = feature.Name, IsChecked = FeatureEnabled(feature), Name = "PluginFeature-" + feature.Id };
            toggle.IsCheckedChanged += (_, _) => EnableFeature(feature, toggle.IsChecked == true); title.Children.Add(toggle);
        }
        var status = Label(""); status.FontFamily = Classic.Mono; Grid.SetColumn(status, 1); title.Children.Add(status);
        var configure = ActionButton("Configure", () => { _configure(feature.Page); return Task.CompletedTask; }); Grid.SetColumn(configure, 2); title.Children.Add(configure);
        content.Children.Add(title); content.Children.Add(Label(feature.Description)); _list.Children.Add(Card(content)); _features[feature.Id] = (toggle, status);
    }
    private void AddInstalled(InstalledPlugin installed)
    {
        var profile = installed.Profile; var content = new StackPanel { Spacing = 4 };
        var toggle = new CheckBox { Content = profile.Name, IsChecked = installed.Enabled, Name = "PluginProfile-" + profile.Id };
        toggle.IsCheckedChanged += (_, _) => { if (!_refreshing) { EnableProfile(profile.Id, toggle.IsChecked == true); Refresh(); } };
        var status = Label(""); status.FontFamily = Classic.Mono; _profiles[profile.Id] = status;
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 }; title.Children.Add(toggle); Grid.SetColumn(status, 1); title.Children.Add(status); content.Children.Add(title);
        content.Children.Add(Label(profile.Description + "  ·  v" + profile.Version));
        var setup = new StackPanel { Spacing = 6 };
        setup.Children.Add(Label(profile.Setup));
        var address = new TextBox { Text = _manager.Endpoint(profile, _engine.Events.ActivePort), IsReadOnly = true };
        _addresses[profile.Id] = address;
        setup.Children.Add(address);
        setup.Children.Add(ActionButton("Copy address", async () => { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard != null) await clipboard.SetTextAsync(_manager.Endpoint(profile, _engine.Events.ActivePort)); }));
        if (profile.Kind == "cs2") setup.Children.Add(ActionButton("Export CS2 game config…", () => SaveFile("gamestate_integration_hapticlink_" + profile.Id + ".cfg", _manager.Cs2Config(profile), "cfg")));
        if (profile.Kind != "buttplug") AddRules(setup, profile);
        setup.Children.Add(ActionButton("Configure bridge", () => { _configure("Events"); return Task.CompletedTask; }));
        var expander = new Expander { Header = "Setup and event mappings", Content = setup };
        content.Children.Add(expander);
        content.Children.Add(Row(ActionButton("Export profile…", () => SaveFile(profile.Id + ".json", _manager.Export(profile.Id), "json")), ActionButton("Remove", () =>
        {
            _removed = (_manager.Export(profile.Id), _manager.Installed.First(p => p.Profile.Id == profile.Id).Enabled);
            _manager.Uninstall(profile.Id); _s.Save(); _message.Text = "Removed " + profile.Name + "."; Build(); return Task.CompletedTask;
        })));
        _list.Children.Add(Card(content));
    }
    private void AddRules(StackPanel setup, PluginProfile profile)
    {
        var rules = new StackPanel { Spacing = 4 }; setup.Children.Add(rules);
        void Add(GameEventRule rule)
        {
            var controls = new StackPanel { Spacing = 4 };
            var enabled = new CheckBox { Content = "Enabled", IsChecked = rule.Enabled }; enabled.IsCheckedChanged += (_, _) => rule.Enabled = enabled.IsChecked == true; controls.Children.Add(enabled);
            void Number(string name, decimal value, decimal min, decimal max, decimal step, Action<decimal> set)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), ColumnSpacing = 6 }; row.Children.Add(Label(name));
                var number = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = step, FormatString = step < 1 ? "0.00" : "0" };
                number.ValueChanged += (_, _) => { if (number.Value is { } v) set(v); }; Grid.SetColumn(number, 1); row.Children.Add(number); controls.Children.Add(row);
            }
            Number("Intensity (0–1)", (decimal)rule.Level, 0, 1, .05m, x => rule.Level = (double)x);
            Number("Duration (ms)", rule.DurationMs, 0, 5000, 50, x => rule.DurationMs = (int)x);
            Number("Cooldown (ms)", rule.CooldownMs, 0, 10000, 50, x => rule.CooldownMs = (int)x);
            var item = new Expander { Header = rule.Event, Content = controls }; rules.Children.Add(item);
            controls.Children.Add(ActionButton("Remove mapping", () => { profile.Rules.Remove(rule); rules.Children.Remove(item); return Task.CompletedTask; }));
        }
        foreach (var rule in profile.Rules) Add(rule);
        var name = new TextBox { Watermark = "New event name" };
        setup.Children.Add(Row(name, ActionButton("Add mapping", () =>
        {
            var value = name.Text?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || profile.Rules.Count >= 64 || profile.Rules.Any(r => r.Event.Equals(value, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Enter a unique event name (up to 80 characters).");
            var rule = new GameEventRule { Event = value }; profile.Rules.Add(rule); Add(rule); name.Text = ""; return Task.CompletedTask;
        })));
        setup.Children.Add(ActionButton("Save mappings", () => { _manager.Install(PluginManager.Serialize(profile)); _s.Save(); _message.Text = "Event mappings saved."; Refresh(); return Task.CompletedTask; }));
    }
    private void EnableProfile(string id, bool enabled)
    {
        _manager.SetEnabled(id, enabled);
        if (enabled && !_s.Events.Enabled) _engine.SetSourceEnabled(4, true);
        _s.Save();
    }
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var installed = _manager.Installed;
            _messageRow.IsVisible = !string.IsNullOrWhiteSpace(_message.Text) || _removed.HasValue;
            _summary.Text = Features.Length + " features · " + installed.Length + " profiles";
            foreach (var feature in Features) if (_features.TryGetValue(feature.Id, out var row))
            {
                var enabled = FeatureEnabled(feature); if (row.Toggle != null) row.Toggle.IsChecked = enabled;
                var ready = feature.Source < 0 || _engine.SourceAt(feature.Source).Running && _engine.SourceAt(feature.Source).Available;
                row.Status.Text = !enabled ? "Disabled" : feature.Source >= 0 ? (ready ? "Running" : "Needs setup") : feature.Core ? "Core" : "Enabled";
                if (feature.Id == "output") row.Status.Text = _engine.Client.IsConnected ? "Connected" : "Disconnected";
                if (feature.Source >= 0) ToolTip.SetTip(row.Status, _engine.SourceAt(feature.Source).Status);
                row.Status.Foreground = enabled && !ready ? Classic.Warn : enabled ? Classic.Good : Classic.Muted;
                if (feature.Id == "output" && !_engine.Client.IsConnected) row.Status.Foreground = Classic.Muted;
                if (enabled && feature.Id == "focus" && !_engine.Game.State.Available) { row.Status.Text = "Select game"; row.Status.Foreground = Classic.Warn; }
                if (enabled && feature.Id is "frequency" or "tone" && !_s.Audio.Enabled) { row.Status.Text = "Audio disabled"; row.Status.Foreground = Classic.Warn; }
            }
            foreach (var plugin in installed) if (_profiles.TryGetValue(plugin.Profile.Id, out var status))
            {
                status.Text = !plugin.Enabled ? "Disabled" : _engine.Events.Running ? "Bridge ready" : "Bridge needs setup";
                status.Foreground = !plugin.Enabled ? Classic.Muted : _engine.Events.Running ? Classic.Good : Classic.Warn;
                if (_addresses.TryGetValue(plugin.Profile.Id, out var address)) address.Text = _manager.Endpoint(plugin.Profile, _engine.Events.ActivePort);
            }
        }
        finally { _refreshing = false; }
    }
    private async Task Import()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import HapticLink integration profile", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("Profile JSON") { Patterns = new[] { "*.json" } } } });
        if (files.Count == 0) return;
        await using var input = await files[0].OpenReadAsync(); using var memory = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer)) != 0) { if (memory.Length + count > 131072) throw new InvalidDataException("Profile exceeds 128 KiB."); memory.Write(buffer, 0, count); }
        _manager.Install(System.Text.Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF')); _s.Save(); _page = "Installed"; _message.Text = "Profile imported."; Build();
    }
    private async Task SaveFile(string name, string text, string extension)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage == null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export HapticLink profile or game config", SuggestedFileName = name, DefaultExtension = extension, FileTypeChoices = new[] { new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = new[] { "*." + extension } } } });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync(); if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)); await writer.WriteAsync(text); _message.Text = "Exported " + file.Name + ".";
    }
    private Button ActionButton(string title, Func<Task> action)
    {
        var button = new Button { Content = title };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { _message.Text = ex.Message; Log.Write("Plugins: " + ex.Message); } finally { button.IsEnabled = true; _messageRow.IsVisible = !string.IsNullOrWhiteSpace(_message.Text) || _removed.HasValue; } };
        return button;
    }
    private static TextBlock Label(string text, bool bold = false) => new() { Text = text, FontSize = 11, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = bold ? Classic.Text : Classic.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Row(params Control[] controls) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; foreach (var control in controls) row.Children.Add(control); return row; }
    private static Grid TitleRow(string name, string version, Control action)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 }; row.Children.Add(Label(name, true)); var v = Label("v" + version); Grid.SetColumn(v, 1); row.Children.Add(v); Grid.SetColumn(action, 2); row.Children.Add(action); return row;
    }
    private static Border Card(Control content) => new() { Child = content, BorderBrush = Classic.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 6) };
}
