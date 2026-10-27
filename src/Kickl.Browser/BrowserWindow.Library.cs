using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Kickl.Core.Games;
using Kickl.Core.Markup;

namespace Kickl.Browser;

/// <summary>
/// The games folder on the home page. Every game found by <see cref="GameLibrary"/> gets a card in the arcade; clicking it opens a game
/// tab that runs the module in the same sandbox as a game on a page. Dropping a folder into <c>games/</c> is all it takes to add one.
/// </summary>
public sealed partial class BrowserWindow
{
    private const string LibraryPrefix = "lib:";

    private List<GameEntry> LibraryGames() => GameLibrary.Scan(GameLibrary.DefaultRoots(_session.Paths.Root));

    private Control LibraryCard(GameEntry game)
    {
        var broken = game.Error is not null;
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(T(game.Title, 13, Text, bold: true));
        panel.Children.Add(T(broken ? game.Error! : game.Tagline, 9.5, broken ? Red : Muted).WithWrap());
        panel.Children.Add(T(broken ? "CANNOT RUN" : $"PLAYABLE · wasm sandbox · {Math.Max(1, game.Bytes / 1024)} KB", 8.5, broken ? Red : game.Accent, mono: true));
        panel.Children.Add(T("@" + game.Author + (game.Card is not null ? "  ·  shared" : ""), 8.5, Muted, mono: true));
        if (PublishButton(game) is { } share) panel.Children.Add(share);

        var card = new Border
        {
            Width = 188,
            Height = 110,
            Margin = new Thickness(0, 0, 8, 8),
            Background = C("#111b26"),
            BorderBrush = C(broken ? Red : game.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Cursor = broken ? Cursor.Default : new Cursor(StandardCursorType.Hand),
            Child = panel
        };
        card.PointerPressed += (_, _) =>
        {
            if (Active is not { } tab) return;
            if (broken) SetStatus(game.Title + " · " + game.Error);
            else OpenLibraryGame(tab, game);
        };
        ToolTip.SetTip(card, $"games/{game.Id}/  ·  {game.WasmPath}");
        return card;
    }

    private void OpenLibraryGame(BrowserTab tab, GameEntry game)
    {
        StopAllGames();
        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            SetStatus("unlock the profile to play");
            return;
        }

        tab.Game = LibraryPrefix + game.Id;
        tab.Page = null;
        tab.Draft = null;
        tab.Error = null;
        tab.BuiltIn = null;
        tab.Address = "";
        tab.Title = "arcade · " + game.Title;
        tab.Unlocked.Clear();
        tab.Texts.Clear();
        ClearImages(tab);
        StartLibraryGame(tab, game);   // clicking the card is the deliberate "play": nothing starts on its own
    }

    private void StartLibraryGame(BrowserTab tab, GameEntry game)
    {
        StopAllGames();
        try
        {
            var running = _session.LoadLibraryGame(game);
            running.Start(Random.Shared.Next());
            var block = new PageBlock { Kind = BlockKind.Game, Id = game.Id, Text = game.Title, Bytes = (int) game.Bytes };
            _player = new GamePlayer(tab, block, Kickl.Core.Session.BrowserSession.LibrarySite, running, _session, SetStatus);
            SetStatus($"{game.Title} running · sandboxed WebAssembly · from the games folder");
        }
        catch (Exception ex)
        {
            SetStatus("game refused · " + ex.Message);
        }
        RenderAll(resetScroll: true);
        Dispatcher.UIThread.Post(() => _player?.Focus());
    }

    /// <summary>The tab for a game from the games folder: the running game, or a play card if it has been stopped or refused.</summary>
    private Control BuildLibraryGame(BrowserTab tab)
    {
        var id = tab.Game![LibraryPrefix.Length..];
        var game = LibraryGames().FirstOrDefault(g => g.Id == id);
        var stack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(42, 30, 42, 60)
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(Pill("ARCADE · WEBASSEMBLY SANDBOX", Accent, "#0b1b2c"));
        top.Children.Add(Pill("NO NETWORK · NO FILES · NO CLOCK", Green, "#102118"));
        stack.Children.Add(top);

        var back = Btn("BACK TO HOME", () => OpenHome(tab, resetSearch: false));
        back.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(back);

        if (game is null)
        {
            stack.Children.Add(T("This game is no longer in the games folder.", 12, Amber).WithWrap());
            return new Border { Child = stack };
        }

        stack.Children.Add(T(game.Title.ToUpperInvariant(), 27, Text, bold: true));
        stack.Children.Add(T(game.Tagline + "  ·  @" + game.Author, 11, Muted).WithWrap());

        if (_player is not null && _player.Matches(tab, game.Id))
        {
            stack.Children.Add(_player.Detach());   // a re-render keeps the running game instead of restarting it
        }
        else
        {
            var play = Btn("PLAY", () => StartLibraryGame(tab, game), accent: true);
            play.HorizontalAlignment = HorizontalAlignment.Left;
            play.Height = 32;
            stack.Children.Add(play);
        }
        return new Border { Child = stack };
    }
}
