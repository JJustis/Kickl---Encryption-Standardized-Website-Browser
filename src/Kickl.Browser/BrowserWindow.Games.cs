using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Kickl.Core.Games;
using Kickl.Core.Markup;

namespace Kickl.Browser;

/// <summary>
/// Runs a page's <c>&lt;game&gt;</c> (a sealed WebAssembly module) in the sandbox from Kickl.Core. Nothing runs until
/// the reader presses PLAY. At most one game runs at a time, and it stops when the page changes, the tab
/// changes, the profile locks or the window closes.
/// </summary>
public sealed partial class BrowserWindow
{
    private GamePlayer? _player;

    private Control BuildGame(BrowserTab tab, PageBlock block)
    {
        var id = block.Id ?? "game";
        if (_player is not null && _player.Matches(tab, id))
            return _player.Detach();   // a re-render keeps the running game instead of restarting it

        var label = T($"{id} · GAME · {block.Text}", 10.5, Accent, bold: true);
        var detail = T($"WebAssembly sandbox · {block.Bytes:N0} bytes · no network, files, clock or host calls · " +
                       "does not start until you press play", 9.5, Muted);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new StackPanel { Spacing = 2, Children = { label, detail } });
        var play = Btn("PLAY", () => StartGame(tab, block), accent: true);
        play.Height = 28;
        row.Children.Add(play);
        Grid.SetColumn(play, 1);

        return new Border
        {
            Background = C("#101a24"),
            BorderBrush = C("#25415c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13),
            Child = row
        };
    }

    private void StartGame(BrowserTab tab, PageBlock block)
    {
        StopAllGames();
        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            SetStatus("unlock the profile to play");
            return;
        }

        try
        {
            var site = tab.Page?.Site ?? "";
            var game = _session.LoadGame(block.Id!);
            game.Start(Random.Shared.Next());
            _player = new GamePlayer(tab, block, site, game, _session, SetStatus);
            RenderAll(resetScroll: false);
            Dispatcher.UIThread.Post(() => _player?.Focus());
            SetStatus($"{block.Text} running · sandboxed WebAssembly");
        }
        catch (Exception ex)
        {
            SetStatus("game refused · " + ex.Message);
        }
    }

    /// <summary>Stop the running game, keep its save, and put the page back to its play card.</summary>
    private void StopAllGames(bool rerender = false)
    {
        if (_player is null) return;
        _player.Dispose();
        _player = null;
        if (rerender) RenderAll(resetScroll: false);
    }

    private sealed class GamePlayer : IDisposable
    {
        private readonly BrowserTab _tab;
        private readonly string _blobId;
        private readonly string _site;
        private readonly WasmGame _game;
        private readonly Kickl.Core.Session.BrowserSession _session;
        private readonly Action<string> _status;
        private readonly WriteableBitmap _bitmap;
        private readonly Image _view;
        private readonly TextBlock _state;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly byte[] _pixels = new byte[WasmGame.FrameBytes];
        private long _lastTick;
        private long _lastSaveCheck;
        private byte[] _lastSave;
        private int _buttons;
        private int _pointerX, _pointerY;
        private bool _pointerDown;
        private bool _disposed;

        public Border Root { get; }

        public GamePlayer(BrowserTab tab, PageBlock block, string site, WasmGame game,
            Kickl.Core.Session.BrowserSession session, Action<string> status)
        {
            _tab = tab;
            _blobId = block.Id!;
            _site = site;
            _game = game;
            _session = session;
            _status = status;
            _lastSave = game.ReadSave();

            _bitmap = new WriteableBitmap(new PixelSize(WasmGame.Width, WasmGame.Height), new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Opaque);
            _view = new Image
            {
                Source = _bitmap,
                Width = WasmGame.Width * 3,
                Height = WasmGame.Height * 3,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            RenderOptions.SetBitmapInterpolationMode(_view, BitmapInterpolationMode.None);

            _state = new TextBlock { FontSize = 9.5, Foreground = new SolidColorBrush(Color.Parse("#7ee787")), Text = "running" };
            var title = new TextBlock
            {
                Text = $"{_blobId} · GAME · {block.Text}",
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#58a6ff"))
            };
            var stop = new Button { Content = "STOP", FontSize = 10, Padding = new Thickness(9, 3) };
            stop.Click += (_, _) => Stop();
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            header.Children.Add(title);
            _state.Margin = new Thickness(0, 0, 10, 0);
            _state.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(_state);
            Grid.SetColumn(_state, 1);
            header.Children.Add(stop);
            Grid.SetColumn(stop, 2);

            var hint = new TextBlock
            {
                Text = "arrows or WASD · Z/J/space = A · X/K = B · Enter = start · or use the mouse. Click the picture first.",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse("#8b9bb0")),
                TextWrapping = TextWrapping.Wrap
            };

            Root = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0a1119")),
                BorderBrush = new SolidColorBrush(Color.Parse("#25415c")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(13),
                Focusable = true,
                Child = new StackPanel { Spacing = 8, Children = { header, _view, hint } }
            };
            Root.KeyDown += OnKeyDown;
            Root.KeyUp += OnKeyUp;
            _view.PointerPressed += OnPointer;
            _view.PointerMoved += OnPointer;
            _view.PointerReleased += (_, _) => _pointerDown = false;
            _view.PointerCaptureLost += (_, _) => _pointerDown = false;

            _lastTick = _clock.ElapsedMilliseconds;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();
        }

        public bool Matches(BrowserTab tab, string blobId) =>
            !_disposed && ReferenceEquals(_tab, tab) && _blobId == blobId;

        /// <summary>Hand back the control, freeing it from the visual tree of the page that is being replaced.</summary>
        public Control Detach()
        {
            (Root.Parent as Panel)?.Children.Remove(Root);
            if (Root.Parent is Decorator d) d.Child = null;
            return Root;
        }

        public void Focus() => Root.Focus();

        private void Tick()
        {
            if (_disposed) return;
            var now = _clock.ElapsedMilliseconds;
            var dt = (int)(now - _lastTick);
            _lastTick = now;

            var alive = _game.Frame(dt, new GameInput(_buttons, _pointerX, _pointerY, _pointerDown));
            if (!_game.CopyFrame(_pixels))
                alive = false;
            Present();

            if (!alive)
            {
                _timer.Stop();
                _state.Foreground = new SolidColorBrush(Color.Parse("#ff7b72"));
                _state.Text = "stopped: " + (_game.HaltReason ?? "the game ended");
                _status("game stopped · " + (_game.HaltReason ?? "ended"));
                SaveIfChanged(force: true);
                return;
            }

            if (now - _lastSaveCheck > 3000)
            {
                _lastSaveCheck = now;
                SaveIfChanged();
            }
        }

        private void Present()
        {
            using var fb = _bitmap.Lock();
            var rowBytes = WasmGame.Width * 4;
            if (fb.RowBytes == rowBytes)
            {
                Marshal.Copy(_pixels, 0, fb.Address, _pixels.Length);
            }
            else
            {
                for (var y = 0; y < WasmGame.Height; y++)
                    Marshal.Copy(_pixels, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
            }
            _view.InvalidateVisual();
        }

        private void SaveIfChanged(bool force = false)
        {
            try
            {
                var save = _game.ReadSave();
                if (!force && save.AsSpan().SequenceEqual(_lastSave)) return;
                if (save.Length == 0 || save.AsSpan().SequenceEqual(_lastSave)) return;
                if (!_session.IsUnlocked) return;
                _session.SaveGameData(_site, _blobId, save);
                _lastSave = save;
            }
            catch (Exception ex)
            {
                _status("game save failed · " + ex.Message);
            }
        }

        private void Stop()
        {
            _timer.Stop();
            SaveIfChanged(force: true);
            _state.Foreground = new SolidColorBrush(Color.Parse("#8b9bb0"));
            _state.Text = "stopped";
            _status("game stopped");
        }

        private void OnPointer(object? sender, PointerEventArgs e)
        {
            var p = e.GetCurrentPoint(_view);
            var w = Math.Max(1, _view.Bounds.Width);
            var h = Math.Max(1, _view.Bounds.Height);
            _pointerX = (int)(p.Position.X * WasmGame.Width / w);
            _pointerY = (int)(p.Position.Y * WasmGame.Height / h);
            if (e.RoutedEvent == InputElement.PointerPressedEvent)
            {
                _pointerDown = p.Properties.IsLeftButtonPressed;
                Root.Focus();
            }
            else if (e.RoutedEvent == InputElement.PointerMovedEvent)
            {
                _pointerDown = _pointerDown && p.Properties.IsLeftButtonPressed;
            }
        }

        private static int ButtonFor(Key key) => key switch
        {
            Key.Left or Key.A => GameButtons.Left,
            Key.Right or Key.D => GameButtons.Right,
            Key.Up or Key.W => GameButtons.Up,
            Key.Down or Key.S => GameButtons.Down,
            Key.Z or Key.J or Key.Space => GameButtons.A,
            Key.X or Key.K => GameButtons.B,
            Key.Enter => GameButtons.Start,
            _ => 0
        };

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var b = ButtonFor(e.Key);
            if (b == 0) return;
            _buttons |= b;
            e.Handled = true;   // arrows and space must not scroll the page while playing
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            var b = ButtonFor(e.Key);
            if (b == 0) return;
            _buttons &= ~b;
            e.Handled = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _timer.Stop();
            SaveIfChanged(force: true);
            _disposed = true;
            _game.Dispose();
            _bitmap.Dispose();
        }
    }
}
