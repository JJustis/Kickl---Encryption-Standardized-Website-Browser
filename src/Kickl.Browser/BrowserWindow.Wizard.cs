using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kickl.Core.Games;

namespace Kickl.Browser;

/// <summary>
/// The "add a game" wizard. Pick a compiled game, see it checked in the sandbox with a preview of what it draws, give it a title, and add it to the arcade,
/// publishing it under your own key so everyone else can download it. No folders, no JSON, no commands. The game is yours: it is signed with your
/// publisher key, only you can update it or take it down, and the arcade marks it as yours.
/// </summary>
public sealed partial class BrowserWindow
{
    private sealed class Wizard
    {
        public int Step;                                   // 0 file, 1 details, 2 share, 3 working, 4 done
        public string? FileName;
        public byte[]? Wasm;
        public GameInspection? Look;
        public string Title = "", Tagline = "", Author = "", Accent = "#7be0ff";
        public bool Share = true;
        public string? Problem;
        public bool Busy;
        public GameEntry? Result;
        public string? SharedAs;
        public string? ShareProblem;
        public readonly List<string> Log = [];
    }

    private static readonly string[] SwatchColors = ["#7be0ff", "#ff7a64", "#f2cc60", "#7ee787", "#d2a8ff", "#ffa657", "#79c0ff", "#ff9bce"];

    private Wizard? _wizard;
    private string _lastAuthor = "";
    private string? _myPublisherId;

    private void OpenWizard()
    {
        if (!_session.IsUnlocked) { _lockOverlay.IsVisible = true; SetStatus("unlock the profile to add a game"); return; }
        EnsureSharing();
        _wizard = new Wizard { Author = _lastAuthor, Share = _shareGames };
        RebuildWizard();
        _gameOverlay.IsVisible = true;
    }

    private void CloseWizard()
    {
        _wizard = null;
        _gameOverlay.IsVisible = false;
    }

    private void RebuildWizard()
    {
        var w = _wizard;
        if (w is null) return;
        _gameBody.Children.Clear();

        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dots.Children.Add(T("ADD A GAME", 12, Accent, bold: true));
        for (var i = 0; i < 3; i++)
            dots.Children.Add(Pill((i + 1).ToString(), i == Math.Min(w.Step, 2) ? Accent : (w.Step > i ? Green : Muted), i == Math.Min(w.Step, 2) ? "#0b1b2c" : "#141d27"));
        _gameBody.Children.Add(dots);

        switch (w.Step)
        {
            case 0: BuildStepFile(w); break;
            case 1: BuildStepDetails(w); break;
            case 2: BuildStepShare(w); break;
            case 3: BuildStepWorking(w); break;
            default: BuildStepDone(w); break;
        }
    }

    // ---------------------------------------------------------------- step 1: the file

    private void BuildStepFile(Wizard w)
    {
        _gameBody.Children.Add(T("Choose your game", 20, Text, bold: true));
        _gameBody.Children.Add(T("Pick the compiled game, a .wasm file. The browser runs it in the sandbox first, to check that it works and to show you what it draws. " +
                                 "Nothing is added or sent anywhere yet. (New to this? docs/26-game-tutorial.md builds one from scratch.)", 10.5, Muted).WithWrap());

        var choose = Btn(w.Wasm is null ? "CHOOSE A .WASM FILE…" : "CHOOSE ANOTHER FILE…", () => _ = ChooseGameFileAsync(), accent: w.Wasm is null);
        choose.HorizontalAlignment = HorizontalAlignment.Left;
        choose.Height = 32;
        _gameBody.Children.Add(choose);

        if (w.Problem is not null) _gameBody.Children.Add(T(w.Problem, 10.5, Red).WithWrap());

        if (w.Look is { } look)
        {
            var box = new StackPanel { Spacing = 5 };
            box.Children.Add(T(w.FileName ?? "game.wasm", 11, Text, mono: true, bold: true));
            if (look.Ok)
            {
                box.Children.Add(T($"✓ It runs in the sandbox · {Math.Max(1, look.Bytes / 1024)} KB", 10.5, Green));
                box.Children.Add(T($"✓ A frame uses {look.FramePercent:0.#}% of the instruction budget" + (look.FramePercent <= 70 ? "" : " (a lot)"),
                    10.5, look.FramePercent <= 70 ? Green : Amber));
                if (look.MemoryGrowth == 0) box.Children.Add(T("✓ Its memory stays steady while it runs", 10.5, Green));
                foreach (var warning in look.Warnings) box.Children.Add(T("⚠ " + warning, 10, Amber).WithWrap());
                if (look.Picture is { } picture) box.Children.Add(PicturePreview(picture));
                box.Children.Add(T("This is what it drew after a short test run with some key presses.", 9, Muted));
            }
            else
            {
                box.Children.Add(T("✗ " + look.Error, 10.5, Red).WithWrap());
            }
            _gameBody.Children.Add(new Border
            {
                Background = C("#0a121a"), BorderBrush = C(look.Ok ? "#245437" : "#6b2f2f"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = box
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
        var next = Btn("NEXT", () => { w.Step = 1; w.Problem = null; RebuildWizard(); }, accent: true);
        next.IsEnabled = w.Look?.Ok == true;
        buttons.Children.Add(next);
        buttons.Children.Add(Btn("CANCEL", CloseWizard));
        _gameBody.Children.Add(buttons);
    }

    private static Control PicturePreview(byte[] rgba)
    {
        var bitmap = new WriteableBitmap(new PixelSize(WasmGame.Width, WasmGame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using (var fb = bitmap.Lock())
        {
            var row = WasmGame.Width * 4;
            for (var y = 0; y < WasmGame.Height; y++) Marshal.Copy(rgba, y * row, fb.Address + y * fb.RowBytes, row);
        }
        var image = new Image
        {
            Source = bitmap, Width = WasmGame.Width * 1.5, Height = WasmGame.Height * 1.5, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        return new Border { BorderBrush = C(Line), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = image };
    }

    private async Task ChooseGameFileAsync()
    {
        var w = _wizard;
        if (w is null) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose your compiled game",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("WebAssembly game") { Patterns = ["*.wasm"] }]
            });
            if (files.Count == 0) return;

            w.Problem = null;
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > Kickl.Core.Games.GameCard.MaxGameBytes + 1) break;      // never read an enormous file into memory just to refuse it
            }
            var wasm = buffer.ToArray();
            w.FileName = files[0].Name;
            w.Wasm = wasm;
            w.Look = null;
            w.Busy = true;
            RebuildWizard();

            var look = await Task.Run(() => GameImport.Inspect(wasm));
            if (!ReferenceEquals(_wizard, w)) return;
            w.Busy = false;
            w.Look = look;
            if (look.Ok && w.Title.Length == 0)
                w.Title = GameImport.SuggestId(Path.GetFileNameWithoutExtension(w.FileName)).Replace('-', ' ') is var t && t.Length > 0
                    ? string.Join(' ', t.Split(' ').Select(x => x.Length > 0 ? char.ToUpperInvariant(x[0]) + x[1..] : x))
                    : "";
            RebuildWizard();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            w.Problem = "Could not read that file: " + ex.Message;
            w.Busy = false;
            RebuildWizard();
        }
    }

    // ---------------------------------------------------------------- step 2: the details

    private void BuildStepDetails(Wizard w)
    {
        _gameBody.Children.Add(T("Describe it", 20, Text, bold: true));
        _gameBody.Children.Add(T("This is what people see on the card in the arcade.", 10.5, Muted));

        var title = Input(w.Title, "title · up to 40 characters");
        var tagline = Input(w.Tagline, "one line about it · up to 90 characters (optional)");
        var author = Input(w.Author, "your name · up to 24 characters");
        var folder = T("", 9, Muted, mono: true);
        var note = T("", 9.5, Amber);

        void Refresh()
        {
            w.Title = title.Text ?? "";
            w.Tagline = tagline.Text ?? "";
            w.Author = author.Text ?? "";
            var id = GameImport.SuggestId(w.Title);
            folder.Text = "games/" + id;
            var existing = LibraryGames().FirstOrDefault(g => g.Id == id && _sharing is not null
                && Path.GetFullPath(g.Folder).StartsWith(Path.GetFullPath(_sharing.GamesRoot), StringComparison.OrdinalIgnoreCase));
            note.Text = existing is null ? "" : existing.Card is { } c && c.PublisherId == (_myPublisherId ??= _sharing?.MyPublisherId())
                ? $"This updates your game \"{existing.Title}\" (version {c.Version} → {c.Version + 1})."
                : $"A game called \"{existing.Title}\" is already in your games folder; this replaces it.";
        }
        title.TextChanged += (_, _) => Refresh();
        tagline.TextChanged += (_, _) => Refresh();
        author.TextChanged += (_, _) => Refresh();

        _gameBody.Children.Add(Field("TITLE", title));
        _gameBody.Children.Add(Field("TAGLINE", tagline));
        _gameBody.Children.Add(Field("AUTHOR", author));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void DrawSwatches()
        {
            swatches.Children.Clear();
            foreach (var color in SwatchColors)
            {
                var picked = string.Equals(color, w.Accent, StringComparison.OrdinalIgnoreCase);
                var swatch = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Background = C(color), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    BorderBrush = C(picked ? "#ffffff" : "#203044"), BorderThickness = new Thickness(picked ? 2 : 1)
                };
                var chosen = color;
                swatch.PointerPressed += (_, _) => { w.Accent = chosen; DrawSwatches(); };
                swatches.Children.Add(swatch);
            }
        }
        DrawSwatches();
        _gameBody.Children.Add(Field("COLOUR", swatches));
        _gameBody.Children.Add(folder);
        _gameBody.Children.Add(note);
        Refresh();

        if (w.Problem is not null) _gameBody.Children.Add(T(w.Problem, 10.5, Red).WithWrap());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Btn("BACK", () => { w.Step = 0; w.Problem = null; RebuildWizard(); }));
        buttons.Children.Add(Btn("NEXT", () =>
        {
            Refresh();
            try
            {
                GameImport.CheckText(GameImport.SuggestId(w.Title), w.Title, w.Tagline, w.Author, w.Accent);
                w.Problem = null;
                w.Step = 2;
            }
            catch (GameCardException ex) { w.Problem = ex.Message; }
            RebuildWizard();
        }, accent: true));
        buttons.Children.Add(Btn("CANCEL", CloseWizard));
        _gameBody.Children.Add(buttons);
    }

    // ---------------------------------------------------------------- step 3: add, and share

    private void BuildStepShare(Wizard w)
    {
        _gameBody.Children.Add(T("Add it to the arcade", 20, Text, bold: true));

        var card = new StackPanel { Spacing = 3 };
        card.Children.Add(T(w.Title, 14, Text, bold: true));
        card.Children.Add(T(w.Tagline.Length > 0 ? w.Tagline : "a WebAssembly game", 10, Muted).WithWrap());
        card.Children.Add(T($"@{w.Author}  ·  {Math.Max(1, (w.Look?.Bytes ?? 0) / 1024)} KB", 9, w.Accent, mono: true));
        _gameBody.Children.Add(new Border
        {
            Background = C("#111b26"), BorderBrush = C(w.Accent), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12), Child = card
        });

        var share = new CheckBox { Content = "Share it with everyone", IsChecked = w.Share, FontSize = 12 };
        share.IsCheckedChanged += (_, _) => { w.Share = share.IsChecked == true; RebuildWizard(); };
        _gameBody.Children.Add(share);

        if (w.Share)
        {
            _myPublisherId ??= _sharing?.MyPublisherId();
            _gameBody.Children.Add(T($"It is yours. The game is signed with your publisher key ({(_myPublisherId ?? "?")[..Math.Min(8, (_myPublisherId ?? "?").Length)]}), so only you can update it or take it down, " +
                                     "and everyone who downloads it can see that it came from you and has not been changed. Other computers show it in their arcade and download it from your " +
                                     "computer while the browser is open, or from anyone who already has it. Nothing is sent until you press the button.", 10, Muted).WithWrap());
            if (!_shareGames)
                _gameBody.Children.Add(T("Sharing is switched off on this computer, so people could only get it from computers that already have it. Switch SHARING on in the arcade after adding it.", 10, Amber).WithWrap());
        }
        else
        {
            _gameBody.Children.Add(T("It will only be on this computer. You can share it later from its card in the arcade.", 10, Muted).WithWrap());
        }

        if (w.Problem is not null) _gameBody.Children.Add(T(w.Problem, 10.5, Red).WithWrap());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Btn("BACK", () => { w.Step = 1; RebuildWizard(); }));
        buttons.Children.Add(Btn(w.Share ? "ADD AND SHARE" : "ADD TO MY ARCADE", () => _ = FinishWizardAsync(), accent: true));
        buttons.Children.Add(Btn("CANCEL", CloseWizard));
        _gameBody.Children.Add(buttons);
    }

    private async Task FinishWizardAsync()
    {
        var w = _wizard;
        var sharing = _sharing;
        if (w is null || sharing is null || w.Wasm is null) return;
        w.Step = 3;
        w.Log.Clear();
        w.Problem = null;
        w.ShareProblem = null;
        RebuildWizard();

        var id = GameImport.SuggestId(w.Title);
        try
        {
            w.Log.Add("Adding it to your games folder …");
            RebuildWizard();
            var entry = await Task.Run(() => GameImport.Add(sharing.GamesRoot, id, w.Wasm, w.Title.Trim(), w.Tagline.Trim(), w.Author.Trim(), w.Accent));
            w.Result = entry;
            _lastAuthor = w.Author.Trim();

            if (w.Share)
            {
                w.Log.Add("Signing it with your key and publishing it …");
                RebuildWizard();
                try
                {
                    var card = await sharing.PublishAsync(entry);
                    w.SharedAs = $"{card.PublisherShort}/{card.Id} · version {card.Version}";
                    w.Result = GameLibrary.Scan(GameLibrary.DefaultRoots(_session.Paths.Root)).FirstOrDefault(g => g.Id == id) ?? entry;
                }
                catch (Exception ex) when (ex is GameRegistryException or GameCardException or HttpRequestException or TaskCanceledException)
                {
                    // the game is added; only the sharing failed, and it can be retried from the game's card
                    w.ShareProblem = ex.Message;
                }
            }
            w.Step = 4;
            RefreshArcadeSoon(force: true);
            RenderAll(resetScroll: false);
            RebuildWizard();
        }
        catch (Exception ex) when (ex is GameCardException or GameRejectedException or IOException or UnauthorizedAccessException)
        {
            w.Step = 2;
            w.Problem = ex.Message;
            RebuildWizard();
        }
    }

    private void BuildStepWorking(Wizard w)
    {
        _gameBody.Children.Add(T("Working …", 20, Text, bold: true));
        foreach (var line in w.Log) _gameBody.Children.Add(T(line, 10.5, Muted));
    }

    // ---------------------------------------------------------------- done

    private void BuildStepDone(Wizard w)
    {
        _gameBody.Children.Add(T($"{w.Title} is in your arcade", 20, Green, bold: true));
        if (w.SharedAs is not null)
        {
            _gameBody.Children.Add(T("✓ Shared. Every computer running Kickl can now see it and download it from yours (while the browser is open) or from anyone who has already downloaded it.", 10.5, Text).WithWrap());
            _gameBody.Children.Add(T($"your game id: {w.SharedAs}", 9.5, Muted, mono: true));
            _gameBody.Children.Add(T("To update it later, run ADD A GAME again with the new file and the same title; its version goes up. To take it down, use STOP SHARING on its card.", 10, Muted).WithWrap());
        }
        else if (w.Share && w.ShareProblem is not null)
        {
            _gameBody.Children.Add(T("It is on this computer, but could not be shared yet: " + w.ShareProblem, 10.5, Amber).WithWrap());
            _gameBody.Children.Add(T("Press SHARE THIS GAME on its card to try again.", 10, Muted));
        }
        else
        {
            _gameBody.Children.Add(T("It is on this computer only. Press SHARE THIS GAME on its card whenever you want to publish it.", 10.5, Muted).WithWrap());
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
        if (w.Result is { } result)
            buttons.Children.Add(Btn("PLAY IT", () => { CloseWizard(); if (Active is { } tab) OpenLibraryGame(tab, result); }, accent: true));
        buttons.Children.Add(Btn("ADD ANOTHER", OpenWizard));
        buttons.Children.Add(Btn("CLOSE", CloseWizard));
        _gameBody.Children.Add(buttons);
    }
}
