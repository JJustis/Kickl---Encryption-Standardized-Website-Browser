using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.News;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Session;
using Kickl.Core.Util;

namespace Kickl.Device;

/// <summary>
/// A working desktop emulation of the two-screen Kickl handheld:
///   top screen    = public RSS scroller + stylus-style mini search
///   inner screen  = browser, search, confirmation sheet, Nickles slot
///   keyboard      = on-screen QWERTY that types into the last focused field
/// The demo profile and fixture site are created on first launch (PIN 1234).
/// </summary>
public sealed class DeviceWindow : Window
{
    private const string DemoPin = "1234";
    private const string HomeHost = "pinwheelexplained.com";
    private const string HomeUrl = "jttp://pinwheelexplained.com/";
    private const string PublicationsFile = @"C:\xampp\htdocs\rssfeed.xml";

    private readonly BrowserSession _session;
    private readonly string _dataRoot;
    private string OwnerDongleDirectory => Path.Combine(_dataRoot, "owner-dongle-emulator");
    private readonly List<SiteSummary> _hits = [];

    private readonly TextBox _urlBox = new();
    private readonly TextBox _mainSearch = new();
    private readonly TextBox _topSearch = new();
    private readonly TextBox _pinBox = new();
    private readonly TextBlock _page = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _sessionLabel = new();
    private readonly TextBlock _clock = new();
    private readonly TextBlock _confirmText = new();
    private readonly StackPanel _innerHits = new();
    private readonly StackPanel _topHits = new();
    private readonly StackPanel _blobBar = new();
    private readonly Border _confirm;
    private readonly Border _topSearchPanel;
    private readonly Border _innerSearchPanel;
    private readonly ScrollViewer _rssScroll;
    private readonly StackPanel _rssItems = new();

    private TextBox? _focus;
    private bool _shift;
    private bool _rssPaused;
    private SiteSummary? _pending;

    public DeviceWindow()
    {
        Title = "Kickl Pinwheel — handheld emulator";
        Width = 1180;
        Height = 1000;
        MinWidth = 1020;
        MinHeight = 900;
        Background = C("#05070a");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KicklDevice");

        _session = new BrowserSession(_dataRoot);
        _confirm = BuildConfirmSheet();
        _topSearchPanel = BuildTopSearchOverlay();
        _innerSearchPanel = BuildInnerSearchOverlay();
        _rssScroll = BuildRssFeed();
        RefreshRssFeed();

        Content = BuildShell();
        BootstrapDemo();
        StartRssTicker();
        StartRssRefresh();
        StartClock();

        if (Application.Current is not null)
            Application.Current.Styles.Add(new Style(x => x.OfType<TextBox>())
            {
                Setters =
                {
                    new Setter(TextBox.BackgroundProperty, C("#0d141d")),
                    new Setter(TextBox.ForegroundProperty, C("#dbe7f3")),
                    new Setter(TextBox.BorderBrushProperty, C("#26374a")),
                    new Setter(TextBox.CornerRadiusProperty, new CornerRadius(7)),
                    new Setter(TextBox.PaddingProperty, new Thickness(9, 6))
                }
            });
    }

    // ------------------------------------------------------------------ shell

    private Control BuildShell()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,236,22,*,238,26"),
            Margin = new Thickness(12)
        };

        var header = BuildHeader();
        root.Children.Add(header);
        Grid.SetRow(header, 0);

        var top = BuildTopScreen();
        root.Children.Add(top);
        Grid.SetRow(top, 1);

        var hinge = BuildHinge();
        root.Children.Add(hinge);
        Grid.SetRow(hinge, 2);

        var inner = BuildInnerScreen();
        root.Children.Add(inner);
        Grid.SetRow(inner, 3);

        var keyboard = BuildKeyboard();
        root.Children.Add(keyboard);
        Grid.SetRow(keyboard, 4);

        var footer = BuildFooter();
        root.Children.Add(footer);
        Grid.SetRow(footer, 5);

        return new Border
        {
            Background = C("#0d141d"),
            BorderBrush = C("#26374a"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(18),
            Child = root
        };
    }

    private Control BuildHeader()
    {
        var left = new StackPanel { Spacing = 1 };
        left.Children.Add(T("KICKL · PINWHEEL", 19, "#e6edf3", bold: true));
        left.Children.Add(T("handheld emulator · two screens · sealed jttp://", 11, "#7d90a8"));

        _clock.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _clock.FontSize = 12;
        _clock.Foreground = C("#8fa3ba");
        _clock.VerticalAlignment = VerticalAlignment.Center;

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        right.Children.Add(_clock);
        right.Children.Add(Pill("SEALED SURFACE", "#7ee787", "#102118"));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 4, 10) };
        row.Children.Add(left);
        row.Children.Add(right);
        Grid.SetColumn(right, 1);
        return row;
    }

    private Control BuildTopScreen()
    {
        var screen = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = C("#05080c")
        };

        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(12, 8, 12, 4)
        };
        bar.Children.Add(T("PUBLICATIONS · ALWAYS ON FRONT SCREEN", 10, "#6f849a", bold: true));
        var stylus = SmallButton("✎ STYLUS SEARCH");
        stylus.Click += (_, _) => ToggleTopSearch();
        bar.Children.Add(stylus);
        Grid.SetColumn(stylus, 1);
        var pair = SmallButton("OWNER PAIR");
        pair.Click += async (_, _) => await OpenOwnerPairFlow();
        bar.Children.Add(pair);
        Grid.SetColumn(pair, 1);
        Grid.SetColumn(stylus, 2);
        screen.Children.Add(bar);

        screen.Children.Add(_rssScroll);
        Grid.SetRow(_rssScroll, 1);

        var overlayHost = new Grid();
        overlayHost.Children.Add(screen);
        overlayHost.Children.Add(_topSearchPanel);

        return new Border
        {
            Background = C("#05080c"),
            BorderBrush = C("#1f2d3d"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = overlayHost
        };
    }

    private async Task OpenOwnerPairFlow()
    {
        var site = _session.Current?.Host;
        if (string.IsNullOrWhiteSpace(site))
        {
            _status.Text = "open a site before authorizing an owner proof";
            return;
        }

        var enrolling = !File.Exists(OwnershipAuthority.SecretPath(OwnerDongleDirectory, site));
        var passphrase = new TextBox { PasswordChar = '*', Watermark = "Owner credential passphrase", MinWidth = 310 };
        var confirmation = new TextBox { PasswordChar = '*', Watermark = "Confirm passphrase", MinWidth = 310,
            IsVisible = enrolling };
        var detail = T(enrolling
            ? $"Enroll an emulated owner credential for {site}. It is stored under this emulator's local data folder."
            : $"Unlock the owner credential for {site}. The passphrase is entered on this trusted-device emulator.",
            11, "#8fa3ba");
        detail.TextWrapping = TextWrapping.Wrap;
        var status = T("Select Wi-Fi discovery for a physical receiver, or leave it empty to use the local process emulator.", 10, "#f2cc60");
        status.TextWrapping = TextWrapping.Wrap;
        var fingerprintApproval = new CheckBox
        {
            Content = "I have verified the receiver fingerprint and want to pin this key.",
            IsVisible = false,
            Foreground = C("#c9d6e4")
        };
        var wifiCandidates = new ComboBox { MinWidth = 310 };
        var discoverWifi = SmallButton("DISCOVER WI-FI DONGLES");
        discoverWifi.Click += async (_, _) =>
        {
            discoverWifi.IsEnabled = false;
            status.Foreground = C("#f2cc60");
            status.Text = "Searching the current Wi-Fi link for dongles…";
            try
            {
                wifiCandidates.ItemsSource = await WifiDongleTransport.DiscoverAsync();
                wifiCandidates.SelectedIndex = wifiCandidates.ItemCount > 0 ? 0 : -1;
                status.Text = wifiCandidates.ItemCount > 0
                    ? $"Found {wifiCandidates.ItemCount} untrusted Wi-Fi candidate(s). Identity is checked after connection."
                    : "No Wi-Fi dongles answered discovery on this network.";
            }
            catch (Exception ex) { status.Text = "Wi-Fi discovery failed: " + ex.Message; }
            finally { discoverWifi.IsEnabled = true; }
        };
        string? shownFingerprint = null;

        var approve = SmallButton(enrolling ? "ENROLL + PAIR TEST" : "AUTHORIZE + PAIR TEST");
        var cancel = SmallButton("CANCEL");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(approve);
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        panel.Children.Add(T("TRUSTED DEVICE Â· OWNER APPROVAL", 12, "#f2cc60", bold: true));
        panel.Children.Add(detail);
        panel.Children.Add(passphrase);
        panel.Children.Add(confirmation);
        panel.Children.Add(discoverWifi);
        panel.Children.Add(wifiCandidates);
        panel.Children.Add(fingerprintApproval);
        panel.Children.Add(status);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Kickl owner credential",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = C("#0d151f"),
            Content = new Border { BorderBrush = C("#3a5570"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Child = panel }
        };
        cancel.Click += (_, _) => dialog.Close();
        approve.Click += async (_, _) =>
        {
            PairedDongleIdentity? receiver = null;
            var wifiEndpoint = wifiCandidates.SelectedItem as WifiDongleEndpoint;
            try
            {
                var entered = passphrase.Text ?? "";
                if (!File.Exists(OwnershipAuthority.SecretPath(OwnerDongleDirectory, site)))
                {
                    if (entered != confirmation.Text) throw new ArgumentException("Passphrases do not match.");
                    _ = OwnershipAuthority.Create(_session.Paths, site, OwnerDongleDirectory, entered,
                        "device emulator owner");
                }

                var owner = OwnershipAuthority.LoadSecret(OwnerDongleDirectory, site, entered);
                var pinDirectory = Path.Combine(_dataRoot, "paired-receiver-pins");
                var transportKey = wifiEndpoint is null
                    ? "local-process-emulator"
                    : "wifi-receiver:" + wifiEndpoint.ReceiverIdHint;
                var pin = OwnershipAuthority.LoadPairedDonglePin(pinDirectory, transportKey);
                IDongleTransport transport;
                string receiverId;
                if (wifiEndpoint is not null)
                {
                    transport = new WifiDongleTransport(wifiEndpoint, pin);
                    receiverId = pin?.ReceiverId ?? wifiEndpoint.ReceiverIdHint;
                }
                else
                {
                    receiver = OwnershipAuthority.LoadOrCreatePairedIdentity(
                        Path.Combine(_dataRoot, "paired-receiver"), entered);
                    transport = new LocalDongleProcessTransport(receiver);
                    receiverId = receiver.Id;
                }
                await using var ownedTransport = transport;

                var helloRequest = OwnershipAuthority.NewPairedDongleHelloRequest();
                var helloPayload = JsonSerializer.SerializeToUtf8Bytes(helloRequest);
                using var exchangeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var helloFrame = await ownedTransport.ExchangeAsync(
                    new DongleFrame(DongleFrameType.ReceiverHelloRequest, helloPayload), exchangeTimeout.Token);
                if (helloFrame.Type != DongleFrameType.ReceiverHelloResponse)
                    throw new InvalidDataException("The paired receiver returned an unexpected hello frame.");
                var hello = JsonSerializer.Deserialize<PairedDongleHello>(helloFrame.Payload,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("The paired receiver identity response is invalid.");
                var fingerprint = OwnershipAuthority.VerifyPairedDongleHello(hello, helloRequest.Challenge);
                if (pin is null)
                {
                    if (!string.Equals(shownFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        shownFingerprint = fingerprint;
                        fingerprintApproval.IsVisible = true;
                        status.Foreground = C("#f2cc60");
                        status.Text = "New receiver fingerprint: " + FormatFingerprint(fingerprint) +
                            ". Compare it with a trusted receiver label/display. The emulator can only pin its local receiver key.";
                        approve.Content = "PIN + AUTHORIZE";
                        return;
                    }
                    if (fingerprintApproval.IsChecked != true)
                    {
                        status.Text = "Confirm the displayed receiver fingerprint before pairing.";
                        return;
                    }
                    _ = OwnershipAuthority.PinPairedDongle(pinDirectory, transportKey, hello,
                        helloRequest.Challenge, userConfirmedFingerprint: true);
                }
                else if (!OwnershipAuthority.MatchesPinnedDongle(pin, hello, helloRequest.Challenge))
                {
                    throw new CryptographicException("The receiver identity changed from the pinned dongle.");
                }

                var challenge = OwnershipAuthority.NewChallenge();
                var proof = OwnershipAuthority.Prove(owner, challenge);
                var receiverPublicKey = hello.RsaPublicKeySpki;
                var packet = OwnershipAuthority.SealForPairedDongle(proof, receiverId, receiverPublicKey);
                var request = JsonSerializer.SerializeToUtf8Bytes(new ReceiverProofRequest(packet, owner.Public));
                var responseFrame = await ownedTransport.ExchangeAsync(
                    new DongleFrame(DongleFrameType.OwnershipProofRequest, request), exchangeTimeout.Token);
                var received = JsonSerializer.Deserialize<PairedDongleAck>(responseFrame.Payload,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("The paired receiver response is invalid.");
                if (!received.Accepted || received.Challenge != challenge || received.Site != site ||
                    received.ReceiverId != receiverId ||
                    !OwnershipAuthority.VerifyPairedDongleAck(receiverPublicKey, received))
                    throw new CryptographicException("The paired receiver could not verify the owner proof.");

                passphrase.Text = "";
                confirmation.Text = "";
                status.Foreground = C("#7ee787");
                status.Text = $"Proof verified for {site}. RSA wrapped the one-use AES-256-GCM key; no title was issued.";
                _status.Text = wifiEndpoint is null ? "owner proof verified in local process emulator" : "owner proof verified over the paired Wi-Fi dongle link";
            }
            catch (Exception ex)
            {
                passphrase.Text = "";
                confirmation.Text = "";
                status.Foreground = C("#ff7b72");
                status.Text = "Authorization failed: " + ex.Message;
            }
            finally
            {
                if (receiver is not null) receiver.RsaPrivateKeyPkcs8 = "";
            }
        };
        await dialog.ShowDialog(this);
    }

    private sealed record ReceiverProofRequest(PairedDonglePacket Packet, OwnershipPublic Owner);
    private static string FormatFingerprint(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4)
            .Select(i => fingerprint.Substring(i * 4, 4)));
    private ScrollViewer BuildRssFeed()
    {
        _rssItems.Spacing = 0;
        _rssItems.Margin = new Thickness(12, 0, 12, 8);
        var scroll = new ScrollViewer
        {
            Content = _rssItems,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 0)
        };
        scroll.PointerPressed += (_, _) => _rssPaused = true;
        scroll.PointerReleased += (_, _) => _rssPaused = false;
        scroll.PointerExited += (_, _) => _rssPaused = false;
        return scroll;
    }

    private void RefreshRssFeed()
    {
        _rssItems.Children.Clear();
        List<NewsItem> items;
        try
        {
            var file = new FileInfo(PublicationsFile);
            if (!file.Exists) throw new FileNotFoundException("The publications XML file is unavailable.");
            if (file.Length > 2_000_000) throw new FormatException("The publications XML file exceeds 2 MB.");
            items = NewsFeed.ParseRss(File.ReadAllText(file.FullName), "secupgrade.com");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            var unavailable = T("PUBLICATIONS UNAVAILABLE · " + ex.Message, 11, "#f2cc60");
            unavailable.TextWrapping = TextWrapping.Wrap;
            _rssItems.Children.Add(unavailable);
            return;
        }

        if (items.Count == 0)
        {
            _rssItems.Children.Add(T("NO PUBLICATIONS IN THE FEED", 11, "#6f849a"));
            return;
        }

        foreach (var news in items)
        {
            var card = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6) };
            var timestamp = news.PublishedUtc > 0
                ? DateTimeOffset.FromUnixTimeSeconds(news.PublishedUtc).ToLocalTime()
                    .ToString("HH:mm · dd MMM", CultureInfo.InvariantCulture)
                : "PUBLICATION";
            var metadata = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            metadata.Children.Add(T(news.Author.Length == 0 ? "SECUPGRADE PUBLICATIONS" : news.Author.ToUpperInvariant(),
                8.5, "#79c0ff", mono: true));
            var time = T(timestamp, 8.5, "#6f849a", mono: true);
            metadata.Children.Add(time);
            Grid.SetColumn(time, 1);
            card.Children.Add(metadata);

            var headline = T(news.Title, 12, "#c9d6e4", bold: true);
            headline.TextWrapping = TextWrapping.Wrap;
            card.Children.Add(headline);
            if (!string.IsNullOrWhiteSpace(news.Summary))
            {
                var summary = T(news.Summary, 10, "#8fa3ba");
                summary.TextWrapping = TextWrapping.Wrap;
                card.Children.Add(summary);
            }

            _rssItems.Children.Add(new Border
            {
                BorderBrush = C("#13202d"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = card
            });
        }
        _rssScroll.Offset = new Vector(0, 0);
    }

    private Border BuildTopSearchOverlay()
    {
        _topSearch.Watermark = "stylus search: tag or phrase";
        _topSearch.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _topSearch.FontSize = 12;
        _topSearch.Height = 34;
        Track(_topSearch);

        var go = SmallButton("SEARCH");
        go.Click += (_, _) => RunSearch(_topSearch.Text ?? "", top: true);

        var close = SmallButton("×");
        close.Click += (_, _) => _topSearchPanel.IsVisible = false;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        row.Children.Add(_topSearch);
        row.Children.Add(go);
        Grid.SetColumn(go, 1);
        row.Children.Add(close);
        Grid.SetColumn(close, 2);

        _topHits.Spacing = 3;

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(T("STYLUS SEARCH · public index", 9, "#7d90a8", bold: true));
        panel.Children.Add(row);
        panel.Children.Add(_topHits);

        return new Border
        {
            Background = C("#0b131c"),
            BorderBrush = C("#2b4057"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(12, 4, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = panel
        };
    }

    private Control BuildHinge()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,*") };
        var line1 = new Border { Height = 1, Background = C("#1c2a38"), VerticalAlignment = VerticalAlignment.Center };
        var dot1 = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = C("#33475d"), Margin = new Thickness(4) };
        var dot2 = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = C("#33475d"), Margin = new Thickness(4) };
        var line2 = new Border { Height = 1, Background = C("#1c2a38"), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(line1);
        grid.Children.Add(dot1);
        Grid.SetColumn(dot1, 1);
        grid.Children.Add(dot2);
        Grid.SetColumn(dot2, 2);
        grid.Children.Add(line2);
        Grid.SetColumn(line2, 4);
        return grid;
    }

    private Control BuildInnerScreen()
    {
        var screen = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = C("#0a1018")
        };

        // browser toolbar
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(12, 10, 12, 6)
        };

        _urlBox.Text = HomeUrl;
        _urlBox.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _urlBox.FontSize = 12;
        _urlBox.Height = 34;
        Track(_urlBox);
        _urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OpenAddress(_urlBox.Text ?? "");
        };
        toolbar.Children.Add(_urlBox);

        var go = SmallButton("GO");
        go.Click += (_, _) => OpenAddress(_urlBox.Text ?? "");
        toolbar.Children.Add(go);
        Grid.SetColumn(go, 1);

        _mainSearch.Width = 150;
        _mainSearch.Watermark = "search tags";
        _mainSearch.FontSize = 12;
        _mainSearch.Height = 34;
        Track(_mainSearch);
        _mainSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) RunSearch(_mainSearch.Text ?? "", top: false);
        };
        toolbar.Children.Add(_mainSearch);
        Grid.SetColumn(_mainSearch, 2);

        var lockButton = SmallButton("LOCK");
        lockButton.Click += (_, _) => LockSession();
        toolbar.Children.Add(lockButton);
        Grid.SetColumn(lockButton, 3);

        screen.Children.Add(toolbar);

        // page
        _page.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        _page.FontSize = 12.5;
        _page.Foreground = C("#c9d6e4");
        _page.TextWrapping = TextWrapping.Wrap;
        _page.Margin = new Thickness(18, 8, 18, 14);
        _page.Text = "Starting the Kickl device…";

        var pageScroll = new ScrollViewer
        {
            Content = new StackPanel
            {
                Children =
                {
                    _page,
                    _blobBar
                }
            },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _blobBar.Orientation = Orientation.Horizontal;
        _blobBar.Spacing = 6;
        _blobBar.Margin = new Thickness(18, 0, 18, 14);

        screen.Children.Add(pageScroll);
        Grid.SetRow(pageScroll, 1);

        // status strip + PIN unlock
        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(12, 0, 12, 8)
        };
        _status.Text = "booting";
        _status.FontSize = 10.5;
        _status.Foreground = C("#7d90a8");
        _status.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_status);

        _sessionLabel.Text = "LOCKED";
        _sessionLabel.FontSize = 10;
        _sessionLabel.Foreground = C("#f2cc60");
        _sessionLabel.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_sessionLabel);
        Grid.SetColumn(_sessionLabel, 1);

        _pinBox.Width = 88;
        _pinBox.Height = 30;
        _pinBox.Watermark = "user key";
        _pinBox.PasswordChar = '●';
        _pinBox.FontSize = 12;
        Track(_pinBox);
        _pinBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) UnlockSession();
        };
        bottom.Children.Add(_pinBox);
        Grid.SetColumn(_pinBox, 2);

        var unlock = SmallButton("UNLOCK");
        unlock.Click += (_, _) => UnlockSession();
        bottom.Children.Add(unlock);
        Grid.SetColumn(unlock, 3);

        screen.Children.Add(bottom);
        Grid.SetRow(bottom, 2);

        // Nickles slot, lower-left, about an inch above the bottom
        var ad = new Border
        {
            Background = C("#111a24"),
            BorderBrush = C("#2b4057"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24, 0, 0, 84),
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    T("NICKLES · slot 1", 9, "#f2cc60", bold: true),
                    T("donation rank #1 · no tracking", 9, "#7d90a8")
                }
            }
        };

        var overlay = new Grid();
        overlay.Children.Add(screen);
        overlay.Children.Add(ad);
        overlay.Children.Add(_innerSearchPanel);
        overlay.Children.Add(_confirm);

        return new Border
        {
            Background = C("#0a1018"),
            BorderBrush = C("#1f2d3d"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = overlay
        };
    }

    private Border BuildInnerSearchOverlay()
    {
        _innerHits.Spacing = 4;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(T("LIVE INDEX · PFS · results are inert until confirmed", 10, "#7d90a8", bold: true));
        panel.Children.Add(_innerHits);

        var close = SmallButton("CLOSE");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => _innerSearchPanel.IsVisible = false;
        panel.Children.Add(close);

        return new Border
        {
            Background = C("#0d151f"),
            BorderBrush = C("#2b4057"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(90, 64, 32, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
            Child = panel
        };
    }

    private Border BuildConfirmSheet()
    {
        _confirmText.FontSize = 12;
        _confirmText.Foreground = C("#c9d6e4");
        _confirmText.TextWrapping = TextWrapping.Wrap;
        _confirmText.FontFamily = new FontFamily("Consolas, Menlo, monospace");

        var open = SmallButton("OPEN");
        open.Click += (_, _) => ConfirmOpen();
        var cancel = SmallButton("CANCEL");
        cancel.Click += (_, _) => _confirm.IsVisible = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(T("DEVICE CONFIRMATION", 10, "#f2cc60", bold: true));
        panel.Children.Add(_confirmText);
        panel.Children.Add(buttons);

        return new Border
        {
            Background = C("#101a24"),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Width = 470,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = panel
        };
    }

    private Control BuildKeyboard()
    {
        var rows = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        rows.Children.Add(KeyRow("1234567890"));
        rows.Children.Add(KeyRow("qwertyuiop"));
        rows.Children.Add(KeyRow("asdfghjkl"));
        rows.Children.Add(KeyRow("zxcvbnm"));
        rows.Children.Add(KeyRow(".-_/:?@"));

        var special = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var shift = KeyButton("SHIFT", 76);
        shift.Click += (_, _) => _shift = !_shift;
        special.Children.Add(shift);

        var space = KeyButton("SPACE", 360);
        space.Click += (_, _) => Type(" ");
        special.Children.Add(space);

        var back = KeyButton("⌫", 76);
        back.Click += (_, _) => Backspace();
        special.Children.Add(back);

        var enter = KeyButton("ENTER", 96);
        enter.Click += (_, _) => Enter();
        special.Children.Add(enter);

        rows.Children.Add(special);

        return new Border
        {
            Background = C("#0b121a"),
            BorderBrush = C("#1f2d3d"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = rows
        };
    }

    private Control BuildFooter()
    {
        var left = T("demo user key 1234 · top screen is public · inner screen holds the session", 9.5, "#5f7288");
        var right = T("AES-256-GCM · PFS lookup · sealed blobs", 9.5, "#5f7288");
        right.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6, 2, 6, 0) };
        grid.Children.Add(left);
        grid.Children.Add(right);
        Grid.SetColumn(right, 1);
        return grid;
    }

    private StackPanel KeyRow(string keys)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        foreach (var c in keys)
        {
            var key = KeyButton(c.ToString(), 64);
            key.Click += (_, _) => Type(_shift ? char.ToUpperInvariant(c).ToString() : c.ToString());
            row.Children.Add(key);
        }
        return row;
    }

    private Button KeyButton(string label, double width) => new()
    {
        Content = label,
        Width = width,
        Height = 30,
        FontSize = 11,
        FontFamily = new FontFamily("Consolas, Menlo, monospace"),
        Background = C("#17222f"),
        Foreground = C("#c9d6e4"),
        BorderBrush = C("#2a3c50"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Padding = new Thickness(0)
    };

    private Button SmallButton(string label) => new()
    {
        Content = label,
        Height = 30,
        FontSize = 10,
        FontFamily = new FontFamily("Consolas, Menlo, monospace"),
        Background = C("#16222f"),
        Foreground = C("#dbe7f3"),
        BorderBrush = C("#2a3c50"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(9, 3),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private Border Pill(string text, string color, string background) => new()
    {
        Background = C(background),
        BorderBrush = C(color),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 3),
        Child = T(text, 9, color, bold: true)
    };

    private static TextBlock T(string text, double size, string color, bool mono = false, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = C(color),
        FontFamily = new FontFamily(mono ? "Consolas, Menlo, monospace" : "Segoe UI, Arial, sans-serif"),
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
    };

    private static SolidColorBrush C(string hex) => new(Color.Parse(hex));

    // ------------------------------------------------------------------ input

    private void Track(TextBox box)
    {
        box.GotFocus += (_, _) => _focus = box;
        if (_focus is null) _focus = box;
    }

    private void Type(string s)
    {
        var box = _focus;
        if (box is null) return;

        var text = box.Text ?? "";
        var index = Math.Clamp(box.CaretIndex, 0, text.Length);
        box.Text = text.Insert(index, s);
        box.CaretIndex = index + s.Length;
        box.Focus();
    }

    private void Backspace()
    {
        var box = _focus;
        if (box is null) return;

        var text = box.Text ?? "";
        var index = Math.Clamp(box.CaretIndex, 0, text.Length);
        if (index == 0) return;
        box.Text = text.Remove(index - 1, 1);
        box.CaretIndex = index - 1;
        box.Focus();
    }

    private void Enter()
    {
        if (_focus == _urlBox) OpenAddress(_urlBox.Text ?? "");
        else if (_focus == _topSearch) RunSearch(_topSearch.Text ?? "", top: true);
        else if (_focus == _mainSearch) RunSearch(_mainSearch.Text ?? "", top: false);
        else if (_focus == _pinBox) UnlockSession();
        else OpenAddress(_urlBox.Text ?? "");
    }

    // ------------------------------------------------------------------ flows

    private void BootstrapDemo()
    {
        try
        {
            _status.Text = "preparing demo profile…";
            if (!_session.HasProfile) _session.CreateProfile(DemoPin);

            var paths = _session.Paths;
            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(paths), HomeHost);
            if (record is null || string.IsNullOrEmpty(record.ContentHash))
                PublishFixture(paths);

            _session.Unlock(DemoPin);
            _pinBox.Text = DemoPin;
            OpenAddress(HomeUrl);
            _status.Text = $"demo ready · data: {_dataRoot}";
        }
        catch (Exception ex)
        {
            _status.Text = "startup failed: " + ex.Message;
            _page.Text = "Kickl could not start the demo.\n\n" + ex.Message +
                "\n\nRun the CLI demo first, or delete:\n" + _dataRoot;
        }
    }

    private static void PublishFixture(DataPaths paths)
    {
        var root = FindRepoRoot()
                   ?? throw new InvalidOperationException("Repository root (Kickl.slnx) not found.");
        var site = Path.Combine(root, "sites", HomeHost);
        if (!File.Exists(Path.Combine(site, "site.kml")))
            throw new FileNotFoundException("Fixture site.kml not found.", site);

        var master = Keystore.Unlock(paths, DemoPin);
        try
        {
            DocumentStore.Publish(paths, site, HomeHost, master);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kickl.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private void OpenAddress(string address)
    {
        try
        {
            address = address.Trim();
            if (address.Length == 0) return;
            if (!address.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
                address = "jttp://" + address.TrimStart('/');

            var page = _session.Open(address);
            _urlBox.Text = "jttp://" + page.Site + "/";
            _innerSearchPanel.IsVisible = false;
            _confirm.IsVisible = false;
            RenderPage(page);
            _status.Text = "opened · package hash verified before parse";
        }
        catch (ContentIntegrityException ex)
        {
            _status.Text = $"ATTACK REPORTED · {ex.Count} mismatch(es) on {ex.Host}";
            _page.Text = "CONTENT HASH MISMATCH\n\n" + ex.Message +
                "\n\nThe page was not parsed or rendered.";
        }
        catch (Exception ex)
        {
            _status.Text = "open failed";
            _page.Text = "OPEN FAILED\n\n" + ex.Message;
        }
    }

    private void RenderPage(PageView page)
    {
        var ownership = _session.OwnershipOf(page.Site);
        var unlocked = new HashSet<string>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var address = "jttp://" + page.Site + "/";

        _page.Text = page.Render(address, _session.IsNameVerified(page.Site), unlocked, texts,
            ownership.Owned, ownership.OwnerId);

        _blobBar.Children.Clear();
        _blobBar.Children.Add(T("sealed blobs:", 10, "#7d90a8"));
        if (_session.Document is not null)
        {
            foreach (var id in _session.Document.Blobs.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var key = id;
                var button = SmallButton("UNLOCK " + key);
                button.Click += (_, _) => UnlockBlob(key);
                _blobBar.Children.Add(button);
            }
        }

        _sessionLabel.Text = _session.IsUnlocked ? "UNLOCKED" : "LOCKED";
        _sessionLabel.Foreground = C(_session.IsUnlocked ? "#7ee787" : "#f2cc60");
        _pinBox.Text = _session.IsUnlocked ? DemoPin : _pinBox.Text;
    }

    private void UnlockBlob(string id)
    {
        try
        {
            if (!_session.IsUnlocked) _session.Unlock(_pinBox.Text ?? "");

            var text = _session.UnlockBlob(id);
            _page.Text += "\n\n── blob " + id + " (opened) ──\n" + text;
            _status.Text = $"blob {id} opened · buffer zeroised after decode";
        }
        catch (Exception ex)
        {
            _status.Text = "blob failed";
            _page.Text += "\n\n[blob " + id + " failed: " + ex.Message + "]";
        }
    }

    private void UnlockSession()
    {
        try
        {
            _session.Unlock(_pinBox.Text ?? "");
            _sessionLabel.Text = "UNLOCKED";
            _sessionLabel.Foreground = C("#7ee787");
            _status.Text = "session unlocked · keys are in RAM only";
            if (_session.Current is not null)
            {
                var page = _session.Open(_session.Current.ToString());
                RenderPage(page);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "unlock failed: " + ex.Message;
        }
    }

    private void LockSession()
    {
        _session.Lock();
        _sessionLabel.Text = "LOCKED";
        _sessionLabel.Foreground = C("#f2cc60");
        _status.Text = "locked · master, session and doc keys zeroised";
        _blobBar.Children.Clear();
    }

    private void RunSearch(string query, bool top)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        try
        {
            var result = _session.Search(query.Trim());
            if (top)
            {
                _topHits.Children.Clear();
                _topSearchPanel.IsVisible = true;
                if (result.Results.Count == 0)
                    _topHits.Children.Add(T("no active site carries that tag", 10, "#ff7b72"));
                foreach (var hit in result.Results)
                    _topHits.Children.Add(T($"jttp://{hit.Host}/  {hit.Title}", 10, "#79c0ff", mono: true));
            }
            else
            {
                _hits.Clear();
                _hits.AddRange(result.Results);
                _innerHits.Children.Clear();
                _innerSearchPanel.IsVisible = true;
                if (result.Results.Count == 0)
                    _innerHits.Children.Add(T("no active site carries that tag", 11, "#ff7b72"));

                foreach (var hit in result.Results)
                {
                    var captured = hit;
                    var b = SmallButton($"{hit.Host} · {hit.Title}");
                    b.HorizontalAlignment = HorizontalAlignment.Stretch;
                    b.HorizontalContentAlignment = HorizontalAlignment.Left;
                    b.Click += (_, _) => PromptOpen(captured);
                    _innerHits.Children.Add(b);
                }
                _status.Text = $"search over PFS · {result.Results.Count} hit(s) · query was encrypted on the wire";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "search failed: " + ex.Message;
        }
    }

    private void PromptOpen(SiteSummary hit)
    {
        _pending = hit;
        _confirmText.Text =
            $"jttp://{hit.Host}/\n{hit.Title}\n{hit.Headline}\n\n" +
            $"tags: {string.Join(", ", hit.Tags)}\n" +
            $"content: {Short(hit.ContentHash)}\n" +
            $"owner: {(string.IsNullOrEmpty(hit.BadgeId) ? "no paid title" : hit.BadgeId)}\n\n" +
            "Open this site on the inner screen?";
        _confirm.IsVisible = true;
    }

    private void ConfirmOpen()
    {
        _confirm.IsVisible = false;
        if (_pending is null) return;
        OpenAddress("jttp://" + _pending.Host + "/");
        _pending = null;
    }

    private void ToggleTopSearch()
    {
        _topSearchPanel.IsVisible = !_topSearchPanel.IsVisible;
        if (_topSearchPanel.IsVisible) _topSearch.Focus();
    }

    private void StartRssTicker()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            if (_rssPaused) return;
            var extent = _rssScroll.Extent.Height;
            var viewport = _rssScroll.Viewport.Height;
            if (extent <= viewport) return;
            var next = _rssScroll.Offset.Y + 0.35;
            if (next >= extent - viewport) next = 0;
            _rssScroll.Offset = new Vector(0, next);
        };
        timer.Start();
    }

    private void StartRssRefresh()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        timer.Tick += (_, _) => RefreshRssFeed();
        timer.Start();
    }

    private void StartClock()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
            _clock.Text = DateTimeOffset.Now.ToString("HH:mm:ss · ddd d MMM", CultureInfo.InvariantCulture);
        _clock.Text = DateTimeOffset.Now.ToString("HH:mm:ss · ddd d MMM", CultureInfo.InvariantCulture);
        timer.Start();
    }

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "—" : (value.Length <= 20 ? value : value[..20] + "…");

    protected override void OnClosed(EventArgs e)
    {
        _session.Dispose();
        base.OnClosed(e);
    }
}
