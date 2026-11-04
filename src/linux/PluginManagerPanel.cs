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
    private readonly PluginManager _manager;
    private readonly CommunityCatalog _catalog = new();
    private readonly Action<string> _open;
    private readonly TextBox _search = new() { Watermark = "Find a game or integration", Name = "PluginSearch" };
    private readonly TextBlock _summary = Label(""), _message = Label("");
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Button _undo;
    private readonly Button _discoverPage, _installedPage;
    private readonly ComboBox _section = new() { ItemsSource = new[] { "Games", "Apps", "All" }, SelectedIndex = 0, MinWidth = 84 };
    private readonly CheckBox _thisPlatform = new() { Content = "This platform" };
    private readonly TextBlock _catalogStatus = Label("");
    private bool _discover = true;
    private (string Json, bool Enabled)? _removed;
    private string _signature = "";
    public PluginManagerPanel(Engine engine, Action<string> open)
    {
        _engine = engine; _manager = engine.Plugins; _open = open; Spacing = 6;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(Label("GAME PLUGINS", true)); Grid.SetColumn(_summary, 1); heading.Children.Add(_summary); Children.Add(heading);
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        var discover = _discoverPage = Button("Discover", () => { _discover = true; Build(); return Task.CompletedTask; }); discover.Name = "DiscoverPlugins";
        var installed = _installedPage = Button("Installed", () => { _discover = false; Build(); return Task.CompletedTask; }); installed.Name = "InstalledPlugins";
        search.Children.Add(discover); Grid.SetColumn(installed, 1); search.Children.Add(installed);
        Grid.SetColumn(_search, 2); search.Children.Add(_search); var import = Button("Import…", Import); Grid.SetColumn(import, 3); search.Children.Add(import);
        var reload = Button("Reload", () => { _manager.Reload(); Build(); return Task.CompletedTask; }); Grid.SetColumn(reload, 4); search.Children.Add(reload); Children.Add(search);
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        filters.Children.Add(_section); Grid.SetColumn(_thisPlatform, 1); filters.Children.Add(_thisPlatform);
        var refresh = Button("Refresh community list", async () =>
        {
            _catalogStatus.Text = "Checking the community list…";
            try { await _catalog.RefreshAsync(); _message.Text = "Community list updated."; }
            finally { Build(); }
        }); refresh.Name = "RefreshCommunity"; Grid.SetColumn(refresh, 3); filters.Children.Add(refresh);
        var source = Button("Source ↗", () => OpenLink(CommunityCatalog.SourcePage)); Grid.SetColumn(source, 4); filters.Children.Add(source); Children.Add(filters); Children.Add(_catalogStatus);
        _section.SelectionChanged += (_, _) => Build(); _thisPlatform.IsCheckedChanged += (_, _) => Build();
        var messages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; messages.Children.Add(_message);
        _undo = Button("Undo removal", () =>
        {
            if (_removed is { } old) { var profile = _manager.Install(old.Json); _manager.SetEnabled(profile.Id, old.Enabled); _removed = null; _engine.S.Save(); Build(); _open("plugin:" + profile.Id); }
            return Task.CompletedTask;
        }); messages.Children.Add(_undo); Children.Add(messages); Children.Add(_list);
        _search.TextChanged += (_, _) => Build(); Build();
    }
    private bool Matches(PluginProfile p) => Matches(p.Name + " " + p.Description);
    private bool Matches(string text) => string.IsNullOrWhiteSpace(_search.Text) || text.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    private void Build()
    {
        _list.Children.Clear(); var installed = _manager.Installed;
        _discoverPage.Classes.Set("active", _discover); _installedPage.Classes.Set("active", !_discover);
        _summary.Text = installed.Length + " tabs installed"; _undo.IsVisible = _removed.HasValue; _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text);
        _section.IsVisible = _thisPlatform.IsVisible = _catalogStatus.IsVisible = _discover;
        _catalogStatus.Text = _catalog.Origin + " · " + _catalog.Snapshot.FetchedAtUtc.ToLocalTime().ToString("MMM d, yyyy") + " · Compatibility varies by project";
        if (_discover) { BuildDiscover(installed); _signature = Signature(installed); return; }
        if (_manager.HostPortPluginId is { } rootId)
        {
            var target = installed.FirstOrDefault(p => p.Profile.Id == rootId);
            _list.Children.Add(Label("Host / port connections → " + (target?.Profile.Name ?? "Removed integration") + (target?.Enabled == true ? "" : " (disabled)")));
            _list.Children.Add(Button("Use shared host / port bridge", () => { _manager.SetHostPortPlugin(null); _engine.S.Save(); Build(); return Task.CompletedTask; }));
        }
        _list.Children.Add(Label("YOUR GAME TABS", true));
        foreach (var item in installed.Where(p => Matches(p.Profile)))
        {
            var profile = item.Profile; var content = new StackPanel { Spacing = 3 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
            row.Children.Add(Label(PluginManager.TabTitle(profile) + "  ·  " + (item.Enabled ? "Enabled" : "Disabled"), true));
            var open = Button("Open tab", () => { _open("plugin:" + profile.Id); return Task.CompletedTask; }); open.Name = "OpenGame-" + profile.Id; Grid.SetColumn(open, 1); row.Children.Add(open);
            var remove = Button("Remove", () => { _removed = (_manager.Export(profile.Id), item.Enabled); _manager.Uninstall(profile.Id); _engine.S.Save(); _message.Text = "Removed " + profile.Name + "."; Build(); return Task.CompletedTask; }); remove.Name = "RemoveGame-" + profile.Id; Grid.SetColumn(remove, 2); row.Children.Add(remove);
            content.Children.Add(row); content.Children.Add(Label(profile.Description));
            var advanced = new StackPanel { Spacing = 4 };
            advanced.Children.Add(Button("Export plugin…", () => Export(profile)));
            content.Children.Add(new Expander { Header = "Share / update", Content = advanced }); _list.Children.Add(Card(content));
        }
        if (installed.Length == 0) _list.Children.Add(Label("Choose a game below to add its tab."));
        var catalog = PluginManager.Catalog();
        var available = catalog.Where(p => p.Kind == "cs2" || p.Companion == "overstim").Where(Matches).Where(p => !installed.Any(i => i.Profile.Id == p.Id && Version.Parse(i.Profile.Version) >= Version.Parse(p.Version))).ToArray();
        if (available.Length > 0) _list.Children.Add(Label("ADD / UPDATE A GAME", true));
        foreach (var profile in available) AddAvailable(profile, installed, _list);
        var custom = new StackPanel { Spacing = 6 };
        foreach (var profile in catalog.Where(p => p.Kind != "cs2" && p.Companion != "overstim").Where(Matches)) AddAvailable(profile, installed, custom);
        _list.Children.Add(new Expander { Header = "Other Buttplug / custom integrations", Content = custom });
        foreach (var error in _manager.Errors) _list.Children.Add(Label(error));
        _signature = Signature(installed);
    }
    private void BuildDiscover(InstalledPlugin[] installed)
    {
        _list.Children.Add(Label("Add tab sets up HapticLink. Install the external mod using its guide. Community games are not live-tested."));
        var entries = _catalog.Snapshot.Entries.Where(e => (_section.SelectedIndex == 2 || e.Section == (_section.SelectedIndex == 1 ? "Apps" : "Games")) && Matches(e.SearchText)
            && (_thisPlatform.IsChecked != true || e.Platform is "Cross platform" or "Check project" || e.Platform == (OperatingSystem.IsWindows() ? "Windows" : "Linux"))).OrderBy(e => e.Category).ThenBy(e => e.Name).ToArray();
        _catalogStatus.Text = entries.Length + " projects · " + _catalog.Origin + " · " + _catalog.Snapshot.FetchedAtUtc.ToLocalTime().ToString("MMM d, yyyy");
        foreach (var entry in entries)
        {
            var local = installed.FirstOrDefault(p => p.Profile.Id == entry.Id);
            var content = new StackPanel { Spacing = 4 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(Label(entry.Name, true));
            if (entry.ListsIntiface || entry.Id is "cs2-events" or "overwatch2")
            {
                var add = Button(local == null ? "Add tab" : "Open tab", () =>
                {
                    if (!_manager.Installed.Any(p => p.Profile.Id == entry.Id))
                    {
                        _manager.Install(PluginManager.Serialize(CommunityCatalog.Profile(entry))); _engine.S.Save(); Build();
                    }
                    _open("plugin:" + entry.Id); return Task.CompletedTask;
                }); add.Name = "CommunityAdd-" + entry.Id; Grid.SetColumn(add, 1); row.Children.Add(add);
            }
            else { var guide = Button("View project ↗", () => OpenLink(entry.Url)); Grid.SetColumn(guide, 1); row.Children.Add(guide); }
            content.Children.Add(row);
            content.Children.Add(Label(entry.Category + "  ·  " + entry.Platform + "  ·  " + entry.SetupKind));
            var state = Label(local != null ? "Connection tab added · external setup may still be needed" : entry.Id == "cs2-events" ? "HapticLink game-config installer available" : entry.Id == "overwatch2" ? "HapticLink companion setup available" : entry.ListsIntiface ? "Lists Intiface support · external setup needed" : "Connection support unknown · check project");
            state.Foreground = local != null ? Classic.Good : Classic.Accent;
            var details = new StackPanel { Spacing = 4 }; details.Children.Add(Label(entry.Details)); details.Children.Add(Label(entry.Url));
            details.Children.Add(Button("Open install guide ↗", () => OpenLink(entry.Url)));
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 }; footer.Children.Add(state);
            details.IsVisible = false;
            var detailButton = Button("Details / guide", () => { details.IsVisible = !details.IsVisible; return Task.CompletedTask; }); Grid.SetColumn(detailButton, 1); footer.Children.Add(detailButton);
            content.Children.Add(footer); content.Children.Add(details); _list.Children.Add(Card(content));
        }
        if (entries.Length == 0) _list.Children.Add(Label("No projects match. Try a game name, clear filters or refresh the community list."));
    }
    private async Task OpenLink(string url)
    {
        var top = TopLevel.GetTopLevel(this); if (top != null && PluginManager.ValidProjectUrl(url)) await top.Launcher.LaunchUriAsync(new Uri(url));
    }
    private void AddAvailable(PluginProfile profile, InstalledPlugin[] installed, StackPanel parent)
    {
        var local = installed.FirstOrDefault(p => p.Profile.Id == profile.Id);
        var update = local != null && Version.Parse(profile.Version) > Version.Parse(local.Profile.Version);
        var content = new StackPanel { Spacing = 3 }; var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(Label(profile.Name, true));
        var action = Button(local == null ? "Add tab" : update ? "Update" : "Open tab", () =>
        {
            if (local == null || update) { _manager.Install(PluginManager.Serialize(profile)); _engine.S.Save(); Build(); }
            _open("plugin:" + profile.Id); return Task.CompletedTask;
        }); action.Name = "Install-" + profile.Id; Grid.SetColumn(action, 1); row.Children.Add(action);
        content.Children.Add(row); content.Children.Add(Label(profile.Description)); parent.Children.Add(Card(content));
    }
    private string Signature(InstalledPlugin[] installed) => (_manager.HostPortPluginId ?? "") + "|" + string.Join("|", installed.Select(p => p.Profile.Id + ":" + p.Enabled + ":" + p.Profile.Version));
    public void Refresh() { if (Signature(_manager.Installed) != _signature) Build(); }
    private async Task Import()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import a HapticLink game plugin", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("Game plugin JSON") { Patterns = new[] { "*.json" } } } });
        if (files.Count == 0) return;
        await using var input = await files[0].OpenReadAsync(); using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer)) > 0) { if (data.Length + count > 131072) throw new InvalidDataException("Plugin exceeds 128 KiB."); data.Write(buffer, 0, count); }
        var profile = _manager.Install(System.Text.Encoding.UTF8.GetString(data.ToArray()).TrimStart('\uFEFF')); _engine.S.Save(); Build(); _open("plugin:" + profile.Id);
    }
    private async Task Export(PluginProfile profile)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage == null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export game plugin", SuggestedFileName = profile.Id + ".json", DefaultExtension = "json" });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync(); if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)); await writer.WriteAsync(_manager.Export(profile.Id));
    }
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { _message.Text = ex.Message; } finally { button.IsEnabled = true; _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text); } }; return button;
    }
    private static TextBlock Label(string text, bool bold = false) => new() { Text = text, FontSize = 11, Foreground = bold ? Classic.Text : Classic.Muted, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Border Card(Control child) => new() { Child = child, BorderBrush = Classic.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5) };
}
