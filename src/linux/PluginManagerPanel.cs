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
    private readonly Action<string> _open;
    private readonly TextBox _search = new() { Watermark = "Find a game or integration", Name = "PluginSearch" };
    private readonly TextBlock _summary = Label(""), _message = Label("");
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Button _undo;
    private (string Json, bool Enabled)? _removed;
    private string _signature = "";
    public PluginManagerPanel(Engine engine, Action<string> open)
    {
        _engine = engine; _manager = engine.Plugins; _open = open; Spacing = 6;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(Label("GAME PLUGINS", true)); Grid.SetColumn(_summary, 1); heading.Children.Add(_summary); Children.Add(heading);
        Children.Add(Label("Each added game gets its own tab with ready-made settings and setup controls."));
        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        search.Children.Add(_search); var import = Button("Import plugin…", Import); Grid.SetColumn(import, 1); search.Children.Add(import);
        var reload = Button("Reload", () => { _manager.Reload(); Build(); return Task.CompletedTask; }); Grid.SetColumn(reload, 2); search.Children.Add(reload); Children.Add(search);
        var messages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; messages.Children.Add(_message);
        _undo = Button("Undo removal", () =>
        {
            if (_removed is { } old) { var profile = _manager.Install(old.Json); _manager.SetEnabled(profile.Id, old.Enabled); _removed = null; _engine.S.Save(); Build(); _open("plugin:" + profile.Id); }
            return Task.CompletedTask;
        }); messages.Children.Add(_undo); Children.Add(messages); Children.Add(_list);
        _search.TextChanged += (_, _) => Build(); Build();
    }
    private bool Matches(PluginProfile p) => string.IsNullOrWhiteSpace(_search.Text) || (p.Name + " " + p.Description).Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    private void Build()
    {
        _list.Children.Clear(); var installed = _manager.Installed;
        _summary.Text = installed.Length + " tabs installed"; _undo.IsVisible = _removed.HasValue; _message.IsVisible = !string.IsNullOrWhiteSpace(_message.Text);
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
    private static string Signature(InstalledPlugin[] installed) => string.Join("|", installed.Select(p => p.Profile.Id + ":" + p.Enabled + ":" + p.Profile.Version));
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
