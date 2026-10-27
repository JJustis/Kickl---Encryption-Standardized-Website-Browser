using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Kickl.Core.Games;

namespace Kickl.Browser;

/// <summary>
/// Sharing games between computers, in the arcade. The registry lists game <b>cards</b>; a card for a game this computer does not have appears
/// in the arcade, and clicking it asks before downloading the game from whoever has it (its publisher, or anyone who already downloaded it).
/// The download is checked against the publisher's signature, installed in the reader's games folder, and run in the same sandbox as any game.
/// This computer then serves the games it holds to others, and tells the registry so, unless the reader turns that off.
/// </summary>
public sealed partial class BrowserWindow
{
    private readonly StackPanel _gameBody = new() { Spacing = 8 };
    private Grid _gameOverlay = null!;
    private GameSharing? _sharing;
    private GameRegistryClient? _gameRegistry;
    private List<RemoteGame> _remoteGames = [];
    private DateTime _lastArcadeRefresh = DateTime.MinValue;
    private bool _arcadeRefreshing;
    private bool _shareGames = true;
    private CancellationTokenSource? _downloadCts;
    private DispatcherTimer? _announceTimer;
    private readonly List<(string Text, string Color)> _gameLog = [];

    // ---------------------------------------------------------------- setup

    private Grid BuildGameOverlay()
    {
        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Width = 620,
            MaxHeight = 660,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = _gameBody }
        };
        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#aa000000") });
        grid.Children.Add(sheet);
        return grid;
    }

    private string ArcadePrefsPath => Path.Combine(_session.Paths.ProfileRoot, "private", "arcade-prefs.kpf");

    /// <summary>Create the sharing service for the unlocked profile (once), restore its on/off setting, and start serving.</summary>
    private GameSharing? EnsureSharing()
    {
        if (!_session.IsUnlocked) return null;
        if (_sharing is not null) return _sharing;

        _gameRegistry = new GameRegistryClient();
        _sharing = new GameSharing(_session, _gameRegistry);
        try
        {
            if (File.Exists(ArcadePrefsPath))
            {
                var plain = _session.OpenProfileData("arcade-prefs", File.ReadAllBytes(ArcadePrefsPath));
                using var doc = JsonDocument.Parse(plain);
                if (doc.RootElement.TryGetProperty("share", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False) _shareGames = s.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException) { }

        if (_shareGames) StartServing();
        return _sharing;
    }

    private void SaveArcadePrefs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArcadePrefsPath)!);
            File.WriteAllBytes(ArcadePrefsPath, _session.ProtectProfileData("arcade-prefs",
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { share = _shareGames }))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void StartServing()
    {
        if (_sharing is null) return;
        if (!_sharing.StartSeeding(LibraryGames))
        {
            SetStatus("could not start sharing games (no free port)");
            return;
        }
        Announce();

        // The registry forgets a source that has been quiet for 36 hours, so a browser that stays open says so again every six hours
        _announceTimer?.Stop();
        _announceTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _announceTimer.Tick += (_, _) => Announce();
        _announceTimer.Start();
    }

    private void Announce()
    {
        var sharing = _sharing;
        if (sharing is null) return;
        _ = Task.Run(async () =>
        {
            try { await sharing.AnnounceAllAsync(); }
            catch (Exception ex) when (ex is GameRegistryException or HttpRequestException or TaskCanceledException) { }
        });
    }

    /// <summary>Stop serving and forget the sharing service. Called when the profile locks.</summary>
    private void StopSharing()
    {
        _downloadCts?.Cancel();
        _announceTimer?.Stop();
        _announceTimer = null;
        _gameOverlay.IsVisible = false;
        _wizard = null;
        _myPublisherId = null;
        _sharing?.Dispose();
        _sharing = null;
        _gameRegistry?.Dispose();
        _gameRegistry = null;
        _remoteGames = [];
        _lastArcadeRefresh = DateTime.MinValue;
    }

    // ---------------------------------------------------------------- the arcade pane

    /// <summary>Fetch the registry's cards in the background (at most every couple of minutes) and redraw the home page if something changed.</summary>
    private void RefreshArcadeSoon(bool force = false)
    {
        var sharing = EnsureSharing();
        if (sharing is null || _arcadeRefreshing) return;
        if (!force && DateTime.UtcNow - _lastArcadeRefresh < TimeSpan.FromMinutes(2)) return;
        _arcadeRefreshing = true;
        _lastArcadeRefresh = DateTime.UtcNow;
        var epoch = _profileEpoch;
        _ = Task.Run(async () =>
        {
            string? note = null;
            try { await sharing.RefreshAsync(); }
            catch (Exception ex) when (ex is GameRegistryException or HttpRequestException or TaskCanceledException)
            {
                note = "game directory unreachable · showing what is already known";
            }
            Dispatcher.UIThread.Post(() =>
            {
                _arcadeRefreshing = false;
                if (epoch != _profileEpoch || !ReferenceEquals(sharing, _sharing)) return;
                var before = _remoteGames.Select(g => g.Card.Sha256).ToList();
                _remoteGames = sharing.Missing(LibraryGames());
                if (note is not null) SetStatus(note);
                if (!before.SequenceEqual(_remoteGames.Select(g => g.Card.Sha256)) && Active is { Page: null, Game: null, BuiltIn: null })
                    RenderAll(resetScroll: false);
            });
        });
    }

    /// <summary>The line above the games: whether this computer shares what it has, and a switch.</summary>
    private Control SharingBar()
    {
        var sharing = EnsureSharing();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var on = _shareGames && sharing?.IsSeeding == true;
        row.Children.Add(Btn("ADD A GAME…", OpenWizard, accent: true));
        row.Children.Add(Btn(_shareGames ? "SHARING: ON" : "SHARING: OFF", ToggleSharing, accent: _shareGames));
        row.Children.Add(T(_shareGames
                ? (on ? "this computer hands the games it has to people who click them" : "sharing is on but could not start")
                : "this computer does not hand games to anyone",
            9, Muted, mono: true));
        ToolTip.SetTip(row, "When on, this computer answers requests for the games it holds over an encrypted connection pinned to its own key, and tells the registry it can. " +
                            "It listens on one port, and only ever serves game files. Turn it off and it stops.");
        return row;
    }

    private void ToggleSharing()
    {
        if (!_session.IsUnlocked) { _lockOverlay.IsVisible = true; return; }
        var sharing = EnsureSharing();
        _shareGames = !_shareGames;
        if (_shareGames) StartServing();
        else { sharing?.StopSeeding(); _announceTimer?.Stop(); }
        SaveArcadePrefs();
        SetStatus(_shareGames ? "sharing games from this computer" : "no longer sharing games from this computer");
        RenderAll(resetScroll: false);
    }

    private Control RemoteCard(RemoteGame remote)
    {
        var card = remote.Card;
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(T(card.Title, 13, Text, bold: true));
        panel.Children.Add(T(card.Tagline.Length > 0 ? card.Tagline : "a WebAssembly game", 9.5, Muted).WithWrap());
        var meta = (remote.IsUpdate ? "UPDATE · " : "DOWNLOAD · ") + $"{Math.Max(1, card.Size / 1024)} KB · " +
                   (card.Sources == 1 ? "1 source" : $"{card.Sources} sources");
        panel.Children.Add(T(meta, 8.5, card.Accent, mono: true));
        panel.Children.Add(T("@" + card.Author, 8.5, Muted, mono: true));

        var border = new Border
        {
            Width = 188,
            Height = 110,
            Margin = new Thickness(0, 0, 8, 8),
            Background = C("#0d1620"),
            BorderBrush = C(card.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Opacity = 0.82,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = panel
        };
        border.PointerPressed += (_, _) => OpenGameDownload(remote);
        ToolTip.SetTip(border, "Not on this computer yet. Click to download it from whoever has it.");
        return border;
    }

    /// <summary>A small button on a game of the reader's own that has not been shared: signs a card and puts it in the registry.</summary>
    private Control? PublishButton(GameEntry game)
    {
        if (_sharing is null || game.Error is not null || game.Card is not null) return null;
        // only games the reader put in their own games folder; the ones that ship with the program are already everyone's
        if (!Path.GetFullPath(game.Folder).StartsWith(Path.GetFullPath(_sharing.GamesRoot), StringComparison.OrdinalIgnoreCase)) return null;
        var button = Btn("SHARE THIS GAME", () => _ = PublishLibraryGameAsync(game), accent: true);
        button.FontSize = 7.5;
        button.Padding = new Thickness(6, 1);
        button.Height = 20;
        ToolTip.SetTip(button, "Publish this game's card to the registry so other computers can download it from yours.");
        return button;
    }

    /// <summary>On a game you published: take its card out of the registry. The game stays on this computer.</summary>
    private Control StopSharingButton(GameEntry game)
    {
        var button = Btn("STOP SHARING", () => _ = UnpublishLibraryGameAsync(game));
        button.FontSize = 7.5;
        button.Padding = new Thickness(6, 1);
        button.Height = 20;
        ToolTip.SetTip(button, "Remove this game's card from the registry so nobody new can download it. It stays on this computer, and on any computer that already has it.");
        return button;
    }

    private async Task UnpublishLibraryGameAsync(GameEntry game)
    {
        var sharing = EnsureSharing();
        if (sharing is null) { _lockOverlay.IsVisible = true; return; }
        try
        {
            await sharing.UnpublishAsync(game);
            SetStatus($"{game.Title} is no longer shared; it stays on this computer");
            RenderAll(resetScroll: false);
        }
        catch (Exception ex) when (ex is GameRegistryException or GameCardException or HttpRequestException or TaskCanceledException)
        {
            SetStatus("could not stop sharing · " + ex.Message);
        }
    }

    private async Task PublishLibraryGameAsync(GameEntry game)
    {
        var sharing = EnsureSharing();
        if (sharing is null) { _lockOverlay.IsVisible = true; return; }
        SetStatus($"publishing {game.Title} …");
        try
        {
            var published = await sharing.PublishAsync(game);
            SetStatus($"{game.Title} is in the registry (version {published.Version}); other computers can now download it from this one");
            RefreshArcadeSoon(force: true);
            RenderAll(resetScroll: false);
        }
        catch (Exception ex) when (ex is GameRegistryException or GameCardException or GameRejectedException or HttpRequestException or TaskCanceledException)
        {
            SetStatus("could not publish · " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- the download prompt

    private void OpenGameDownload(RemoteGame remote)
    {
        if (!_session.IsUnlocked) { _lockOverlay.IsVisible = true; SetStatus("unlock the profile to download games"); return; }
        _gameLog.Clear();
        RebuildGameSheet(remote, downloading: false);
        _gameOverlay.IsVisible = true;
    }

    private void RebuildGameSheet(RemoteGame remote, bool downloading)
    {
        var card = remote.Card;
        _gameBody.Children.Clear();
        _gameBody.Children.Add(T(remote.IsUpdate ? "UPDATE A GAME" : "DOWNLOAD A GAME", 12, Accent, bold: true));
        _gameBody.Children.Add(T(card.Title, 22, Text, bold: true));
        if (card.Tagline.Length > 0) _gameBody.Children.Add(T(card.Tagline, 11.5, Muted).WithWrap());

        var facts = new StackPanel { Spacing = 2 };
        facts.Children.Add(T($"by @{card.Author}   ·   publisher key {card.PublisherShort}", 10, Text, mono: true));
        facts.Children.Add(T($"{card.Size:N0} bytes   ·   version {card.Version}   ·   {(card.Sources == 1 ? "1 computer" : $"{card.Sources} computers")} can send it right now", 10, Muted, mono: true));
        facts.Children.Add(T("sha-256 " + card.Sha256[..16] + "…", 9, Muted, mono: true));
        _gameBody.Children.Add(new Border
        {
            Background = C("#0a121a"), BorderBrush = C(Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 8), Child = facts
        });

        _gameBody.Children.Add(T("This game is not on this computer. It will be fetched from the computer that published it, or from another person who has already " +
                                 "downloaded it, and checked against the publisher's signature before it is kept. It runs in the sandbox: no network, no files, no clock. " +
                                 "Nothing is sent to the publisher.", 10, Muted).WithWrap());
        if (card.Sources == 0)
            _gameBody.Children.Add(T("No computer is listed as having it at the moment. You can still try; it works if one comes online.", 10, Amber).WithWrap());

        foreach (var (text, color) in _gameLog)
            _gameBody.Children.Add(new Border
            {
                BorderBrush = C(color), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(9, 2, 4, 2),
                Child = T(text, 10.5, color == Text ? "#c9d6e4" : color).WithWrap()
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
        if (!downloading)
        {
            buttons.Children.Add(Btn(_gameLog.Count > 0 ? "TRY AGAIN" : (remote.IsUpdate ? "UPDATE" : "DOWNLOAD"), () => _ = DownloadGameAsync(remote), accent: true));
            buttons.Children.Add(Btn("CANCEL", () => _gameOverlay.IsVisible = false));
        }
        else
        {
            buttons.Children.Add(T("downloading …", 10.5, Accent, mono: true));
            buttons.Children.Add(Btn("STOP", () => _downloadCts?.Cancel()));
        }
        _gameBody.Children.Add(buttons);
    }

    private async Task DownloadGameAsync(RemoteGame remote)
    {
        var sharing = EnsureSharing();
        if (sharing is null) return;
        _downloadCts?.Cancel();
        var cts = _downloadCts = new CancellationTokenSource();
        _gameLog.Clear();
        RebuildGameSheet(remote, downloading: true);

        void Note(string text) => Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested) return;
            _gameLog.Add((text, Muted));
            RebuildGameSheet(remote, downloading: true);
        });

        try
        {
            var entry = await Task.Run(() => sharing.DownloadAsync(remote.Card, Note, cts.Token));
            _gameLog.Add(("Downloaded and checked. Opening it …", Green));
            _gameOverlay.IsVisible = false;
            _remoteGames = sharing.Missing(LibraryGames());
            SetStatus($"{entry.Title} downloaded · this computer can now share it too");
            if (Active is { } tab) OpenLibraryGame(tab, entry);
        }
        catch (OperationCanceledException)
        {
            _gameLog.Clear();
            _gameLog.Add(("Stopped.", Amber));
            RebuildGameSheet(remote, downloading: false);
        }
        catch (Exception ex) when (ex is GameCardException or GameRegistryException or GameRejectedException or HttpRequestException or IOException)
        {
            _gameLog.Add((ex.Message, Red));
            RebuildGameSheet(remote, downloading: false);
        }
    }
}
