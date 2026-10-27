using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Kickl.Core.Ads;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.FreeChat;
using Kickl.Core.Mail;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.News;
using Kickl.Core.Ownership;
using Kickl.Core.Registry;
using Kickl.Core.Session;
using Kickl.Core.Util;

namespace Kickl.Browser;

/// <summary>
/// Screen one is the browser. Screen two is the saved-news stream fed by every
/// registry site addition plus opt-in public RSS. Everything is inert until confirmed.
/// </summary>
public sealed partial class BrowserWindow : Window
{
    private const string DemoPin = "1234";
    private const double DesignWidth = 1460;
    private const double DesignHeight = 920;
    private const string HomeHost = "pinwheelexplained.com";
    private const string HomeUrl = "jttp://pinwheelexplained.com/";
    private const string AdNetworkUrl = "jttp://secupgrade.com/adnetworking";
    private const string AdPublisherUrl = "jttp://secupgrade.com/adnetworking/publisher";
    private const string SecupgradeRssFile = @"C:\xampp\htdocs\rssfeed.xml";
    private static readonly string[] DemoHosts =
        [HomeHost, "mesh-lab.custom", "stego-lab.custom", "nickles-testnet.custom", "svg-lab.custom", "sealed-lab.custom", "game-lab.custom"];
    private const string Accent = "#58a6ff";
    private const string Green = "#7ee787";
    private const string Amber = "#f2cc60";
    private const string DefaultHomeHint =
        "PFS tag search · the query and results stay inside one AES-256-GCM session. Nothing opens until you confirm.";

    private static readonly HomeGame[] HomeGames =
    [
        new("tic-tac-toe", "TIC-TAC-TOE", "three in a row", "you", "PLAYABLE · native demo", "#78c8c8", true),
        new("memory", "MEMORY", "match the pairs", "arcade", "PLAYABLE · OFFLINE", "#ff7a64", true),
        new("snake", "SNAKE", "longer every bite", "arcade", "QUEUED · wasm-1", "#f2cc60", false),
        new("2048", "2048", "merge the tiles", "arcade", "QUEUED · wasm-1", "#8fa8bd", false)
    ];
    private const string Red = "#ff7b72";

    private static readonly string Bg = "#070b10";
    private static readonly string Panel = "#0c1219";
    private static readonly string Panel2 = "#101a24";
    private static readonly string Line = "#203044";
    private static readonly string Text = "#dbe7f3";
    private static readonly string Muted = "#8194aa";

    private BrowserSession _session = null!;
    private SavedItemsStore _savedItems = null!;
    private string _selectedProfileId = "local";
    private bool _updatingProfilePicker;
    private bool _profileCreateMode;
    private readonly string _dataRoot;
    private readonly string? _screenshotPath;
    private readonly bool _exitAfterScreenshot;
    private readonly bool _demoMode;
    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }.Limit(HttpLimits.Feed);
    private readonly IWebSurfaceEngine _sealedSurface = new AvaloniaSealedSurfaceEngine();

    private readonly List<BrowserTab> _tabs = [];
    private readonly List<NewsItem> _siteNews = [];
    private readonly List<NewsItem> _rssNews = [];
    private readonly HashSet<string> _seenSiteIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _newIds = new(StringComparer.Ordinal);
    private readonly List<FeedSource> _sources = [];

    private int _active;
    private int _focusCycleIndex;
    private int _omniboxActiveIndex;
    private int _profileEpoch;
    private bool _newsModeSaved;
    private bool _rssBusy;
    private bool _homeAdCollapsed;
    private long _homeAdBucket = -1;
    private string _newsSignature = "";
    private string? _pendingExternal;
    private SiteSummary? _pendingSite;
    private string? _lastClosedAddress;

    private readonly TextBox _address = new();
    private readonly TextBox _searchBox = new();
    private readonly TextBox _pinBox = new();
    private readonly TextBox _confirmPinBox = new();
    private readonly TextBox _profileNameBox = new();
    private readonly ComboBox _profilePicker = new();
    private readonly TextBlock _gateTitle = new();
    private readonly TextBlock _gateMessage = new();
    private readonly TextBox _rssUrl = new();
    private readonly TextBlock _titleText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _searchHint = new();
    private readonly TextBlock _confirmTitle = new();
    private readonly TextBlock _confirmText = new();
    private readonly TextBlock _newsStatus = new();
    private readonly TextBlock _newsCount = new();
    private readonly TextBlock _newsHeaderDetail = new();
    private readonly TextBlock _clock = new();
    private readonly StackPanel _tabStrip = new();
    private readonly StackPanel _omniboxSuggestions = new() { Spacing = 2 };
    private readonly StackPanel _searchResults = new();
    private TextBox? _homeSearchBox;
    private readonly StackPanel _newsFeed = new();
    private readonly ScrollViewer _newsScroll = new();
    private readonly Border _pageHost = new();
    private readonly Grid _homeAdOverlay = new() { IsVisible = false };
    private readonly Border _sessionPill;
    private readonly ScrollViewer _contentScroll;
    private readonly Grid _searchOverlay;
    private readonly Grid _confirmOverlay;
    private readonly Grid _shieldOverlay;
    private readonly StackPanel _shieldPins = new() { Spacing = 8 };
    private readonly TextBlock _shieldHead = new();
    private readonly TextBlock _shieldDetail = new();
    private readonly Grid _lockOverlay;
    private readonly Grid _chatOverlay;
    private readonly Grid _mailOverlay;
    private readonly StackPanel _mailAuthPanel = new() { Spacing = 10 };
    private readonly Grid _mailMainPanel = new();
    private readonly TextBlock _mailAuthTitle = new();
    private readonly TextBlock _mailAuthHint = new();
    private readonly TextBox _mailPassword = new();
    private readonly TextBox _mailPasswordConfirm = new();
    private readonly TextBox _mailHandle = new();
    private readonly TextBlock _mailError = new();
    private Button _mailAuthButton = null!;
    private readonly TextBlock _mailIdentity = new();
    private readonly TextBlock _mailStatus = new();
    private readonly TextBox _mailEndpointBox = new();
    private readonly StackPanel _mailRows = new() { Spacing = 4 };
    private readonly ScrollViewer _mailRowsScroll = new();
    private readonly StackPanel _mailReader = new() { Spacing = 8 };
    private readonly Grid _mailComposePanel = new();
    private readonly TextBox _mailTo = new();
    private readonly TextBox _mailSubject = new();
    private readonly TextBox _mailBody = new();
    private MailService? _mail;
    private DispatcherTimer? _mailTimer;
    private bool _mailCreating = true;
    private bool _mailBusy;
    private string? _mailOpenId;
    private string? _mailOpenSubject;
    private List<string> _mailPages = [];
    private int _mailPage;
    private readonly StackPanel _chatAuthPanel = new() { Spacing = 10 };
    private readonly Grid _chatRoomPanel = new();
    private readonly StackPanel _chatMessages = new() { Spacing = 6 };
    private readonly ScrollViewer _chatMessagesScroll = new();
    private readonly TextBox _chatUsername = new();
    private readonly TextBox _chatPin = new();
    private readonly TextBlock _chatError = new();
    private readonly TextBox _chatInput = new();
    private readonly TextBlock _chatIdentity = new();
    private readonly TextBlock _chatRoomStatus = new();
    private readonly StackPanel _chatMembers = new() { Spacing = 4 };
    private readonly StackPanel _chatBlockedList = new() { Spacing = 4 };
    private FreeChatClient? _chatClient;
    private Dictionary<string, string> _chatKeyPins = [];
    private byte[]? _chatSigningKey;

    private byte[] LoadOrMakeChatSigningKey()
    {
        var made = FreeChatClient.NewSigningKey();
        SaveChatPrefs(made);
        return made;
    }

    /// <summary>A new chat client remembers the member keys this profile has already seen, and tells us when it learns one.</summary>
    private FreeChatClient NewChatClient()
    {
        var client = NewChatClient();
        client.LoadPins(_chatKeyPins);
        client.UseSigningKey(_chatSigningKey ??= LoadOrMakeChatSigningKey());
        client.PinsChanged += () =>
        {
            _chatKeyPins = client.KeyPins.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
            SaveChatPrefs();
        };
        return client;
    }
    private DispatcherTimer? _chatTimer;
    private bool _chatBusy;
    private readonly HashSet<string> _chatBlocked = new(StringComparer.OrdinalIgnoreCase);
    private string? _chatSavedUser;
    private string? _chatSavedPin;
    private readonly Button _confirmOpen;
    private readonly Button _streamButton;
    private readonly Button _savedButton;
    private Button _unlockProfileButton = null!;
    private Button _createProfileButton = null!;
    private Button _cancelProfileCreateButton = null!;
    private TextBox? _editorFocus;

    private BrowserTab? Active => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    public BrowserWindow(string dataRoot, string startUrl, string? screenshotPath = null,
        bool exitAfterScreenshot = false, bool demoMode = false)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _screenshotPath = screenshotPath;
        _exitAfterScreenshot = exitAfterScreenshot;
        _demoMode = demoMode;

        Title = "Kickl Browser — screen 1";
        Width = DesignWidth;
        Height = DesignHeight;
        MinWidth = 800;
        MinHeight = 500;
        Background = C(Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        IReadOnlyList<string> existingProfiles = _demoMode ? Array.Empty<string>() : ProfileCatalog.List(_dataRoot);
        _selectedProfileId = existingProfiles.FirstOrDefault() ?? "local";
        _session = new BrowserSession(_dataRoot, _selectedProfileId);
        _mail = new MailService(_session.Paths);
        _savedItems = new SavedItemsStore(_dataRoot, _session);
        _sessionPill = Pill("LOCKED", Amber, "#241f0d");
        _confirmOpen = Btn("OPEN", ConfirmPending, accent: true);
        _streamButton = Btn("STREAM", () => SetNewsMode(false));
        _savedButton = Btn("SAVED", () => SetNewsMode(true));

        _contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _pageHost.Focusable = true;
        _newsFeed.Focusable = true;
        _searchOverlay = BuildSearchOverlay();
        _confirmOverlay = BuildConfirmOverlay();
        _shieldOverlay = BuildShieldOverlay();
        _claimOverlay = BuildClaimOverlay();
        _lockOverlay = BuildLockOverlay();
        _chatOverlay = BuildChatOverlay();
        _mailOverlay = BuildMailOverlay();

        // The shell is laid out once at a fixed design size, then scaled uniformly to whatever
        // screen is available. That keeps the 36 px rail/geometry intact on a TV or small laptop
        // instead of letting the window run off the display.
        var shell = BuildShell();
        shell.Width = DesignWidth;
        shell.Height = DesignHeight;
        Content = new Viewbox
        {
            Child = shell,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Bootstrap();
        if (_session.IsUnlocked)
        {
        }
        RefreshRegistryNews();
        StartTimers();
        if (string.IsNullOrWhiteSpace(startUrl)) NewTab();
        else NewTab(startUrl);

        if (!string.IsNullOrWhiteSpace(App.StartSearch) && Active is { Page: null, Game: null } home)
            RunHomeSearch(home, App.StartSearch!);

        _lockOverlay.IsVisible = !_session.IsUnlocked;
        RefreshProfileGate();

        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            FitToScreen();
            if (_lockOverlay.IsVisible)
            {
                if (_profileCreateMode) _profileNameBox.Focus();
                else _pinBox.Focus();
            }
            ScheduleScreenshot();
        };
    }

    /// <summary>Clamp the window to the working area and let the Viewbox scale the fixed shell.</summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var area = screen.WorkingArea;
        var availableWidth = area.Width / scaling;
        var availableHeight = area.Height / scaling;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        Width = Math.Min(DesignWidth, Math.Max(MinWidth, availableWidth * 0.96));
        Height = Math.Min(DesignHeight, Math.Max(MinHeight, availableHeight * 0.92));
        Position = new PixelPoint(
            area.X + Math.Max(0, (int) ((area.Width - Width * scaling) / 2)),
            area.Y + Math.Max(0, (int) ((area.Height - Height * scaling) / 2)));
    }

    // ------------------------------------------------------------------ shell

    private Control BuildShell()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("52,*,26") };
        var header = BuildHeader();
        root.Children.Add(header);
        root.Children.Add(BuildMain());
        Grid.SetRow(root.Children[1], 1);
        var status = BuildStatusBar();
        root.Children.Add(status);
        Grid.SetRow(status, 2);
        return root;
    }

    private Control BuildHeader()
    {
        var brand = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(T("KICKL", 20, Text, bold: true));
        brand.Children.Add(T("SCREEN 1 · SEALED BROWSER · KML", 8.5, Muted, bold: true));

        _titleText.Text = "starting…";
        _titleText.FontSize = 12.5;
        _titleText.Foreground = C(Muted);
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        _titleText.Margin = new Thickness(18, 0);
        _titleText.MaxWidth = 640;

        var session = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _clock.FontFamily = Mono;
        _clock.FontSize = 10.5;
        _clock.Foreground = C(Muted);
        _clock.VerticalAlignment = VerticalAlignment.Center;
        _clock.Text = DateTimeOffset.Now.ToString("HH:mm:ss · ddd d MMM", CultureInfo.InvariantCulture);

        var shieldButton = Btn("SHIELD", OpenShield);
        shieldButton.Height = 28;
        AutomationProperties.SetName(shieldButton, "Registry trust and owner keys");
        var lockButton = Btn("LOCK", LockSession);
        lockButton.Height = 28;
        var chatButton = Btn("CHAT", OpenChat);
        chatButton.Height = 28;
        AutomationProperties.SetName(chatButton, "Pinwheel encrypted chat room");
        var mailButton = Btn("MAIL", OpenMail);
        mailButton.Height = 28;
        AutomationProperties.SetName(mailButton, "KicklMail direct sealed mail");
        session.Children.Add(_sessionPill);
        session.Children.Add(mailButton);
        session.Children.Add(chatButton);
        session.Children.Add(shieldButton);
        session.Children.Add(lockButton);
        session.Children.Add(_clock);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 0) };
        grid.Children.Add(brand);
        grid.Children.Add(_titleText);
        Grid.SetColumn(_titleText, 1);
        grid.Children.Add(session);
        Grid.SetColumn(session, 2);
        return new Border
        {
            Background = C(Panel),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private Control BuildMain()
    {
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,400") };
        var browser = BuildBrowserPane();
        main.Children.Add(browser);
        var divider = new Border { Background = C(Line) };
        main.Children.Add(divider);
        Grid.SetColumn(divider, 1);
        var news = BuildNewsPane();
        main.Children.Add(news);
        Grid.SetColumn(news, 2);
        return main;
    }

    private Control BuildBrowserPane()
    {
        var pane = new Grid { RowDefinitions = new RowDefinitions("34,46,*,Auto") };

        var tabsBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = C(Panel)
        };
        _tabStrip.Orientation = Orientation.Horizontal;
        _tabStrip.Spacing = 2;
        _tabStrip.Margin = new Thickness(8, 3, 8, 0);
        var tabsScroll = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        tabsBar.Children.Add(tabsScroll);
        var plus = FlatButton("+", NewTab);
        plus.Width = 30;
        plus.Height = 26;
        plus.Margin = new Thickness(4, 3, 8, 0);
        ToolTip.SetTip(plus, "New tab (Ctrl+T)");
        AutomationProperties.SetName(plus, "New tab");
        tabsBar.Children.Add(plus);
        Grid.SetColumn(plus, 1);
        pane.Children.Add(tabsBar);

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto,Auto"),
            ColumnSpacing = 5,
            Margin = new Thickness(10, 7, 10, 7)
        };
        var back = FlatButton("‹", Back);
        back.Width = 30;
        var forward = FlatButton("›", Forward);
        forward.Width = 30;
        var reload = FlatButton("⟳", Reload);
        reload.Width = 30;
        var home = FlatButton("⌂", NavigateHome);
        home.Width = 30;
        toolbar.Children.Add(back);
        toolbar.Children.Add(forward);
        Grid.SetColumn(forward, 1);
        toolbar.Children.Add(reload);
        Grid.SetColumn(reload, 2);
        toolbar.Children.Add(home);
        Grid.SetColumn(home, 3);

        StyleInput(_address, "jttp://name.tld/path or search");
        AutomationProperties.SetName(_address, "Address or search");
        AutomationProperties.SetHelpText(_address, "Suggestions come only from this device's local registry. Press Enter to accept the selected suggestion.");
        _address.FontFamily = Mono;
        _address.FontSize = 12;
        _address.Height = 32;
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (_omniboxSuggestions.IsVisible && _omniboxSuggestions.Children.Count > 0)
                    AcceptOmniboxSuggestion(_omniboxActiveIndex);
                else
                    NavigateOmnibox();
                e.Handled = true;
            }
            else if ((e.Key is Key.Down or Key.Up) && _omniboxSuggestions.IsVisible && _omniboxSuggestions.Children.Count > 0)
            {
                var delta = e.Key == Key.Down ? 1 : -1;
                _omniboxActiveIndex = (_omniboxActiveIndex + delta + _omniboxSuggestions.Children.Count)
                                      % _omniboxSuggestions.Children.Count;
                for (var i = 0; i < _omniboxSuggestions.Children.Count; i++)
                    if (_omniboxSuggestions.Children[i] is Button suggestionButton)
                        suggestionButton.Background = C(i == _omniboxActiveIndex ? "#203044" : "#101a24");
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _omniboxSuggestions.IsVisible = false;
            }
        };
        _address.TextChanged += (_, _) => RefreshOmniboxSuggestions();
        toolbar.Children.Add(_address);
        Grid.SetColumn(_address, 4);

        var go = Btn("GO", NavigateOmnibox);
        go.Height = 32;
        toolbar.Children.Add(go);
        Grid.SetColumn(go, 5);

        var search = Btn("SEARCH", ToggleSearch);
        search.Height = 32;
        toolbar.Children.Add(search);
        Grid.SetColumn(search, 6);

        var newsButton = Btn("NEWS", () => _rssUrl.Focus());
        newsButton.Height = 32;
        toolbar.Children.Add(newsButton);
        Grid.SetColumn(newsButton, 7);

        var toolbarOverlay = new Grid();
        toolbarOverlay.Children.Add(toolbar);
        _omniboxSuggestions.IsVisible = false;
        var suggestionCard = new Border
        {
            Background = C(Panel2),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6),
            Width = 430,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(200, 42, 0, 0),
            Child = _omniboxSuggestions
        };
        toolbarOverlay.Children.Add(suggestionCard);
        pane.Children.Add(toolbarOverlay);
        Grid.SetRow(toolbarOverlay, 1);

        var surface = new Grid();
        _pageHost.Background = C(Bg);
        _contentScroll.Content = _pageHost;
        surface.Children.Add(_contentScroll);
        surface.Children.Add(_searchOverlay);
        surface.Children.Add(_confirmOverlay);
        surface.Children.Add(_shieldOverlay);
        surface.Children.Add(_claimOverlay);
        surface.Children.Add(_chatOverlay);
        surface.Children.Add(_mailOverlay);
        surface.Children.Add(_lockOverlay);
        pane.Children.Add(surface);
        Grid.SetRow(surface, 2);

        // The home ad is a real footer row, not an overlay: it can never cover page content.
        _homeAdOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
        _homeAdOverlay.VerticalAlignment = VerticalAlignment.Stretch;
        _homeAdOverlay.Margin = new Thickness(12, 6, 12, 8);
        _homeAdOverlay.ClipToBounds = true;
        pane.Children.Add(_homeAdOverlay);
        Grid.SetRow(_homeAdOverlay, 3);
        return pane;
    }

    private Control BuildNewsPane()
    {
        var pane = new Grid
        {
            RowDefinitions = new RowDefinitions("54,34,42,*,28"),
            Background = C(Panel)
        };

        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(14, 9, 14, 4)
        };
        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = C(Green),
            Margin = new Thickness(0, 4, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        var titles = new StackPanel { Spacing = 0 };
        titles.Children.Add(T("SCREEN 2 · PUBLICATIONS", 10, Accent, bold: true));
        titles.Children.Add(T("site additions + opt-in public RSS", 8.5, Muted));
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(dot);
        titleRow.Children.Add(titles);
        head.Children.Add(titleRow);

        _newsHeaderDetail.Text = "newest first · no ranking";
        _newsHeaderDetail.FontSize = 8.5;
        _newsHeaderDetail.Foreground = C(Muted);
        _newsHeaderDetail.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_newsHeaderDetail);
        Grid.SetColumn(_newsHeaderDetail, 1);
        _newsHeaderDetail.Margin = new Thickness(10, 0);

        _newsCount.FontSize = 9;
        _newsCount.Foreground = C(Green);
        _newsCount.VerticalAlignment = VerticalAlignment.Center;
        _newsCount.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(_newsCount);
        Grid.SetColumn(_newsCount, 2);
        pane.Children.Add(head);

        var modes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(14, 2, 14, 4)
        };
        StyleModeButton(_streamButton, active: true);
        StyleModeButton(_savedButton, active: false);
        modes.Children.Add(_streamButton);
        modes.Children.Add(_savedButton);
        pane.Children.Add(modes);
        Grid.SetRow(modes, 1);

        var add = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 5,
            Margin = new Thickness(14, 2, 14, 4)
        };
        StyleInput(_rssUrl, "add RSS/Atom URL (https://…)");
        _rssUrl.FontSize = 10;
        _rssUrl.Height = 28;
        _rssUrl.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) AddRssSource();
        };
        add.Children.Add(_rssUrl);
        var addButton = Btn("+", AddRssSource);
        addButton.Height = 28;
        addButton.Width = 32;
        add.Children.Add(addButton);
        Grid.SetColumn(addButton, 1);
        pane.Children.Add(add);
        Grid.SetRow(add, 2);

        _newsFeed.Spacing = 0;
        _newsScroll.Content = _newsFeed;
        _newsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _newsScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var scroll = _newsScroll;
        pane.Children.Add(scroll);
        Grid.SetRow(scroll, 3);

        _newsStatus.Text = "reading the registry · RSS is opt-in";
        _newsStatus.FontSize = 8.5;
        _newsStatus.Foreground = C(Muted);
        _newsStatus.Margin = new Thickness(14, 0, 14, 6);
        _newsStatus.VerticalAlignment = VerticalAlignment.Center;
        pane.Children.Add(_newsStatus);
        Grid.SetRow(_newsStatus, 4);

        return pane;
    }

    private Control BuildStatusBar()
    {
        _statusText.Text = "booting…";
        _statusText.FontSize = 9.5;
        _statusText.Foreground = C(Muted);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.Margin = new Thickness(14, 0);

        var right = T("PFS lookup · AES-256-GCM · KML only · no scripts", 9, Muted);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.Margin = new Thickness(0, 0, 14, 0);
        right.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_statusText);
        grid.Children.Add(right);
        Grid.SetColumn(right, 1);
        return new Border
        {
            Background = C(Panel),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid
        };
    }

    // ------------------------------------------------------------------ overlays

    private Grid BuildSearchOverlay()
    {
        StyleInput(_searchBox, "search the live index by tag or phrase");
        _searchBox.FontSize = 13;
        _searchBox.Height = 36;
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) RunSearch(_searchBox.Text ?? "");
        };

        var go = Btn("SEARCH", () => RunSearch(_searchBox.Text ?? ""), accent: true);
        go.Height = 36;
        var close = Btn("CLOSE", () => _searchOverlay.IsVisible = false);
        close.Height = 36;

        var inputRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 6
        };
        inputRow.Children.Add(_searchBox);
        inputRow.Children.Add(go);
        Grid.SetColumn(go, 1);
        inputRow.Children.Add(close);
        Grid.SetColumn(close, 2);

        _searchHint.Text = "The query is sealed in a fresh PFS lookup. Hits do not open until you confirm.";
        _searchHint.FontSize = 9.5;
        _searchHint.Foreground = C(Muted);
        _searchHint.TextWrapping = TextWrapping.Wrap;

        _searchResults.Spacing = 7;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(T("LIVE INDEX · REQUIRED TAGS", 10, Accent, bold: true));
        panel.Children.Add(inputRow);
        panel.Children.Add(_searchHint);
        panel.Children.Add(new Border { Height = 1, Background = C(Line) });
        panel.Children.Add(_searchResults);

        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(36, 24, 36, 24),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = panel
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#99000000") });
        grid.Children.Add(sheet);
        return grid;
    }

    private Grid BuildShieldOverlay()
    {
        _shieldHead.Text = "REGISTRY TRUST";
        _shieldHead.FontSize = 12;
        _shieldHead.Foreground = C(Accent);
        _shieldHead.FontWeight = FontWeight.Bold;

        _shieldDetail.FontSize = 10;
        _shieldDetail.Foreground = C(Muted);
        _shieldDetail.TextWrapping = TextWrapping.Wrap;

        var close = Btn("CLOSE", () => _shieldOverlay.IsVisible = false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(close);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_shieldHead);
        panel.Children.Add(_shieldDetail);
        panel.Children.Add(new Border { Height = 1, Background = C(Line) });
        panel.Children.Add(_shieldPins);
        panel.Children.Add(buttons);

        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Width = 540,
            MaxHeight = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = panel }
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#aa000000") });
        grid.Children.Add(sheet);
        return grid;
    }

    private void OpenShield()
    {
        RefreshShield();
        _shieldOverlay.IsVisible = true;
    }

    private void RefreshShield()
    {
        var state = RegistryStateStore.Load(_session.Paths);
        _shieldPins.Children.Clear();
        _shieldHead.Text = state.Version == 0
            ? "REGISTRY TRUST · NOT SYNCED"
            : $"REGISTRY TRUST · v{state.Version}";
        _shieldDetail.Text = state.Version == 0
            ? "No signed registry snapshot has been accepted on this device. Snapshots are refused unless the Ed25519 and ML-DSA-65 signatures both verify."
            : $"root {ShortFingerprint(state.RootHash)} · {state.Pins.Count} pinned owner key(s). A changed owner key stays pending until you approve it.";

        if (state.Pins.Count == 0)
        {
            _shieldPins.Children.Add(T("No owner keys pinned yet.", 10, Muted));
            return;
        }

        foreach (var pin in state.Pins.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            _shieldPins.Children.Add(ShieldPinRow(pin));
    }

    private Control ShieldPinRow(RegistryPin pin)
    {
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(T(pin.Name, 11.5, Text, bold: true));
        left.Children.Add(T($"owner {ShortFingerprint(pin.OwnerFingerprint)} · pinned {PinnedAt(pin.PinnedUtc)}",
            9, Muted, mono: true));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(left);

        if (pin.PendingFingerprint is not null)
        {
            left.Children.Add(T($"CHANGED · new key {ShortFingerprint(pin.PendingFingerprint)} · awaiting approval",
                9, Amber, mono: true));
            var approve = Btn("APPROVE", () => ApproveShieldPin(pin.Name), accent: true);
            approve.Height = 26;
            approve.Margin = new Thickness(10, 0, 0, 0);
            AutomationProperties.SetName(approve, "Approve changed owner key for " + pin.Name);
            row.Children.Add(approve);
            Grid.SetColumn(approve, 1);
        }

        return new Border
        {
            Background = C(Panel2),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = row
        };
    }

    private void ApproveShieldPin(string name)
    {
        var state = RegistryStateStore.Load(_session.Paths);
        if (!RegistryMirror.ApproveChange(state, name))
        {
            SetStatus("no pending owner change for " + name);
            return;
        }

        RegistryStateStore.Save(_session.Paths, state);
        SetStatus("owner key approved · " + name);
        RefreshShield();
    }

    private static string ShortFingerprint(string? value) =>
        string.IsNullOrEmpty(value) ? "—" : value[..Math.Min(12, value.Length)] + "…";

    private static string PinnedAt(long unix) => unix <= 0
        ? "unknown"
        : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Grid BuildConfirmOverlay()
    {
        _confirmTitle.Text = "DEVICE CONFIRMATION";
        _confirmTitle.FontSize = 10;
        _confirmTitle.Foreground = C(Amber);
        _confirmTitle.FontWeight = FontWeight.Bold;

        _confirmText.FontSize = 11.5;
        _confirmText.Foreground = C(Text);
        _confirmText.FontFamily = Mono;
        _confirmText.TextWrapping = TextWrapping.Wrap;

        var cancel = Btn("CANCEL", () => _confirmOverlay.IsVisible = false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_confirmOpen);

        var panel = new StackPanel { Spacing = 11 };
        panel.Children.Add(_confirmTitle);
        panel.Children.Add(_confirmText);
        panel.Children.Add(buttons);

        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Width = 540,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#aa000000") });
        grid.Children.Add(sheet);
        return grid;
    }

    private Grid BuildLockOverlay()
    {
        StyleInput(_pinBox, "user key");
        _pinBox.PasswordChar = '\u25CF';
        _pinBox.Width = 190;
        _pinBox.Height = 34;
        _pinBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (_profileCreateMode) _confirmPinBox.Focus();
                else UnlockSession();
            }
        };

        _unlockProfileButton = Btn("SIGN IN", UnlockSession, accent: true);
        _unlockProfileButton.Height = 34;
        _profileNameBox.Width = 250;
        _profileNameBox.Height = 34;
        StyleInput(_profileNameBox, "new profile name");
        _profileNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _pinBox.Focus();
        };
        _confirmPinBox.Width = 250;
        _confirmPinBox.Height = 34;
        _confirmPinBox.PasswordChar = '\u25CF';
        StyleInput(_confirmPinBox, "confirm user key (8+ characters)");
        _confirmPinBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CreateProfile();
        };
        _profilePicker.Width = 250;
        _profilePicker.Height = 34;
        _profilePicker.SelectionChanged += (_, _) =>
        {
            if (_updatingProfilePicker || _profilePicker.SelectedItem is not string displayName) return;
            var profileId = ProfileCatalog.CreateId(displayName);
            if (string.Equals(profileId, _selectedProfileId, StringComparison.Ordinal)) return;
            SwitchProfile(profileId);
        };

        _createProfileButton = Btn("CREATE PROFILE", () =>
        {
            if (_profileCreateMode) CreateProfile();
            else BeginProfileCreation();
        }, accent: true);
        _createProfileButton.Height = 34;
        _cancelProfileCreateButton = Btn("CANCEL", CancelProfileCreation);
        _cancelProfileCreateButton.Height = 34;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(_pinBox);
        row.Children.Add(_unlockProfileButton);

        var createActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        createActions.Children.Add(_createProfileButton);
        createActions.Children.Add(_cancelProfileCreateButton);

        var panel = new StackPanel { Spacing = 12 };
        _gateTitle.Text = "SIGN IN";
        _gateTitle.FontSize = 16;
        _gateTitle.Foreground = C(Amber);
        _gateTitle.FontWeight = FontWeight.Bold;
        _gateMessage.TextWrapping = TextWrapping.Wrap;
        _gateMessage.FontSize = 11;
        _gateMessage.Foreground = C(Muted);
        panel.Children.Add(_gateTitle);
        panel.Children.Add(_gateMessage);
        panel.Children.Add(_profilePicker);
        panel.Children.Add(_profileNameBox);
        panel.Children.Add(_confirmPinBox);
        panel.Children.Add(row);
        panel.Children.Add(createActions);

        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Width = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#cc05070a") });
        grid.Children.Add(sheet);
        return grid;
    }

    private Grid BuildChatOverlay()
    {
        StyleInput(_chatUsername, "freechat username");
        _chatUsername.Height = 36;
        StyleInput(_chatPin, "freechat PIN");
        _chatPin.PasswordChar = '\u25CF';
        _chatPin.Height = 36;
        _chatPin.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ChatSignIn();
        };
        _chatError.FontSize = 10.5;
        _chatError.Foreground = C(Red);
        _chatError.TextWrapping = TextWrapping.Wrap;

        var signIn = Btn("SIGN IN", ChatSignIn, accent: true);
        var register = Btn("CREATE ACCOUNT", ChatRegister);
        var guest = Btn("GUEST", ChatGuest);
        var authButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        authButtons.Children.Add(signIn);
        authButtons.Children.Add(register);
        authButtons.Children.Add(guest);

        _chatAuthPanel.Children.Clear();
        _chatAuthPanel.Width = 430;
        _chatAuthPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _chatAuthPanel.VerticalAlignment = VerticalAlignment.Center;
        _chatAuthPanel.Children.Add(T("PINWHEEL FREECHAT", 16, Text, bold: true));
        _chatAuthPanel.Children.Add(T(
            "Same encrypted room as secupgrade.com/pinwheel-freechat. Messages are PNGs sealed with " +
            "ML-KEM-1024 + AES-256-GCM; the server relays ciphertext it cannot read.",
            10.5, Muted));
        _chatAuthPanel.Children.Add(T("USERNAME", 8.5, Accent, bold: true));
        _chatAuthPanel.Children.Add(_chatUsername);
        _chatAuthPanel.Children.Add(T("PIN · 4+ characters", 8.5, Accent, bold: true));
        _chatAuthPanel.Children.Add(_chatPin);
        _chatAuthPanel.Children.Add(authButtons);
        _chatAuthPanel.Children.Add(_chatError);
        _chatAuthPanel.Children.Add(T(
            "Sign in or create an account once. Your PIN is stored only inside the encrypted local " +
            "profile so the room can reconnect after you unlock the browser.", 9, Muted));

        _chatIdentity.FontSize = 10;
        _chatIdentity.Foreground = C(Text);
        _chatIdentity.FontFamily = Mono;
        _chatRoomStatus.FontSize = 9;
        _chatRoomStatus.Foreground = C(Muted);
        _chatRoomStatus.TextWrapping = TextWrapping.Wrap;

        StyleInput(_chatInput, "message the room…");
        _chatInput.MaxLength = 600;
        _chatInput.Height = 38;
        _chatInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SendChatMessage();
        };
        var send = Btn("SEND", SendChatMessage, accent: true);
        send.Height = 38;
        var copyId = Btn("COPY ID", CopyChatId);
        var reconnect = Btn("RECONNECT", () => _ = ConnectChatAsync());
        var signOut = Btn("SIGN OUT", SignOutChat);
        copyId.Height = 26;
        reconnect.Height = 26;
        signOut.Height = 26;

        var roomHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var roomTitle = new StackPanel { Spacing = 3 };
        roomTitle.Children.Add(_chatIdentity);
        roomTitle.Children.Add(_chatRoomStatus);
        roomHeader.Children.Add(roomTitle);
        var roomActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top
        };
        roomActions.Children.Add(copyId);
        roomActions.Children.Add(reconnect);
        roomActions.Children.Add(signOut);
        Grid.SetColumn(roomActions, 1);
        roomHeader.Children.Add(roomActions);

        _chatMessagesScroll.Content = _chatMessages;
        _chatMessagesScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _chatMessagesScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _chatMessagesScroll.Padding = new Thickness(0, 6, 8, 6);

        var composer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 7 };
        composer.Children.Add(_chatInput);
        Grid.SetColumn(send, 1);
        composer.Children.Add(send);

        var messageColumn = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 9,
            Margin = new Thickness(16, 14, 10, 14)
        };
        messageColumn.Children.Add(roomHeader);
        Grid.SetRow(_chatMessagesScroll, 1);
        messageColumn.Children.Add(_chatMessagesScroll);
        Grid.SetRow(composer, 2);
        messageColumn.Children.Add(composer);

        _chatMembers.Children.Clear();
        _chatBlockedList.Children.Clear();
        var side = new StackPanel { Spacing = 8 };
        side.Children.Add(T("IN THIS ROOM", 8.5, Accent, bold: true));
        side.Children.Add(_chatMembers);
        side.Children.Add(T("BLOCKED IDS · CHAT HIDDEN + NOT SENT", 8.5, Amber, bold: true));
        side.Children.Add(_chatBlockedList);
        var sideScroll = new ScrollViewer
        {
            Content = side,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var sideBorder = new Border
        {
            Background = C("#0b131c"),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12, 14, 12, 14),
            Child = sideScroll
        };

        _chatRoomPanel.Children.Clear();
        _chatRoomPanel.ColumnDefinitions = new ColumnDefinitions("*,250");
        _chatRoomPanel.Children.Add(messageColumn);
        Grid.SetColumn(sideBorder, 1);
        _chatRoomPanel.Children.Add(sideBorder);
        _chatRoomPanel.IsVisible = false;

        var body = new Grid();
        body.Children.Add(_chatAuthPanel);
        body.Children.Add(_chatRoomPanel);

        var close = Btn("CLOSE", CloseChatOverlay);
        close.Height = 26;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 12, 16, 10) };
        var headText = new StackPanel { Spacing = 2 };
        headText.Children.Add(T("FREECHAT · PINWHEEL PROTOCOL", 12, Accent, bold: true));
        headText.Children.Add(T("ML-KEM-1024 · AES-256-GCM · images only", 8.5, Muted));
        head.Children.Add(headText);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);

        var sheetGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        sheetGrid.Children.Add(head);
        Grid.SetRow(body, 1);
        sheetGrid.Children.Add(body);

        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Width = 900,
            Height = 640,
            MaxHeight = 760,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = sheetGrid
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#dd05070a") });
        grid.Children.Add(sheet);
        return grid;
    }

    // -------------------------------------------------------------------- kmail

    private Grid BuildMailOverlay()
    {
        StyleInput(_mailPassword, "mail password (8+ chars)");
        _mailPassword.PasswordChar = '\u25CF';
        _mailPassword.Height = 36;
        _mailPassword.KeyDown += (_, e) => { if (e.Key == Key.Enter) MailSubmit(); };
        StyleInput(_mailPasswordConfirm, "confirm mail password");
        _mailPasswordConfirm.PasswordChar = '\u25CF';
        _mailPasswordConfirm.Height = 36;
        _mailPasswordConfirm.KeyDown += (_, e) => { if (e.Key == Key.Enter) MailSubmit(); };
        StyleInput(_mailHandle, "display handle");
        _mailHandle.Height = 36;
        _mailHandle.Text = DefaultMailHandle();
        _mailError.FontSize = 10.5;
        _mailError.Foreground = C(Red);
        _mailError.TextWrapping = TextWrapping.Wrap;
        _mailAuthTitle.FontSize = 16;
        _mailAuthTitle.FontWeight = FontWeight.Bold;
        _mailAuthTitle.Foreground = C(Amber);
        _mailAuthHint.FontSize = 10.5;
        _mailAuthHint.Foreground = C(Muted);
        _mailAuthHint.TextWrapping = TextWrapping.Wrap;
        _mailAuthButton = Btn("CREATE MAIL", MailSubmit, accent: true);
        _mailAuthButton.Height = 34;

        _mailAuthPanel.Width = 430;
        _mailAuthPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _mailAuthPanel.VerticalAlignment = VerticalAlignment.Center;
        _mailAuthPanel.Children.Clear();
        _mailAuthPanel.Children.Add(_mailAuthTitle);
        _mailAuthPanel.Children.Add(_mailAuthHint);
        _mailAuthPanel.Children.Add(T("HANDLE", 8.5, Accent, bold: true));
        _mailAuthPanel.Children.Add(_mailHandle);
        _mailAuthPanel.Children.Add(T("MAIL PASSWORD · THIS IS NOT YOUR PROFILE KEY", 8.5, Accent, bold: true));
        _mailAuthPanel.Children.Add(_mailPassword);
        _mailAuthPanel.Children.Add(_mailPasswordConfirm);
        _mailAuthPanel.Children.Add(_mailAuthButton);
        _mailAuthPanel.Children.Add(_mailError);
        _mailAuthPanel.Children.Add(T(
            "The password never leaves this computer. It unwraps the local SQLite key; " +
            "the Pinwheel server only stores your public bundle.", 9, Muted));

        _mailIdentity.FontSize = 10;
        _mailIdentity.FontFamily = Mono;
        _mailIdentity.Foreground = C(Text);
        _mailIdentity.TextWrapping = TextWrapping.Wrap;
        _mailStatus.FontSize = 9;
        _mailStatus.Foreground = C(Muted);
        _mailStatus.TextWrapping = TextWrapping.Wrap;
        StyleInput(_mailEndpointBox, "host:port (TLS key pin is added for you)");
        _mailEndpointBox.Height = 28;
        var publish = Btn("PUBLISH", MailSaveEndpoint);
        publish.Height = 28;
        var endpointRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        endpointRow.Children.Add(_mailEndpointBox);
        Grid.SetColumn(publish, 1);
        endpointRow.Children.Add(publish);

        var copyId = Btn("COPY ID", MailCopyId);
        var compose = Btn("COMPOSE", ShowMailCompose, accent: true);
        var refresh = Btn("REFRESH", () => RefreshMailList());
        var lockMail = Btn("LOCK", MailLock);
        copyId.Height = 26;
        compose.Height = 26;
        refresh.Height = 26;
        lockMail.Height = 26;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(compose);
        actions.Children.Add(copyId);
        actions.Children.Add(refresh);
        actions.Children.Add(lockMail);

        _mailRowsScroll.Content = _mailRows;
        _mailRowsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _mailRowsScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var left = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12, 10, 12) };
        left.Children.Add(T("YOUR PFS ID · SHARE THIS NUMBER", 8.5, Accent, bold: true));
        left.Children.Add(_mailIdentity);
        left.Children.Add(T("DIRECT ENDPOINT · LOOKED UP ON PINWHEEL", 8.5, Muted, bold: true));
        left.Children.Add(endpointRow);
        left.Children.Add(actions);
        left.Children.Add(_mailStatus);
        left.Children.Add(new Border
        {
            Background = C(Line),
            Height = 1,
            Margin = new Thickness(0, 4)
        });
        left.Children.Add(_mailRowsScroll);

        _mailReader.Children.Clear();
        var readerScroll = new ScrollViewer
        {
            Content = _mailReader,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 14, 18, 14)
        };

        StyleInput(_mailTo, "eight-digit PFS id");
        _mailTo.Height = 32;
        _mailTo.MaxLength = 12;
        StyleInput(_mailSubject, "subject");
        _mailSubject.Height = 32;
        _mailSubject.MaxLength = 200;
        StyleInput(_mailBody, "write the message…");
        _mailBody.AcceptsReturn = true;
        _mailBody.TextWrapping = TextWrapping.Wrap;
        _mailBody.Height = 220;
        _mailBody.MaxLength = 90_000;
        var send = Btn("SEND DIRECT", SendMail, accent: true);
        var cancel = Btn("CANCEL", ShowMailBlank);
        var composeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        composeButtons.Children.Add(send);
        composeButtons.Children.Add(cancel);
        _mailComposePanel.Children.Clear();
        _mailComposePanel.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto");
        _mailComposePanel.RowSpacing = 8;
        _mailComposePanel.Margin = new Thickness(18, 14, 18, 14);
        _mailComposePanel.Children.Add(T("NEW SEALED MAIL · 90,000 CHARACTERS MAX", 9, Accent, bold: true));
        Grid.SetRow(_mailTo, 1);
        _mailComposePanel.Children.Add(_mailTo);
        Grid.SetRow(_mailSubject, 2);
        _mailComposePanel.Children.Add(_mailSubject);
        Grid.SetRow(_mailBody, 3);
        _mailComposePanel.Children.Add(_mailBody);
        Grid.SetRow(composeButtons, 4);
        _mailComposePanel.Children.Add(composeButtons);
        _mailComposePanel.IsVisible = false;

        var readerHost = new Grid();
        readerHost.Children.Add(readerScroll);
        readerHost.Children.Add(_mailComposePanel);
        var right = new Border
        {
            Background = C("#0b131c"),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = readerHost
        };

        _mailMainPanel.Children.Clear();
        _mailMainPanel.ColumnDefinitions = new ColumnDefinitions("320,*");
        _mailMainPanel.Children.Add(left);
        Grid.SetColumn(right, 1);
        _mailMainPanel.Children.Add(right);
        _mailMainPanel.IsVisible = false;

        var body = new Grid();
        body.Children.Add(_mailAuthPanel);
        body.Children.Add(_mailMainPanel);

        var close = Btn("CLOSE", CloseMailOverlay);
        close.Height = 26;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 12, 16, 8) };
        var headText = new StackPanel { Spacing = 2 };
        headText.Children.Add(T("KICKLMAIL · DIRECT PFS", 12, Accent, bold: true));
        headText.Children.Add(T(
            "Dilithium-authenticated · RSA-3072 key wrap · AES-256-GCM · SQLite on this device",
            8.5, Muted));
        head.Children.Add(headText);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);

        var sheetGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        sheetGrid.Children.Add(head);
        Grid.SetRow(body, 1);
        sheetGrid.Children.Add(body);

        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Width = 1000,
            Height = 620,
            MaxHeight = 660,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = sheetGrid
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#dd05070a") });
        grid.Children.Add(sheet);
        return grid;
    }

    private string DefaultMailHandle()
    {
        var name = ProfileCatalog.DisplayName(_selectedProfileId);
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "member";
        return name.Length <= 32 ? name : name[..32];
    }

    private void OpenMail()
    {
        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            Dispatcher.UIThread.Post(() => _pinBox.Focus());
            SetStatus("unlock the profile before opening mail");
            return;
        }

        _mail ??= new MailService(_session.Paths);
        if (_mailOverlay.IsVisible)
        {
            if (_mail.IsUnlocked) RefreshMailList();
            return;
        }
        _mailOverlay.IsVisible = true;
        if (_mail.IsUnlocked)
        {
            ShowMailMain();
            return;
        }

        ShowMailAuth(create: !_mail.HasPassword, preserveInput: false);
    }

    private void ShowMailAuth(bool create, bool preserveInput = true)
    {
        _mailCreating = create;
        _mailAuthPanel.IsVisible = true;
        _mailMainPanel.IsVisible = false;
        _mailAuthTitle.Text = create ? "CREATE YOUR MAIL PASSWORD" : "UNLOCK KICKLMAIL";
        _mailAuthHint.Text = create
            ? "Bridge your profile with a separate mail password. It seals the local SQLite vault; " +
              "losing it means losing the mail in that vault."
            : "Enter the mail password to unwrap the local SQLite key and open the direct listener.";
        _mailHandle.IsVisible = create;
        _mailPasswordConfirm.IsVisible = create;
        _mailAuthButton.Content = create ? "CREATE MAIL" : "UNLOCK MAIL";
        _mailError.Text = "";
        if (!preserveInput)
        {
            _mailPassword.Text = "";
            _mailPasswordConfirm.Text = "";
        }
        if (create && string.IsNullOrWhiteSpace(_mailHandle.Text))
            _mailHandle.Text = DefaultMailHandle();
    }

    private void ShowMailMain()
    {
        _mailPassword.Text = "";
        _mailPasswordConfirm.Text = "";
        _mailAuthPanel.IsVisible = false;
        _mailMainPanel.IsVisible = true;
        RefreshMailIdentity();
        RefreshMailList();
        ShowMailBlank();
        try
        {
            if (_mail?.ListeningPort is null) _mail?.StartListener();
        }
        catch (Exception ex)
        {
            _mailStatus.Text = "listener unavailable · " + ex.Message;
        }
        StartMailTimer();
    }

    private void CloseMailOverlay()
    {
        _mailOverlay.IsVisible = false;
        _mailTimer?.Stop();
        _mailTimer = null;
    }

    private void MailLock()
    {
        _mail?.Lock();
        CloseMailOverlay();
        SetStatus("mail locked · SQLite key and listener keys erased");
    }

    private void MailSubmit() => _ = MailSubmitAsync();

    private async Task MailSubmitAsync()
    {
        if (_mail is null || _mailBusy) return;
        var password = _mailPassword.Text ?? "";
        _mailBusy = true;
        _mailError.Text = "";
        try
        {
            if (_mailCreating)
            {
                if (!string.Equals(password, _mailPasswordConfirm.Text ?? "", StringComparison.Ordinal))
                    throw new ArgumentException("The mail password confirmation does not match.");
                var handle = (_mailHandle.Text ?? "").Trim();
                if (handle.Length == 0) handle = DefaultMailHandle();
                _mailAuthButton.IsEnabled = false;
                _mailAuthHint.Text = "Generating RSA-3072 + ML-DSA-65 keys and publishing the public bundle…";
                await _mail.SetupAsync(password, handle);
                ShowMailMain();
                _mailStatus.Text = "identity published · direct listener ready on " + _mail.Endpoint;
            }
            else
            {
                _mailAuthButton.IsEnabled = false;
                _mailAuthHint.Text = "Unwrapping the SQLite vault…";
                await _mail.UnlockAsync(password, DefaultMailHandle());
                ShowMailMain();
            }
        }
        catch (Exception ex)
        {
            if (_mail.IsUnlocked && _mail.Store.Identity is not null)
            {
                ShowMailMain();
                _mailStatus.Text = "local vault ready · Pinwheel publish failed · " + ex.Message;
            }
            else
            {
                _mailError.Text = ex.Message;
                _mailAuthHint.Text = _mailCreating
                    ? "Your input is still here. Fix the error and try again; the password was not changed."
                    : "Your input is still here. Check the password and try again.";
            }
        }
        finally
        {
            _mailBusy = false;
            _mailAuthButton.IsEnabled = true;
        }
    }

    private void RefreshMailIdentity()
    {
        if (_mail?.ShortId is not { } shortId) return;
        _mailIdentity.Text = $"PFS ID {FormatMailId(shortId)} · @{_mail.Handle}";
        _mailEndpointBox.Text = _mail.Endpoint ?? "";
    }

    private static string FormatMailId(string shortId) =>
        shortId.Length == 8 ? shortId[..4] + " " + shortId[4..] : shortId;

    private void StartMailTimer()
    {
        if (_mailTimer is not null) return;
        _mailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _mailTimer.Tick += (_, _) =>
        {
            if (_mailMainPanel.IsVisible) RefreshMailList();
        };
        _mailTimer.Start();
    }

    private void RefreshMailList()
    {
        if (_mail is null || !_mail.IsUnlocked) return;
        _mailRows.Children.Clear();
        var messages = _mail.Store.List();
        if (messages.Count == 0)
        {
            _mailRows.Children.Add(T(
                "No mail yet. Ask the other person for their eight-digit PFS ID. Delivery is direct, so their device must be online with mail unlocked.",
                9, Muted));
            return;
        }

        foreach (var message in messages)
        {
            var incoming = message.Direction == "in";
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var dot = T(message.Unread ? "●" : "○", 9, message.Unread ? Amber : Muted);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 2, 7, 0);
            grid.Children.Add(dot);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(T((incoming ? "FROM @" : "TO @") + message.PeerHandle, 10, Text, bold: message.Unread));
            text.Children.Add(T(
                (incoming ? "incoming" : "sent") + " · " + FormatMailId(message.PeerShortId) +
                " · " + Stamp(message.CreatedAt), 8.5, Muted));
            text.Children.Add(T("SEALED · click to open", 8, incoming ? Accent : Green));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var size = T((message.ImageBytes / 1024) + " KB", 8, Muted);
            size.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            var row = new Button
            {
                Content = grid,
                Background = C(message.Unread ? "#102030" : "#0c1621"),
                Foreground = C(Text),
                BorderBrush = C("#1d2c3c"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            var id = message.Id;
            row.Click += (_, _) => OpenMailMessage(id);
            _mailRows.Children.Add(row);
        }
    }

    private void OpenMailMessage(string id)
    {
        if (_mail is null) return;
        try
        {
            var content = _mail.Read(id);
            _mailOpenId = id;
            _mailOpenSubject = content.Subject;
            _mailPages = SplitMailPages(content.Body);
            _mailPage = 0;
            _mailComposePanel.IsVisible = false;
            ShowMailPage();
            RefreshMailList();
        }
        catch (Exception ex)
        {
            _mailStatus.Text = "open failed · " + ex.Message;
        }
    }

    private static List<string> SplitMailPages(string body)
    {
        const int pageChars = 2400;
        var pages = new List<string>();
        var current = new StringBuilder();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > pageChars)
            {
                pages.Add(current.ToString());
                current.Clear();
            }
            if (line.Length > pageChars)
            {
                for (var i = 0; i < line.Length; i += pageChars)
                    pages.Add(line.Substring(i, Math.Min(pageChars, line.Length - i)));
                continue;
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) pages.Add(current.ToString());
        if (pages.Count == 0) pages.Add("");
        return pages;
    }

    private void ShowMailPage()
    {
        _mailReader.Children.Clear();
        if (_mailOpenId is null || _mailPages.Count == 0) return;
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var back = Btn("INBOX", ShowMailBlank);
        back.Height = 25;
        nav.Children.Add(back);
        var prev = Btn("PREV", () =>
        {
            if (_mailPage <= 0) return;
            _mailPage--;
            ShowMailPage();
        });
        var next = Btn("NEXT", () =>
        {
            if (_mailPage >= _mailPages.Count - 1) return;
            _mailPage++;
            ShowMailPage();
        });
        prev.Height = 25;
        next.Height = 25;
        prev.IsEnabled = _mailPage > 0;
        next.IsEnabled = _mailPage < _mailPages.Count - 1;
        nav.Children.Add(prev);
        nav.Children.Add(next);
        nav.Children.Add(T($"page {_mailPage + 1} / {_mailPages.Count}", 9, Muted));
        _mailReader.Children.Add(nav);

        var png = MailBodyImage.RenderPage(_mailOpenSubject ?? "", _mailPages[_mailPage],
            _mailPage, _mailPages.Count);
        _mailReader.Children.Add(new Image
        {
            Source = new Bitmap(new MemoryStream(png)),
            Width = 620,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        });
    }

    private void ShowMailBlank()
    {
        _mailOpenId = null;
        _mailOpenSubject = null;
        _mailPages = [];
        _mailComposePanel.IsVisible = false;
        _mailReader.Children.Clear();
        _mailReader.Children.Add(T("No message open.", 11, Text, bold: true));
        _mailReader.Children.Add(T(
            "Click a sealed row. Nothing is decrypted or previewed until you click it.", 9.5, Muted));
    }

    private void ShowMailCompose()
    {
        if (_mail is null || !_mail.IsUnlocked) return;
        _mailOpenId = null;
        _mailOpenSubject = null;
        _mailPages = [];
        _mailReader.Children.Clear();
        _mailComposePanel.IsVisible = true;
        _mailTo.Text = "";
        _mailSubject.Text = "";
        _mailBody.Text = "";
        _mailStatus.Text = "direct delivery · no server mailbox";
        _mailTo.Focus();
    }

    private void SendMail() => _ = SendMailAsync();

    private async Task SendMailAsync()
    {
        if (_mail is null) return;
        var to = new string((_mailTo.Text ?? "").Where(char.IsAsciiDigit).ToArray());
        var subject = _mailSubject.Text ?? "";
        var body = _mailBody.Text ?? "";
        if (to.Length != 8)
        {
            _mailStatus.Text = "recipient PFS id must be eight digits";
            return;
        }
        if (body.Length == 0)
        {
            _mailStatus.Text = "write a message first";
            return;
        }
        if (body.Length > MailCrypto.MaxBodyChars)
        {
            _mailStatus.Text = "messages are limited to 90,000 characters";
            return;
        }

        _mailStatus.Text = "sealing · looking up " + FormatMailId(to) + " on Pinwheel…";
        try
        {
            await _mail.SendAsync(to, subject, body);
            _mailStatus.Text = "delivered directly · sealed copy kept in local SQLite";
            _mailBody.Text = "";
            _mailSubject.Text = "";
            _mailTo.Text = "";
            _mailComposePanel.IsVisible = false;
            ShowMailBlank();
            RefreshMailList();
        }
        catch (Exception ex)
        {
            _mailStatus.Text = "send failed · " + ex.Message;
        }
    }

    private void MailSaveEndpoint() => _ = MailSaveEndpointAsync();

    private async Task MailSaveEndpointAsync()
    {
        if (_mail is null) return;
        var endpoint = (_mailEndpointBox.Text ?? "").Trim();
        try
        {
            _mailStatus.Text = "rebinding direct listener and publishing endpoint…";
            await _mail.UpdateEndpointAsync(endpoint);
            RefreshMailIdentity();
            _mailStatus.Text = "endpoint published · listening on port " + _mail.ListeningPort;
        }
        catch (Exception ex)
        {
            _mailStatus.Text = "endpoint failed · " + ex.Message;
        }
    }

    private void MailCopyId()
    {
        if (_mail?.ShortId is not { } shortId) return;
        CopyText(FormatMailId(shortId) + " · @" + _mail.Handle, "mail id");
    }

    // ---------------------------------------------------------------- freechat

    private string ChatPrefsPath => Path.Combine(_session.Paths.ProfileRoot, "private", "freechat-prefs.kpf");

    private void OpenChat()
    {
        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            Dispatcher.UIThread.Post(() => _pinBox.Focus());
            SetStatus("unlock the profile before opening chat");
            return;
        }

        LoadChatPrefs();
        _chatOverlay.IsVisible = true;
        if (_chatClient is { IsConnected: true })
        {
            ShowChatRoom();
            StartChatPolling();
        }
        else if (_chatClient is not null)
        {
            ShowChatRoom();
            _ = ConnectChatAsync();
        }
        else if (_chatSavedUser is not null)
        {
            ShowChatAuth();
            _chatUsername.Text = _chatSavedUser;
            _chatPin.Text = _chatSavedPin;
            _chatError.Text = "Saved session · press SIGN IN to reconnect.";
        }
        else
        {
            ShowChatAuth();
        }
    }

    private void CloseChatOverlay()
    {
        _chatOverlay.IsVisible = false;
        _chatTimer?.Stop();
        _chatTimer = null;
    }

    private void CloseChat()
    {
        CloseChatOverlay();
        _chatClient?.Dispose();
        _chatClient = null;
        _chatSavedUser = null;
        _chatSavedPin = null;
        _chatBlocked.Clear();
        _chatMessages.Children.Clear();
        _chatMembers.Children.Clear();
        _chatBlockedList.Children.Clear();
        _chatInput.Text = "";
        _chatPin.Text = "";
    }

    private void ShowChatAuth()
    {
        _chatAuthPanel.IsVisible = true;
        _chatRoomPanel.IsVisible = false;
        _chatError.Text = "";
    }

    private void ShowChatRoom()
    {
        _chatAuthPanel.IsVisible = false;
        _chatRoomPanel.IsVisible = true;
        _chatError.Text = "";
        if (_chatClient?.User is { } user)
        {
            _chatIdentity.Text =
                $"@{user.Username} · FREECHAT ID #{user.Id} · ML-KEM-1024";
            _chatRoomStatus.Text = _chatClient.IsConnected
                ? $"room {_chatClient.SessionId} · encrypted · {_chatClient.Members.Count} member(s)"
                : "signed in · not connected";
        }
        RefreshChatPeople();
    }

    private void ChatSignIn() => _ = ChatAuthenticateAsync(register: false);

    private void ChatRegister() => _ = ChatAuthenticateAsync(register: true);

    private void ChatGuest() => _ = ChatGuestAsync();

    private async Task ChatAuthenticateAsync(bool register)
    {
        if (!_session.IsUnlocked) return;
        var username = (_chatUsername.Text ?? "").Trim();
        var pin = _chatPin.Text ?? "";
        if (username.Length < 3 || pin.Length < 4)
        {
            _chatError.Text = "Username must be at least 3 characters and PIN at least 4.";
            return;
        }

        _chatError.Text = register ? "Creating account…" : "Signing in…";
        try
        {
            var previous = _chatClient;
            var client = NewChatClient();
            try
            {
                if (register) await client.RegisterAsync(username, pin);
                else await client.SignInAsync(username, pin);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            previous?.Dispose();
            _chatClient = client;
            _chatSavedUser = username;
            _chatSavedPin = pin;
            SaveChatPrefs();
            ShowChatRoom();
            await ConnectChatAsync();
        }
        catch (Exception ex)
        {
            _chatError.Text = ex.Message;
            ShowChatAuth();
            _chatError.Text = ex.Message;
        }
    }

    private async Task ChatGuestAsync()
    {
        if (!_session.IsUnlocked) return;
        _chatError.Text = "Opening a guest session…";
        try
        {
            var client = NewChatClient();
            _chatClient?.Dispose();
            _chatClient = client;
            await client.GuestAsync();
            _chatSavedUser = null;
            _chatSavedPin = null;
            ShowChatRoom();
            await ConnectChatAsync();
        }
        catch (Exception ex)
        {
            _chatClient?.Dispose();
            _chatClient = null;
            _chatError.Text = ex.Message;
            ShowChatAuth();
            _chatError.Text = ex.Message;
        }
    }

    private async Task ConnectChatAsync()
    {
        if (_chatClient is null) return;
        _chatTimer?.Stop();
        _chatTimer = null;
        _chatRoomStatus.Text = "connecting · ML-KEM-1024 key exchange…";
        try
        {
            await _chatClient.ConnectAsync();
            ShowChatRoom();
            StartChatPolling();
            await ChatTickAsync();
        }
        catch (Exception ex)
        {
            _chatRoomStatus.Text = "offline · " + ex.Message + " · press RECONNECT";
        }
    }

    private void StartChatPolling()
    {
        if (_chatTimer is not null) return;
        _chatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chatTimer.Tick += (_, _) => _ = ChatTickAsync();
        _chatTimer.Start();
    }

    private async Task ChatTickAsync()
    {
        if (_chatBusy || _chatClient is null || _chatOverlay.IsVisible == false) return;
        _chatBusy = true;
        try
        {
            await _chatClient.DistributePendingAsync();
            var messages = await _chatClient.PollAsync();
            foreach (var message in messages) RenderChatMessage(message);
            RefreshChatPeople();
            if (_chatClient.IsConnected)
                _chatRoomStatus.Text =
                    $"encrypted · {_chatClient.Members.Count} member(s) · auto-refresh 3 s";
        }
        catch (Exception ex) when (ex is FreeChatException or HttpRequestException
                                       or TaskCanceledException)
        {
            _chatRoomStatus.Text = "offline · " + ex.Message + " · retrying";
        }
        catch (ObjectDisposedException)
        {
            _chatTimer?.Stop();
            _chatTimer = null;
        }
        finally
        {
            _chatBusy = false;
        }
    }

    private void RenderChatMessage(FreeChatMessage message)
    {
        if (_chatBlocked.Contains(message.Username)) return;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        who.Children.Add(T("@" + message.Username, 9.5, Accent, bold: true));
        who.Children.Add(T("#" + message.UserId, 8.5, Muted));
        if (message.ServerAuthentic == false)
            who.Children.Add(T("ROLL TAG MISMATCH", 8.5, Amber, bold: true));
        else if (message.ServerAuthentic == true)
            who.Children.Add(T("SERVER ROLL OK", 8.5, Muted));
        switch (message.Trust)
        {
            case SenderTrust.Verified: who.Children.Add(T("✓ SIGNED", 8.5, Green, bold: true)); break;
            case SenderTrust.FirstSeen: who.Children.Add(T("✓ SIGNED · NEW KEY", 8.5, Green, bold: true)); break;
            case SenderTrust.KeyChanged: who.Children.Add(T("⚠ SIGNING KEY CHANGED", 8.5, Red, bold: true)); break;
            case SenderTrust.Invalid: who.Children.Add(T("✖ FORGED OR WRONG SENDER", 8.5, Red, bold: true)); break;
            default: who.Children.Add(T("unsigned", 8.5, Muted)); break;
        }
        head.Children.Add(who);
        var stamp = T(message.CreatedAt, 8.5, Muted);
        Grid.SetColumn(stamp, 1);
        head.Children.Add(stamp);
        var block = Btn("BLOCK", () => ToggleChatBlock(message.Username));
        block.FontSize = 7.5;
        block.Padding = new Thickness(6, 1);
        block.Height = 20;
        Grid.SetColumn(block, 2);
        head.Children.Add(block);

        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(head);
        if (message.Png.Length > 0)
        {
            try
            {
                content.Children.Add(new Image
                {
                    Source = new Bitmap(new MemoryStream(message.Png)),
                    Width = 240,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }
            catch (Exception)
            {
                content.Children.Add(T("[image could not be decoded]", 9, Amber));
            }
        }

        _chatMessages.Children.Add(new Border
        {
            Background = C("#0c1621"),
            BorderBrush = C("#1d2c3c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 7),
            Child = content
        });

        Dispatcher.UIThread.Post(() => _chatMessagesScroll.ScrollToEnd());
    }

    private void RefreshChatPeople()
    {
        if (_chatClient?.User is not { } me) return;
        _chatMembers.Children.Clear();
        foreach (var member in _chatClient.Members.OrderBy(m => m.UserId))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new StackPanel { Spacing = 1 };
            name.Children.Add(T(member.Username + (member.UserId == me.Id ? " (you)" : ""),
                9.5, Text, bold: member.UserId == me.Id));
            var changed = _chatClient.KeyAlerts.Contains(member.Username);
            name.Children.Add(T(changed ? "KEY CHANGED · not given the room key" : (member.HasSecret ? "key ✓" : "key pending") + " · #" + member.UserId,
                8, changed ? Red : member.HasSecret ? Green : Amber));
            if (member.UserId != me.Id)
                name.Children.Add(T(FreeChatClient.KeyFingerprint(member), 7, Muted, mono: true));
            row.Children.Add(name);
            if (changed)
            {
                var accept = Btn("ACCEPT KEY", () => { _chatClient!.AcceptKey(member.Username); RefreshChatPeople(); });
                accept.FontSize = 7.5;
                accept.Padding = new Thickness(6, 1);
                accept.Height = 20;
                Grid.SetColumn(accept, 1);
                row.Children.Add(accept);
            }
            else if (member.UserId != me.Id && !string.IsNullOrWhiteSpace(member.Username))
            {
                var action = Btn(_chatBlocked.Contains(member.Username) ? "UNBLOCK" : "BLOCK",
                    () => ToggleChatBlock(member.Username));
                action.FontSize = 7.5;
                action.Padding = new Thickness(6, 1);
                action.Height = 20;
                Grid.SetColumn(action, 1);
                row.Children.Add(action);
            }
            _chatMembers.Children.Add(row);
        }
        if (_chatMembers.Children.Count == 0)
            _chatMembers.Children.Add(T("No members yet. Share your ID and keep this room open.", 8.5, Muted));

        _chatBlockedList.Children.Clear();
        foreach (var username in _chatBlocked.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(T("@" + username, 9, Amber));
            var action = Btn("UNBLOCK", () => ToggleChatBlock(username));
            action.FontSize = 7.5;
            action.Padding = new Thickness(6, 1);
            action.Height = 20;
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
            _chatBlockedList.Children.Add(row);
        }
        if (_chatBlockedList.Children.Count == 0)
            _chatBlockedList.Children.Add(T("Nobody blocked.", 8.5, Muted));
        _chatBlockedList.Children.Add(T(
            "Blocking hides incoming messages from that ID on this profile. It is a local filter; " +
            "the room is a group broadcast, so their ciphertext still reaches other members.", 8, Muted));
    }

    private void ToggleChatBlock(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        if (!_chatBlocked.Add(username)) _chatBlocked.Remove(username);
        SaveChatPrefs();
        RefreshChatPeople();
        SetStatus(_chatBlocked.Contains(username)
            ? "blocked @" + username + " · incoming chat hidden"
            : "unblocked @" + username);
    }

    private void CopyChatId()
    {
        if (_chatClient?.User is not { } user)
        {
            SetStatus("sign in before sharing a chat id");
            return;
        }
        CopyText($"@{user.Username} · FreeChat ID #{user.Id}", "chat id");
    }

    private void SignOutChat()
    {
        _chatSavedUser = null;
        _chatSavedPin = null;
        _chatBlocked.Clear();
        SaveChatPrefs();
        _chatClient?.Dispose();
        _chatClient = null;
        _chatMessages.Children.Clear();
        _chatMembers.Children.Clear();
        _chatBlockedList.Children.Clear();
        _chatPin.Text = "";
        _chatTimer?.Stop();
        _chatTimer = null;
        ShowChatAuth();
        SetStatus("freechat signed out · local credentials erased");
    }

    private void SendChatMessage()
    {
        var text = (_chatInput.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (_chatClient is not { IsConnected: true })
        {
            _chatRoomStatus.Text = "not connected · press RECONNECT";
            return;
        }

        _chatInput.Text = "";
        _ = SendChatMessageAsync(text);
    }

    private async Task SendChatMessageAsync(string text)
    {
        if (_chatClient is null) return;
        try
        {
            var png = ChatTextImage.Render(text);
            await _chatClient.SendAsync(png);
            await ChatTickAsync();
        }
        catch (Exception ex)
        {
            _chatInput.Text = text;
            _chatRoomStatus.Text = "send failed · " + ex.Message;
        }
    }

    private void LoadChatPrefs()
    {
        try
        {
            if (!_session.IsUnlocked) return;
            var path = ChatPrefsPath;
            if (!File.Exists(path)) return;
            var plaintext = _session.OpenProfileData("freechat-prefs", File.ReadAllBytes(path));
            try
            {
                var prefs = JsonSerializer.Deserialize<ChatPrefs>(plaintext);
                if (prefs is null) return;
                _chatSavedUser = prefs.Username;
                _chatSavedPin = prefs.Pin;
                _chatBlocked.Clear();
                foreach (var name in prefs.Blocked ?? []) _chatBlocked.Add(name);
                _chatKeyPins = prefs.KeyPins ?? [];
                if (prefs.SigningKey is { Length: > 0 } sk) _chatSigningKey = Convert.FromBase64String(sk);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception)
        {
            // A corrupt or old preference file must not keep chat closed.
        }
    }

    private void SaveChatPrefs(byte[]? newSigningKey = null)
    {
        if (newSigningKey is not null) _chatSigningKey = newSigningKey;
        try
        {
            if (!_session.IsUnlocked) return;
            var path = ChatPrefsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var prefs = new ChatPrefs
            {
                Username = _chatSavedUser,
                Pin = _chatSavedPin,
                Blocked = _chatBlocked.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                KeyPins = _chatKeyPins,
                SigningKey = _chatSigningKey is null ? null : Convert.ToBase64String(_chatSigningKey)
            };
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(prefs));
            var encrypted = _session.ProtectProfileData("freechat-prefs", json);
            CryptographicOperations.ZeroMemory(json);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, encrypted);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            SetStatus("freechat preferences could not be saved · " + ex.Message);
        }
    }

    private sealed class ChatPrefs
    {
        public string? Username { get; set; }
        public string? Pin { get; set; }
        public List<string>? Blocked { get; set; }
        public Dictionary<string, string>? KeyPins { get; set; }
        public string? SigningKey { get; set; }
    }

    private void RefreshProfileGate()
    {
        var profiles = ProfileCatalog.List(_dataRoot);
        if (profiles.Count == 0) _profileCreateMode = true;
        _updatingProfilePicker = true;
        _profilePicker.ItemsSource = profiles.Select(ProfileCatalog.DisplayName).ToArray();
        if (profiles.Contains(_selectedProfileId, StringComparer.Ordinal))
            _profilePicker.SelectedItem = ProfileCatalog.DisplayName(_selectedProfileId);
        else if (profiles.Count > 0)
            _profilePicker.SelectedItem = ProfileCatalog.DisplayName(profiles[0]);
        _updatingProfilePicker = false;

        var hasProfile = _session.HasProfile;
        _gateTitle.Text = _profileCreateMode
            ? (_demoMode ? "CREATE A DEMO OR PRIVATE PROFILE" : "CREATE PROFILE")
            : (_demoMode ? "DEMO SESSION · SIGN IN OR CREATE" : "SIGN IN");
        _gateMessage.Text = _profileCreateMode
            ? "Create an encrypted local profile with a name and your own 8+ character key. New profiles do not use the demo 1234 key. Recovery and cloud sync are not available."
            : (_demoMode
                ? "The throwaway demo profile is ready with key 1234. Or choose CREATE NEW PROFILE and use your own key. Do not put personal data in the demo profile."
                : $"Sign in to {ProfileCatalog.DisplayName(_selectedProfileId)} with its local user key. Profile data remains encrypted until sign-in.");
        _profilePicker.IsVisible = profiles.Count > 0 && !_profileCreateMode;
        _profileNameBox.IsVisible = _profileCreateMode;
        _confirmPinBox.IsVisible = _profileCreateMode;
        _unlockProfileButton.IsVisible = hasProfile && !_profileCreateMode;
        _createProfileButton.Content = _profileCreateMode ? "CREATE PROFILE" : "CREATE NEW PROFILE";
        _createProfileButton.IsVisible = _profileCreateMode || hasProfile || _demoMode;
        _cancelProfileCreateButton.IsVisible = _profileCreateMode && profiles.Count > 0;
    }

    private void BeginProfileCreation()
    {
        _profileCreateMode = true;
        _pinBox.Text = "";
        _confirmPinBox.Text = "";
        RefreshProfileGate();
        _profileNameBox.Focus();
    }

    private void CancelProfileCreation()
    {
        _profileCreateMode = false;
        _profileNameBox.Text = "";
        _pinBox.Text = "";
        _confirmPinBox.Text = "";
        RefreshProfileGate();
        _pinBox.Focus();
    }

    private void SwitchProfile(string profileId)
    {
        if (string.Equals(profileId, _selectedProfileId, StringComparison.Ordinal)) return;
        try
        {
            _profileEpoch++;
            _pinBox.Text = "";
            _confirmPinBox.Text = "";
            foreach (var tab in _tabs)
            {
                tab.Unlocked.Clear();
                tab.Texts.Clear();
                ClearImages(tab);
            }
            _mailOverlay.IsVisible = false;
            _chatOverlay.IsVisible = false;
            _mailTimer?.Stop();
            _mailTimer = null;
            _mail?.Dispose();
            _mail = null;
            _session.Dispose();
            _selectedProfileId = profileId;
            _session = new BrowserSession(_dataRoot, profileId);
            _mail = new MailService(_session.Paths);
            _savedItems = new SavedItemsStore(_dataRoot, _session);
            _sources.Clear();
            _rssNews.Clear();
            _newIds.Clear();
            _seenSiteIds.Clear();
            _newsSignature = "";
            _homeAdCollapsed = false;
            _newsModeSaved = false;
            StyleModeButton(_streamButton, active: true);
            StyleModeButton(_savedButton, active: false);

            _tabs.Clear();
            _active = -1;
            NewTab();
            _lockOverlay.IsVisible = true;
            SetPill(_sessionPill, "LOCKED", Amber, "#241f0d");
            RefreshProfileGate();
            RefreshRegistryNews();
            SetStatus($"selected profile {ProfileCatalog.DisplayName(profileId)} · sign in to unlock");
        }
        catch (Exception ex)
        {
            SetStatus("profile switch failed · " + ex.Message);
        }
    }

    private void CreateProfile()
    {
        if (!_profileCreateMode) return;
        var pin = _pinBox.Text ?? "";
        try
        {
            if (pin.Length < 8)
                throw new ArgumentException("User key must be at least 8 characters.");
            if (!string.Equals(pin, _confirmPinBox.Text ?? "", StringComparison.Ordinal))
                throw new ArgumentException("The user key confirmation does not match.");
            var profileId = ProfileCatalog.CreateId(_profileNameBox.Text ?? "");
            if (Keystore.Exists(new DataPaths(_dataRoot, profileId)))
                throw new InvalidOperationException("A profile with that name already exists.");

            SwitchProfile(profileId);
            _session.CreateProfile(pin);
            // The new profile remains locked until the user explicitly signs in.
            _pinBox.Text = "";
            _confirmPinBox.Text = "";
            _profileCreateMode = false;
            _lockOverlay.IsVisible = true;
            SetPill(_sessionPill, "LOCKED", Amber, "#241f0d");
            SetStatus($"profile {ProfileCatalog.DisplayName(profileId)} created · sign in to unlock");
            _profileNameBox.Text = "";
            RefreshProfileGate();
            _pinBox.Focus();
        }
        catch (Exception ex)
        {
            SetStatus("profile creation failed · " + ex.Message);
            _pinBox.SelectAll();
        }
    }

    // ------------------------------------------------------------------ tabs & navigation

    private void NewTab()
    {
        var tab = new BrowserTab { Address = "", Title = "home" };
        _tabs.Add(tab);
        _active = _tabs.Count - 1;
        OpenHome(tab);
    }

    private void NewTab(string address)
    {
        StopAllGames();
        _tabs.Add(new BrowserTab());
        _active = _tabs.Count - 1;
        NavigateToActive(address);
    }

    private void NavigateHome()
    {
        var tab = Active;
        if (tab is null)
        {
            NewTab();
            return;
        }
        OpenHome(tab, resetSearch: false);
    }

    private void OpenHome(BrowserTab tab, bool resetSearch = true)
    {
        tab.Page = null;
        tab.Draft = null;
        tab.Error = null;
        tab.Game = null;
        tab.BuiltIn = null;
        tab.Address = "";
        tab.Title = "home";
        tab.Verified = false;
        tab.Ownership = null;
        tab.ContentHash = "";
        tab.Transport = null;
        tab.TransportFingerprint = null;
        tab.Unlocked.Clear();
        tab.Texts.Clear();
        if (resetSearch)
        {
            tab.HomeQuery = "";
            tab.HomeResults.Clear();
            tab.HomeHint = DefaultHomeHint;
        }
        ClearImages(tab);
        _address.Text = "";
        SetStatus("home · no address looked up · arcade array + PFS tag search");
        RenderAll(resetScroll: true);
        Dispatcher.UIThread.Post(() => _homeSearchBox?.Focus());
    }

    private void OpenGame(BrowserTab tab, string gameId)
    {
        if (gameId == "tic-tac-toe")
        {
            tab.TttBoard = new char[9];
            tab.TttMessage = "Your turn.";
        }
        else if (gameId == "memory")
        {
            ResetMemoryGame(tab);
        }

        tab.Game = gameId;
        tab.Page = null;
        tab.Draft = null;
        tab.Error = null;
        tab.BuiltIn = null;
        tab.Address = "";
        tab.Title = "arcade · " + gameId;
        tab.Unlocked.Clear();
        tab.Texts.Clear();
        ClearImages(tab);
        RenderAll(resetScroll: true);
    }

    private void CloseTab(int index)
    {
        StopAllGames();
        if (index < 0 || index >= _tabs.Count) return;
        _lastClosedAddress = string.IsNullOrWhiteSpace(_tabs[index].Address)
            ? null
            : _tabs[index].Address;
        ClearImages(_tabs[index]);
        _tabs.RemoveAt(index);
        if (_tabs.Count == 0)
        {
            NewTab(HomeUrl);
            return;
        }
        if (_active >= _tabs.Count) _active = _tabs.Count - 1;
        else if (_active > index) _active--;
        ActivateTab(_active);
    }

    private void SelectRelativeTab(int delta)
    {
        if (_tabs.Count < 2) return;
        var index = (_active + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(index);
    }

    private void SelectTab(int oneBasedIndex)
    {
        StopAllGames();
        if (_tabs.Count == 0) return;
        var index = oneBasedIndex == 9 ? _tabs.Count - 1 : oneBasedIndex - 1;
        if (index >= 0 && index < _tabs.Count)
            ActivateTab(index);
    }

    private void ReopenLastClosedTab()
    {
        var address = _lastClosedAddress;
        _lastClosedAddress = null;
        if (string.IsNullOrWhiteSpace(address)) NewTab();
        else NewTab(address);
    }

    private void CycleKeyboardFocus()
    {
        var targets = new Control[] { _address, _pageHost, _newsFeed };
        for (var attempt = 0; attempt < targets.Length; attempt++)
        {
            var target = targets[_focusCycleIndex % targets.Length];
            _focusCycleIndex = (_focusCycleIndex + 1) % targets.Length;
            if (target.Focus()) return;
        }
        _address.Focus();
    }

    private void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _active = index;
        var tab = _tabs[index];
        if (tab.Page is not null)
        {
            try
            {
                _session.Open(tab.Address);
                foreach (var id in tab.Unlocked)
                    tab.Texts[id] = _session.UnlockBlob(id);
                tab.Ownership = _session.OwnershipOf(tab.Page.Site);
                tab.Transport = _session.LastTransport;
                tab.TransportFingerprint = _session.LastTransportFingerprint;
            }
            catch (Exception ex)
            {
                tab.Error = ex.Message;
            }
        }
        RenderAll();
    }

    private readonly List<string> _omniboxSuggestionUrls = [];

    private void RefreshOmniboxSuggestions()
    {
        _omniboxSuggestions.Children.Clear();
        _omniboxSuggestionUrls.Clear();
        var input = (_address.Text ?? "").Trim();
        if (input.Length < 2 || !_address.IsFocused || _lockOverlay.IsVisible)
        {
            _omniboxSuggestions.IsVisible = false;
            return;
        }

        var candidates = new List<(string Label, string Url, string Kind)>();
        var lane = InputClassifier.Classify(input);
        if (lane.Lane == InputLane.Sealed && lane.Url is not null)
            candidates.Add((lane.Url, lane.Url, "address"));
        else if (lane.Lane == InputLane.Clearnet && lane.Url is not null)
            candidates.Add((lane.Url, lane.Url, "open web · external browser"));
        else if (lane.Lane == InputLane.Search)
        {
            var query = input.TrimEnd('/');
            if (query.Length > 0 && !query.Contains(' ') && _session.IsUnlocked)
            {
                try
                {
                    var registry = LocalRegistry.LoadOrEmpty(_session.Paths);
                    candidates.AddRange(registry.Records
                        .Where(r => r.Status == "active" && r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(5)
                        .Select(r => ($"jttp://{r.Name}/", $"jttp://{r.Name}/", "local site")));
                }
                catch { /* local suggestions fail closed; typing never reaches the network */ }
            }
        }

        _omniboxActiveIndex = 0;
        foreach (var suggestion in candidates.Take(6))
        {
            var index = _omniboxSuggestionUrls.Count;
            _omniboxSuggestionUrls.Add(suggestion.Url);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(T(suggestion.Label, 11, Text, mono: true));
            var kind = T(suggestion.Kind, 9, Muted);
            kind.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(kind);
            Grid.SetColumn(kind, 1);
            var button = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = C(index == _omniboxActiveIndex ? "#203044" : "#101a24"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(9, 7),
                MinHeight = 32
            };
            button.Click += (_, _) => AcceptOmniboxSuggestion(index);
            AutomationProperties.SetName(button, $"{suggestion.Kind}: {suggestion.Label}");
            _omniboxSuggestions.Children.Add(button);
        }
        _omniboxSuggestions.IsVisible = _omniboxSuggestionUrls.Count > 0;
    }

    private void AcceptOmniboxSuggestion(int index)
    {
        if (index < 0 || index >= _omniboxSuggestionUrls.Count) return;
        _address.Text = _omniboxSuggestionUrls[index];
        _omniboxSuggestions.IsVisible = false;
        NavigateOmnibox();
    }

    private void NavigateOmnibox()
    {
        var input = (_address.Text ?? "").Trim();
        if (input.Length == 0) return;

        var lane = InputClassifier.Classify(input);
        switch (lane.Lane)
        {
            case InputLane.Sealed:
                NavigateToActive(lane.Url!);
                break;
            case InputLane.Clearnet:
                OpenExternal(lane.Url!);
                break;
            case InputLane.Search:
                _searchBox.Text = input;
                ToggleSearch();
                RunSearch(input);
                break;
            default:
                SetStatus(lane.Reason ?? "Invalid address.");
                break;
        }
    }

    private void NavigateToActive(string address, bool addHistory = true, bool resetScroll = true)
    {
        StopAllGames();
        var tab = Active;
        if (tab is null)
        {
            NewTab(address);
            return;
        }

        if (!address.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
            address = "jttp://" + address.TrimStart('/');

        if (!_sealedSurface.IsSecureScheme("jttp") || !_sealedSurface.AllowsTopLevelNavigation(address))
        {
            SetStatus("sealed surface blocked navigation outside the secure jttp namespace");
            return;
        }

        if (TryBuiltInRoute(address, out var builtIn, out var builtInCanonical))
        {
            OpenBuiltIn(tab, builtIn, builtInCanonical, addHistory, resetScroll);
            return;
        }

        tab.EditGate = null;
        if (TryEditRoute(address, out var editHost))
        {
            OpenEditRoute(tab, editHost, addHistory);
            return;
        }

        try
        {
            var page = _session.Open(address);
            var canonical = "jttp://" + page.Site + "/";
            tab.Page = page;
            tab.Address = canonical;
            tab.Title = page.Title;
            tab.Verified = _session.IsNameVerified(page.Site);
            tab.Ownership = _session.OwnershipOf(page.Site);
            tab.ContentHash = _session.Document?.Integrity ?? "";
            tab.Transport = _session.LastTransport;
            tab.TransportFingerprint = _session.LastTransportFingerprint;
            tab.Error = null;
            tab.Draft = null;
            tab.Unlocked.Clear();
            tab.Texts.Clear();
            ClearImages(tab);
            if (addHistory)
            {
                var removeAt = tab.HistoryIndex + 1;
                if (removeAt < tab.History.Count)
                    tab.History.RemoveRange(removeAt, tab.History.Count - removeAt);
                if (tab.History.Count == 0 || tab.History[^1] != canonical)
                    tab.History.Add(canonical);
                tab.HistoryIndex = tab.History.Count - 1;
            }
            _address.Text = canonical;
            SetStatus($"opened {canonical} · {tab.Transport} · package hash verified before parse");
            RefreshCaRevocationsSoon();
            AutoSealIfNeeded(page.Site);
            RenderAll(resetScroll);
        }
        catch (ContentIntegrityException ex)
        {
            tab.Error = ex.Message;
            tab.Title = "content integrity failure";
            tab.Address = address;
            tab.ContentHash = "";
            tab.Draft = null;
            tab.Unlocked.Clear();
            tab.Texts.Clear();
            ClearImages(tab);
            SetStatus($"ATTACK REPORTED · {ex.Count} mismatch(es) on {ex.Host}");
            RenderAll(resetScroll);
        }
        catch (SiteNotFoundException ex)
        {
            tab.Page = null;
            tab.Error = null;
            tab.Draft = NewDraft(ex.Host);
            tab.Address = "jttp://" + ex.Host + "/";
            tab.Title = tab.Draft.IsRss
                ? "next story · " + ex.Host
                : "create · " + ex.Host;
            tab.ContentHash = "";
            tab.Unlocked.Clear();
            tab.Texts.Clear();
            ClearImages(tab);
            _address.Text = tab.Address;
            SetStatus($"jttp://{ex.Host}/ is not in the phonebook · fill the template to create it");
            RenderAll(resetScroll);
        }
        catch (Exception ex)
        {
            tab.Error = ex.Message;
            tab.Title = "could not open " + address;
            tab.Address = address;
            tab.ContentHash = "";
            tab.Draft = null;
            tab.Unlocked.Clear();
            tab.Texts.Clear();
            ClearImages(tab);
            SetStatus("open failed · " + ex.Message);
            RenderAll(resetScroll);
        }
    }

    private static bool TryBuiltInRoute(string address, out string route, out string canonical)
    {
        route = "";
        canonical = "";
        if (!JttpUrl.TryParse(address, out var url, out _)) return false;
        if (!string.Equals(url!.Host, Nickles.NetworkHost, StringComparison.OrdinalIgnoreCase))
            return false;

        var path = url.Path.TrimEnd('/');
        if (path.Equals(Nickles.NetworkRoot, StringComparison.OrdinalIgnoreCase))
        {
            route = "overview";
            canonical = $"jttp://{url.Host}{Nickles.NetworkRoot}";
            return true;
        }
        if (path.Equals(Nickles.PublisherPath, StringComparison.OrdinalIgnoreCase))
        {
            route = "publisher";
            canonical = $"jttp://{url.Host}{Nickles.PublisherPath}";
            return true;
        }
        return false;
    }

    private void OpenBuiltIn(BrowserTab tab, string route, string canonical,
        bool addHistory, bool resetScroll)
    {
        tab.BuiltIn = route;
        tab.Page = null;
        tab.Draft = null;
        tab.Error = null;
        tab.Game = null;
        tab.Address = canonical;
        tab.Title = route == "publisher" ? "NCKL publisher" : "ad networking";
        tab.Verified = false;
        tab.Ownership = null;
        tab.ContentHash = "";
        tab.Transport = null;
        tab.TransportFingerprint = null;
        tab.Unlocked.Clear();
        tab.Texts.Clear();
        ClearImages(tab);

        if (addHistory)
        {
            var removeAt = tab.HistoryIndex + 1;
            if (removeAt < tab.History.Count)
                tab.History.RemoveRange(removeAt, tab.History.Count - removeAt);
            if (tab.History.Count == 0 || tab.History[^1] != canonical)
                tab.History.Add(canonical);
            tab.HistoryIndex = tab.History.Count - 1;
        }

        _address.Text = canonical;
        SetStatus(route == "publisher"
            ? "NCKL publisher · mint a site-bound loader and ad space"
            : "secupgrade.com ad networking · publisher console");
        RenderAll(resetScroll);
    }

    private void Back()
    {
        var tab = Active;
        if (tab is null || tab.HistoryIndex <= 0) return;
        var index = tab.HistoryIndex - 1;
        NavigateToActive(tab.History[index], addHistory: false);
        tab.HistoryIndex = index;
    }

    private void Forward()
    {
        var tab = Active;
        if (tab is null || tab.HistoryIndex >= tab.History.Count - 1) return;
        var index = tab.HistoryIndex + 1;
        NavigateToActive(tab.History[index], addHistory: false);
        tab.HistoryIndex = index;
    }

    private void Reload()
    {
        var tab = Active;
        if (tab is null) return;
        if (tab.Page is null)
        {
            RenderAll();
            return;
        }
        NavigateToActive(tab.Address, addHistory: false);
    }

    private void LockSession()
    {
        CloseChat();
        _mail?.Lock();
        _mailOverlay.IsVisible = false;
        _mailTimer?.Stop();
        _mailTimer = null;
        _profileEpoch++;
        StopAllGames();
        _session.Lock();
        _pinBox.Text = "";
        _confirmPinBox.Text = "";
        _sources.Clear();
        _rssNews.Clear();
        _homeAdCollapsed = false;
        _newsSignature = "";
        foreach (var tab in _tabs)
        {
            tab.Unlocked.Clear();
            tab.Texts.Clear();
            ClearImages(tab);
        }
        _searchOverlay.IsVisible = false;
        _confirmOverlay.IsVisible = false;
        _lockOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => _pinBox.Focus());
        RefreshNewsView(force: true);
        SetPill(_sessionPill, "LOCKED", Amber, "#241f0d");
        SetStatus("locked · master, session, document and blob keys erased");
        RenderAll();
    }

    private void UnlockSession()
    {
        try
        {
            _session.Unlock(_pinBox.Text ?? "");
            _homeAdCollapsed = LoadHomeAdPreference();
            LoadSources();
            if (_sources.Count > 0) RefreshRssSources();
            _lockOverlay.IsVisible = false;
            _pinBox.Text = "";
            _confirmPinBox.Text = "";
            RefreshProfileGate();
            SetPill(_sessionPill, "UNLOCKED · PFS", Green, "#102118");
            SetStatus("session unlocked · keys live in RAM only");
            if (Active is { } tab)
            {
                NavigateToActive(tab.Address, addHistory: false);
            }
        }
        catch (Exception ex)
        {
            SetStatus("unlock failed · " + ex.Message);
            _pinBox.SelectAll();
        }
    }

    private void OpenExternal(string url)
    {
        // The shell will open ANY protocol handler (file:, ms-msdt:, a custom scheme...). Only ever hand it a plain web address.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            SetStatus("refused to open a non-web address in the system browser");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            SetStatus("handed to the system browser · http(s) never renders inside Kickl");
        }
        catch (Exception ex)
        {
            SetStatus("could not open system browser · " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ render

    private void RenderAll(bool resetScroll = false)
    {
        RenderTabs();
        var tab = Active;
        if (tab is not null)
        {
            _address.Text = tab.Address;
            _titleText.Text = tab.Title;
            _pageHost.Child = tab.EditGate is not null
                ? BuildEditGate(tab)
                : tab.Draft is not null
                ? BuildEditor(tab)
                : tab.Error is not null
                    ? BuildError(tab)
                    : BuildPage(tab);
            if (!_session.IsUnlocked)
                _lockOverlay.IsVisible = true;
        }
        if (resetScroll)
            Dispatcher.UIThread.Post(() => _contentScroll.Offset = new Vector(0, 0));
    }

    private void RenderTabs()
    {
        _tabStrip.Children.Clear();
        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var tab = _tabs[i];
            var selected = i == _active;

            var title = T(tab.Title, 10.5, selected ? Text : Muted);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.MaxWidth = 170;

            var open = new Button
            {
                Content = title,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 4, 0),
                Height = 26,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            open.Click += (_, _) => ActivateTab(index);
            AutomationProperties.SetName(open, $"Tab {index + 1}: {tab.Title}{(selected ? ", selected" : "")}");

            var close = new Button
            {
                Content = "×",
                FontSize = 12,
                Width = 20,
                Height = 24,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = C(Muted),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            close.Click += (_, _) => CloseTab(index);
            AutomationProperties.SetName(close, $"Close tab {index + 1}: {tab.Title}");
            ToolTip.SetTip(close, "Close tab");

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(open);
            row.Children.Add(close);
            Grid.SetColumn(close, 1);

            var border = new Border
            {
                Background = C(selected ? Panel2 : Panel),
                BorderBrush = C(selected ? Accent : Line),
                BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 1),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Child = row,
                Margin = new Thickness(0, 0, 0, 0)
            };
            _tabStrip.Children.Add(border);
        }
    }

    private Control BuildPage(BrowserTab tab)
    {
        var showHomeAd = tab.Page is null && tab.Game is null && tab.BuiltIn is null;
        _homeAdOverlay.IsVisible = showHomeAd;
        _homeAdOverlay.IsHitTestVisible = showHomeAd;
        _homeAdOverlay.Children.Clear();
        if (showHomeAd)
            _homeAdOverlay.Children.Add(BuildHomeAd());

        if (tab.Game is not null)
            return BuildGame(tab);
        if (tab.BuiltIn is not null)
            return BuildAdNetwork(tab);
        if (tab.Page is null)
            return BuildHome(tab);

        var page = tab.Page;
        var stack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 940,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(42, 34, 42, 70)
        };

        var addressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        addressRow.Children.Add(Pill("jttp://" + page.Site + "/", Accent, "#0b1b2c"));
        addressRow.Children.Add(Pill(tab.Verified ? "NAME VERIFIED" : "UNVERIFIED NAME",
            tab.Verified ? Green : Amber, tab.Verified ? "#102118" : "#241f0d"));
        if (tab.Ownership?.Verified == true)
        {
            var isTest = tab.Ownership.VerifiedDetail.StartsWith("TEST CA", StringComparison.Ordinal);
            var verified = isTest ? Pill("✓ VERIFIED · TEST CA", Amber, "#241f0d") : Pill("✓ VERIFIED OWNER", Green, "#0d2b1a");
            ToolTip.SetTip(verified, isTest
                ? "Sealed by a throwaway TEST CA. This is not the real Kickl CA."
                : "Ownership of this name is sealed by the Kickl CA and recorded in its transparency log.");
            addressRow.Children.Add(verified);
        }
        else
        {
            addressRow.Children.Add(Pill(tab.Ownership?.Owned == true ? "OWNED TITLE" : "NO PAID TITLE",
                tab.Ownership?.Owned == true ? Green : Muted, tab.Ownership?.Owned == true ? "#102118" : "#141d27"));
        }
        var direct = tab.Transport?.StartsWith("direct", StringComparison.OrdinalIgnoreCase) == true;
        addressRow.Children.Add(Pill(direct ? "DIRECT FROM ORIGIN" : "LOCAL MIRROR",
            direct ? Green : Muted, direct ? "#102118" : "#141d27"));
        if (page.Site.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
            addressRow.Children.Add(Pill("FEED STORY", Accent, "#0b1b2c"));
        var pageSite = page.Site;
        if (tab.Ownership?.Owned != true)
            addressRow.Children.Add(Btn("CLAIM SITE", () => OpenClaim(pageSite), accent: true));
        addressRow.Children.Add(Btn("EDIT", () => NavigateToActive("jttp://" + pageSite + "/edit")));
        stack.Children.Add(addressRow);

        var title = T(page.Title, 30, Text, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(title);

        var byline = string.Join(" · ", new[] { page.Author, page.Published, page.Location }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (byline.Length > 0)
        {
            var line = T(byline, 11, Muted);
            stack.Children.Add(line);
        }

        if (!string.IsNullOrWhiteSpace(page.Headline) &&
            !string.Equals(page.Headline.Trim(), page.Title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var headline = T(page.Headline, 14.5, Muted);
            headline.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(headline);
        }

        if (page.Site.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
        {
            var next = Btn("WRITE NEXT STORY", () => OpenStoryEditor(tab, page.Site), accent: true);
            next.HorizontalAlignment = HorizontalAlignment.Left;
            next.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(next);
        }

        if (page.Tags.Count > 0)
        {
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (var tag in page.Tags)
                tags.Children.Add(Pill("#" + tag, Muted, "#111b26"));
            stack.Children.Add(tags);
        }

        stack.Children.Add(new Border { Height = 1, Background = C(Line), Margin = new Thickness(0, 8, 0, 8) });

        var blocks = page.Blocks.ToList();
        if (blocks.Count > 0 && blocks[0].Kind == BlockKind.Heading &&
            string.Equals(blocks[0].Text?.Trim(), page.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            blocks.RemoveAt(0);
        foreach (var block in blocks)
            AddBlock(stack, tab, block);

        var meta = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        var hash = T("content sha-256 " + Short(tab.ContentHash) + " · verified before parse", 9, Muted, mono: true);
        hash.TextWrapping = TextWrapping.Wrap;
        meta.Children.Add(hash);
        var render = T((tab.Transport ?? "local package") +
                       (string.IsNullOrEmpty(tab.TransportFingerprint) ? "" : " · " + tab.TransportFingerprint), 9, Muted);
        render.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Children.Add(render);
        Grid.SetColumn(render, 1);
        stack.Children.Add(meta);

        return new Border { Child = stack };
    }

    private void AddBlock(StackPanel stack, BrowserTab tab, PageBlock block)
    {
        switch (block.Kind)
        {
            case BlockKind.Heading:
            {
                var size = block.Level switch { 1 => 23, 2 => 18, 3 => 15, _ => 13 };
                var heading = T(block.Text ?? "", size, Text, bold: true);
                heading.TextWrapping = TextWrapping.Wrap;
                heading.Margin = new Thickness(0, block.Level <= 2 ? 14 : 8, 0, 0);
                stack.Children.Add(heading);
                break;
            }
            case BlockKind.Paragraph:
            {
                var text = T(block.Text ?? "", 13.5, "#c5d3e2");
                text.TextWrapping = TextWrapping.Wrap;
                text.LineHeight = 21;
                stack.Children.Add(text);
                break;
            }
            case BlockKind.Code:
                stack.Children.Add(Box(block.Text ?? "", "#7ee787", "#0a121a", mono: true));
                break;
            case BlockKind.Quote:
                stack.Children.Add(new Border
                {
                    BorderBrush = C(Accent),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Child = T(block.Text ?? "", 13, "#a9bdd2")
                });
                break;
            case BlockKind.Divider:
                stack.Children.Add(new Border { Height = 1, Background = C(Line) });
                break;
            case BlockKind.Button:
            {
                var intent = (block.Text ?? "").ToLowerInvariant();
                var button = Btn(block.Text ?? "action", intent.Contains("lock") ? LockSession : () => { });
                button.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(button);
                break;
            }
            case BlockKind.Blob:
                stack.Children.Add(BuildBlob(tab, block));
                break;
            case BlockKind.Image:
                stack.Children.Add(BuildImage(tab, block));
                break;
            case BlockKind.Html:
                stack.Children.Add(BuildHtml(tab, block));
                break;
            case BlockKind.Ad:
                stack.Children.Add(BuildAd(block));
                break;
            case BlockKind.File:
                stack.Children.Add(BuildFile(block));
                break;
            case BlockKind.Game:
                stack.Children.Add(BuildGame(tab, block));
                break;
        }
    }

    private Control BuildBlob(BrowserTab tab, PageBlock block)
    {
        var id = block.Id ?? "blob";
        if (tab.Unlocked.Contains(id) && tab.Texts.TryGetValue(id, out var text))
        {
            var body = T(text, 12.5, "#d8e5f2", mono: true);
            body.TextWrapping = TextWrapping.Wrap;
            return new Border
            {
                Background = C("#0e1a15"),
                BorderBrush = C("#245437"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Child = new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        T($"{id} · UNLOCKED · {block.Mime ?? "text/plain"}", 9.5, Green, bold: true),
                        body
                    }
                }
            };
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                T($"{id} · {(block.Protected ? "PROTECTED BLOB" : "SEALED BLOB")}", 10.5, Amber, bold: true),
                T($"{block.Mime ?? "application/octet-stream"} · {block.Bytes} bytes · AES-256-GCM" +
                  (block.Protected ? " · needs this site's access key" : ""), 9.5, Muted)
            }
        };
        TextBox? accessKey = null;
        if (block.Protected)
        {
            accessKey = Input("", "access key");
            accessKey.PasswordChar = '•';
            accessKey.Margin = new Thickness(0, 6, 0, 0);
            label.Children.Add(accessKey);
        }
        row.Children.Add(label);
        var unlock = Btn("UNLOCK", () => UnlockBlob(tab, id, accessKey?.Text), accent: true);
        unlock.Height = 28;
        row.Children.Add(unlock);
        Grid.SetColumn(unlock, 1);

        return new Border
        {
            Background = C("#12181f"),
            BorderBrush = C("#3a3320"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13),
            Child = row
        };
    }

    private Control BuildImage(BrowserTab tab, PageBlock block)
    {
        var id = block.Id;
        if (id is not null && tab.Images.TryGetValue(id, out var bitmap))
        {
            return new Border
            {
                Background = C("#0b1219"),
                BorderBrush = C(Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new Image
                        {
                            Source = bitmap,
                            Stretch = Stretch.Uniform,
                            MaxHeight = 430,
                            HorizontalAlignment = HorizontalAlignment.Left
                        },
                        T(block.Text ?? "", 9, Muted)
                    }
                }
            };
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                T(id is null ? "REMOTE IMAGE" : $"{id} · SEALED IMAGE", 10.5, Amber, bold: true),
                T(id is null
                    ? "External images are not fetched; a sealed package carries its own image blob."
                    : $"sealed image · {block.Bytes} bytes · AES-256-GCM", 9.5, Muted)
            }
        };
        row.Children.Add(info);
        if (id is not null)
        {
            var unlock = Btn("UNLOCK IMAGE", () => UnlockImage(tab, id), accent: true);
            unlock.Height = 28;
            row.Children.Add(unlock);
            Grid.SetColumn(unlock, 1);
        }

        return new Border
        {
            Background = C("#12181f"),
            BorderBrush = C("#3a3320"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13),
            Child = row
        };
    }

    private Control BuildAd(PageBlock block)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(block.Publisher) || string.IsNullOrWhiteSpace(block.Site)
                || string.IsNullOrWhiteSpace(block.Space) || string.IsNullOrWhiteSpace(block.Token))
                throw new KmlException("The ad block is incomplete.");

            var slot = _session.NicklesSlot(new NicklesPlacement(
                block.Publisher, block.Site, block.Space, block.Token));
            return BuildAdCard(slot);
        }
        catch (Exception ex)
        {
            return new Border
            {
                Background = C("#12181f"),
                BorderBrush = C("#3a3320"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Child = T("AD · unavailable · " + ex.Message, 9.5, Amber, mono: true).WithWrap()
            };
        }
    }

    private Control BuildAdCard(NicklesSlot slot)
    {
        var creative = slot.Creative;
        var stack = new StackPanel { Spacing = 5 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var ad = Pill("AD", "#0b0b0b", "#f2cc60");
        head.Children.Add(ad);
        var label = T("NCKL", 8.5, Muted, mono: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(7, 0, 0, 0);
        head.Children.Add(label);
        Grid.SetColumn(label, 1);
        var bucket = T("bucket " + slot.Bucket, 7.5, Muted, mono: true);
        bucket.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(bucket);
        Grid.SetColumn(bucket, 2);
        stack.Children.Add(head);

        var headline = T(creative.Headline, 15, Text, bold: true);
        headline.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(headline);
        var support = T(creative.Support, 9.5, Muted);
        support.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(support);
        stack.Children.Add(T("AD · publisher " + slot.Signature[..Math.Min(8, slot.Signature.Length)],
            7.5, Muted, mono: true));

        var card = new Border
        {
            Width = 260,
            MinHeight = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = C("#0b1219"),
            BorderBrush = C(creative.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            Child = stack
        };
        ToolTip.SetTip(card, creative.Dest);
        card.PointerPressed += (_, e) =>
        {
            if (e.Source is Button) return;
            PromptAd(creative);
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.Source is Button) return;
            e.Handled = true;
            PromptAd(creative);
        };
        return card;
    }

    private void PromptAd(NicklesCreative creative)
    {
        _pendingSite = null;
        _pendingExternal = creative.Dest;
        _confirmTitle.Text = "AD · LEAVE THE SEALED LANE?";
        _confirmOpen.Content = "OPEN IN SYSTEM BROWSER";
        _confirmText.Text =
            $"{creative.Headline}\n{creative.Support}\n\n{creative.Dest}\n\n" +
            "This NCKL slot measured no impression and stored no click. The link goes straight to " +
            "the advertiser; your IP and normal browser identity are visible to them, not to the sealed session.";
        _confirmOverlay.IsVisible = true;
    }

    private Control BuildHtml(BrowserTab tab, PageBlock block)
    {
        var inner = _sealedSurface.RenderInlineFragment(block.Html ?? "", C(Text), C(Muted), C(Accent), Sans, Mono, 12,
            tab.Visuals, ids => UnlockHtmlVisual(tab, ids));
        return new Border
        {
            Background = C("#0b1219"),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14),
            Child = inner
        };
    }

    private void UnlockImage(BrowserTab tab, string id)
    {
        try
        {
            var offset = _contentScroll.Offset;
            var bytes = _session.UnlockBlobBytes(id);
            try
            {
                using var stream = new MemoryStream(bytes);
                tab.Images[id] = new Bitmap(stream);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            _pageHost.Child = BuildPage(tab);
            Dispatcher.UIThread.Post(() => _contentScroll.Offset = offset);
            SetStatus($"image {id} opened · sealed blob decrypted in memory");
        }
        catch (Exception ex)
        {
            SetStatus("image failed · " + ex.Message);
        }
    }

    private void UnlockHtmlVisual(BrowserTab tab, IReadOnlyList<string> ids)
    {
        try
        {
            var offset = _contentScroll.Offset;
            var doc = _session.Document;
            foreach (var id in ids)
            {
                if (tab.Visuals.ContainsKey(id)) continue;
                if (doc is null || !doc.Blobs.TryGetValue(id, out var blob))
                    throw new KeyNotFoundException($"Blob '{id}' is not part of this document.");
                if (!blob.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Blob '{id}' is not an image.");
                var bytes = _session.UnlockBlobBytes(id);
                tab.Visuals[id] = new HtmlBlockView.UnlockedVisual(blob.Mime, bytes);
            }
            _pageHost.Child = BuildPage(tab);
            Dispatcher.UIThread.Post(() => _contentScroll.Offset = offset);
            SetStatus(ids.Count == 1
                ? $"sealed inline image {ids[0]} opened · decrypted in memory"
                : $"{ids.Count} sealed inline images opened · decrypted in memory");
        }
        catch (Exception ex)
        {
            SetStatus("sealed inline image failed · " + ex.Message);
        }
    }

    private static void ClearImages(BrowserTab tab)
    {
        foreach (var bitmap in tab.Images.Values)
            bitmap.Dispose();
        tab.Images.Clear();
        foreach (var visual in tab.Visuals.Values)
            CryptographicOperations.ZeroMemory(visual.Bytes);
        tab.Visuals.Clear();
    }

    private Control BuildFile(PageBlock block) => new Border
    {
        Background = C("#111b26"),
        BorderBrush = C(Line),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(13),
        Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                T($"{block.Id} · SEALED ATTACHMENT", 10.5, Accent, bold: true),
                T($"{block.Mime ?? "application/octet-stream"} · {block.Bytes} bytes · no download in this slice", 9.5, Muted)
            }
        }
    };

    private void UnlockBlob(BrowserTab tab, string id, string? accessKey = null)
    {
        try
        {
            var offset = _contentScroll.Offset;
            var text = _session.UnlockBlob(id, null, accessKey);
            tab.Unlocked.Add(id);
            tab.Texts[id] = text;
            _pageHost.Child = BuildPage(tab);
            Dispatcher.UIThread.Post(() => _contentScroll.Offset = offset);
            SetStatus($"blob {id} opened · plaintext held in RAM only");
        }
        catch (Exception ex)
        {
            SetStatus("blob failed · " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ story template

    private StoryDraft NewDraft(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        if (normalized.EndsWith(".rss", StringComparison.OrdinalIgnoreCase))
        {
            return new StoryDraft
            {
                Host = normalized,
                Date = date,
                RssXml = RssTemplate.Example(normalized, date)
            };
        }

        // Every new name starts as a complete example page, not a blank editor. The fastest
        // way to learn the sealed-site format is to change a working page.
        return new StoryDraft
        {
            Host = normalized,
            Date = date,
            Author = "you",
            Headline = "Welcome to " + normalized,
            Html = SiteStarter.Example(normalized),
            Tags = ["new", "site"]
        };
    }

    private void OpenStoryEditor(BrowserTab tab, string host)
    {
        tab.ReturnAddress = tab.Page is not null ? tab.Address : HomeUrl;
        tab.EditGate = null;
        tab.Draft = NewDraft(host);
        tab.Page = null;
        tab.Error = null;
        tab.Address = "jttp://" + host + "/";
        tab.Title = tab.Draft.IsRss ? "next story · " + host : "create · " + host;
        tab.Unlocked.Clear();
        tab.Texts.Clear();
        ClearImages(tab);
        RenderAll(resetScroll: true);
        Dispatcher.UIThread.Post(() => _editorFocus?.Focus());
    }

    private Control BuildEditor(BrowserTab tab)
    {
        var draft = tab.Draft!;
        var isExisting = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(_session.Paths), draft.Host) is not null;
        var stack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(42, 30, 42, 60)
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(Pill("jttp://" + draft.Host + "/", Accent, "#0b1b2c"));
        top.Children.Add(Pill(draft.IsRss
                ? "RSS STORY TEMPLATE"
                : isExisting ? "NEW PAGE" : "FREE PAGE · NAME NOT FOUND",
            Amber, "#241f0d"));
        stack.Children.Add(top);

        stack.Children.Add(T(draft.IsRss
            ? "Next story for " + draft.Host
            : (isExisting ? "New page for " : "Create ") + draft.Host, 27, Text, bold: true));
        stack.Children.Add(T(draft.IsRss
            ? "Fill in the RSS XML template. Publishing seals the story, notifies Pinwheel, and puts it in the screen-two feed. Readers who find the tags can fetch this article from your device over the RSA + AES-GCM tunnel."
            : isExisting
                ? "This page already exists. Edit the inline HTML/SVG, headline, or tags and publish a new version."
                : "A complete example page has already been made for this name. Change the drawing, words, headline, and tags to learn how a sealed site works; publish when it feels like yours.", 11.5, Muted).WithWrap());

        if (draft.IsRss)
        {
            draft.RssXml ??= RssTemplate.Example(draft.Host, draft.Date);
            var xml = Input(draft.RssXml, "");
            xml.AcceptsReturn = true;
            xml.TextWrapping = TextWrapping.Wrap;
            xml.FontFamily = Mono;
            xml.FontSize = 11;
            xml.Height = 460;
            xml.TextChanged += (_, _) => draft.RssXml = xml.Text ?? "";
            _editorFocus = xml;
            stack.Children.Add(Field("RSS XML · fill in the one <item>", xml));
            stack.Children.Add(T("The XML is the source of truth for an RSS story. <title>, <description>, <category>, <pubDate>, <location>, and the optional <enclosure url=\"…\"> are read at publish time. Keep it as XML; RSS has no inline SVG/HTML.", 9, Muted).WithWrap());
        }
        else
        {
            var headline = Input(draft.Headline, "headline · one line, this is the page title");
            headline.FontSize = 15;
            headline.Height = 34;
            headline.TextChanged += (_, _) => draft.Headline = headline.Text ?? "";
            _editorFocus = headline;
            stack.Children.Add(Field("HEADLINE", headline));

            var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,180"), ColumnSpacing = 8 };
            var author = Input(draft.Author, "author");
            author.TextChanged += (_, _) => draft.Author = author.Text ?? "";
            var date = Input(draft.Date, "yyyy-MM-dd");
            date.TextChanged += (_, _) => draft.Date = date.Text ?? "";
            var location = Input(draft.Location, "location");
            location.TextChanged += (_, _) => draft.Location = location.Text ?? "";
            meta.Children.Add(Field("AUTHOR", author));
            meta.Children.Add(Field("DATE", date));
            Grid.SetColumn(meta.Children[1], 1);
            meta.Children.Add(Field("LOCATION", location));
            Grid.SetColumn(meta.Children[2], 2);
            stack.Children.Add(meta);

            var image = Input(draft.ImagePath ?? "", "image path · PNG/JPEG/GIF/WebP/SVG");
            image.TextChanged += (_, _) => draft.ImagePath = string.IsNullOrWhiteSpace(image.Text) ? null : image.Text;
            var browse = Btn("BROWSE", () => _ = BrowseImage(draft, image));
            browse.Height = 32;
            var imageRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            imageRow.Children.Add(image);
            imageRow.Children.Add(browse);
            Grid.SetColumn(browse, 1);
            stack.Children.Add(Field("IMAGE · sealed attachment", imageRow));

            if (!string.IsNullOrWhiteSpace(draft.ImagePath) && File.Exists(draft.ImagePath))
            {
                try
                {
                    var preview = new Image
                    {
                        Source = new Bitmap(draft.ImagePath),
                        Stretch = Stretch.Uniform,
                        MaxHeight = 190,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    stack.Children.Add(new Border
                    {
                        Background = C("#0b1219"),
                        BorderBrush = C(Line),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8),
                        Child = preview
                    });
                }
                catch (Exception)
                {
                    stack.Children.Add(T("The selected image could not be previewed (unsupported format).", 9.5, Amber));
                }
            }

            var visual = Input(draft.Html ?? "",
                "<div>…</div> or <svg xmlns=\"http://www.w3.org/2000/svg\">…</svg>");
            visual.AcceptsReturn = true;
            visual.TextWrapping = TextWrapping.Wrap;
            visual.FontFamily = Mono;
            visual.FontSize = 11;
            visual.Height = 320;

            var pagePreview = new ContentControl();
            void RenderPreview()
            {
                pagePreview.Content = _sealedSurface.RenderInlineFragment(draft.Html ?? "", C(Text), C(Muted), C(Accent),
                    Sans, Mono, 12);
            }

            visual.TextChanged += (_, _) =>
            {
                draft.Html = string.IsNullOrWhiteSpace(visual.Text) ? null : visual.Text;
                RenderPreview();
            };
            RenderPreview();

            stack.Children.Add(Field("PAGE · INLINE HTML / SVG · no CSS, no scripts, no external requests", visual));
            stack.Children.Add(Field("PREVIEW · change the code and this updates", new Border
            {
                Background = C("#0b1219"),
                BorderBrush = C(Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Child = pagePreview
            }));
            stack.Children.Add(T("A working example is already here: change the drawing and text to learn the format. Pasted data-URL images are extracted and sealed; a sealed image shows an unlock button until clicked. Scripts, forms, event handlers, stylesheets, and remote URLs are removed before rendering.", 9, Muted).WithWrap());

            var tags = Input(string.Join(", ", draft.Tags), "tags · comma separated, at least one");
            tags.TextChanged += (_, _) => draft.Tags = SplitTags(tags.Text);
            stack.Children.Add(Field("TAGS", tags));

            var body = Input(draft.Body, "optional text below the page");
            body.AcceptsReturn = true;
            body.TextWrapping = TextWrapping.Wrap;
            body.FontFamily = Mono;
            body.FontSize = 12;
            body.Height = 150;
            body.TextChanged += (_, _) => draft.Body = body.Text ?? "";
            stack.Children.Add(Field("TEXT · optional", body));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var publish = Btn(draft.IsRss ? "PUBLISH STORY" : "CREATE NAME", () => PublishDraft(tab), accent: true);
        publish.Height = 34;
        var cancel = Btn("CANCEL", () =>
        {
            var returnTo = tab.ReturnAddress;
            tab.Draft = null;
            NavigateToActive(returnTo, addHistory: false);
        });
        cancel.Height = 34;
        actions.Children.Add(publish);
        actions.Children.Add(cancel);
        stack.Children.Add(actions);

        stack.Children.Add(T(draft.IsRss
            ? "rss xml: item title · author · pubDate · location · description · category · enclosure"
            : "example page: headline · tags · inline HTML/SVG · optional text and image", 9, Muted, mono: true));

        return new Border { Child = stack };
    }

    private void PublishDraft(BrowserTab tab)
    {
        var draft = tab.Draft;
        if (draft is null) return;

        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            SetStatus("unlock the profile before publishing");
            return;
        }

        try
        {
            _session.CreateSite(draft);
            var host = draft.Host.Trim().ToLowerInvariant();
            tab.Draft = null;
            NavigateToActive("jttp://" + host + "/");
            RefreshRegistryNews();
            var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(_session.Paths), host);
            SetStatus($"published jttp://{host}/ · Pinwheel registry v{record?.Version ?? 0} · {_session.LastTransport}");
        }
        catch (Exception ex)
        {
            SetStatus("publish failed · " + ex.Message);
            // Keep the editor and its field values visible; re-render only the status line.
            tab.Error = null;
            RenderAll(resetScroll: false);
        }
    }

    private async Task BrowseImage(StoryDraft draft, TextBox box)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the story image",
            AllowMultiple = false
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        draft.ImagePath = path;
        box.Text = path;
        RenderAll(resetScroll: false);
    }

    private static string[] SplitTags(string? raw) =>
        (raw ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#').ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static TextBox Input(string? text, string watermark)
    {
        var box = new TextBox();
        StyleInput(box, watermark);
        box.Text = text ?? "";
        box.FontSize = 12;
        return box;
    }

    private static Control Field(string label, Control control) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            T(label, 8.5, Muted, bold: true),
            control
        }
    };

    private Control BuildError(BrowserTab tab)
    {
        var stack = new StackPanel
        {
            Spacing = 13,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(42, 54, 42, 42)
        };
        var attack = tab.Error?.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase) == true
                     || tab.Error?.Contains("attack counter", StringComparison.OrdinalIgnoreCase) == true;
        stack.Children.Add(T(attack ? "CONTENT INTEGRITY FAILURE" : "PAGE COULD NOT OPEN", 21,
            attack ? Red : Amber, bold: true));
        stack.Children.Add(T(tab.Error ?? "unknown error", 13, Text).WithWrap());
        stack.Children.Add(T(attack
            ? "The package failed the registry SHA-256 check. Nothing was parsed or rendered. The local attack counter was incremented."
            : "The name may not exist in the phonebook yet, or its sealed package is not in this profile.", 11, Muted).WithWrap());
        var retry = Btn("RETRY", () => NavigateToActive(tab.Address), accent: true);
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(retry);
        return new Border { Child = stack };
    }

    // ------------------------------------------------------------------ home: arcade + PFS search

    private Control BuildHome(BrowserTab tab)
    {
        var stack = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 1180,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(34, 28, 34, 60)
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(Pill("NO ADDRESS OPEN", Accent, "#0b1b2c"));
        top.Children.Add(Pill("LOCAL START PAGE", Muted, "#141d27"));
        top.Children.Add(Pill("PFS SEARCH", Green, "#102118"));
        stack.Children.Add(top);

        var title = T("Games on one side. The phonebook on the other.", 27, Text, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(title);
        stack.Children.Add(T(
            "The first half is the games array. The second half searches the live index by the tags you send, over a fresh PFS lookup; hits stay inert until you confirm one.",
            11.5, Muted).WithWrap());

        var split = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 16
        };
        split.Children.Add(HomePane(BuildArcadePane()));
        var search = HomePane(BuildHomeSearchPane(tab));
        split.Children.Add(search);
        Grid.SetColumn(search, 1);
        stack.Children.Add(split);
        return new Border { Child = stack };
    }

    private Control BuildHomeAd()
    {
        _homeAdBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600;
        try
        {
            if (!_session.IsUnlocked)
                return T("AD  ·  unavailable while locked", 8.5, Muted, mono: true);

            var slot = _session.NicklesHomeSlot();
            _homeAdBucket = slot.Bucket;
            if (!_homeAdCollapsed) return BuildHomeAdBar(slot);

            var restore = Btn("AD  ·  SHOW SLOT", () => SetHomeAdCollapsed(false));
            restore.Height = 22;
            restore.FontSize = 8.5;
            restore.Foreground = C("#f2cc60");
            restore.HorizontalAlignment = HorizontalAlignment.Left;
            return restore;
        }
        catch (Exception ex)
        {
            return new Border
            {
                Background = C("#0b1219"),
                BorderBrush = C(Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = T("AD  ·  unavailable · " + ex.Message, 8.5, Amber, mono: true).WithWrap()
            };
        }
    }

    /// <summary>The home slot spans the page footer. One line, always labelled, never overlays content.</summary>
    private Control BuildHomeAdBar(NicklesSlot slot)
    {
        var creative = slot.Creative;

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        left.Children.Add(Pill("AD", "#0b0b0b", "#f2cc60"));
        left.Children.Add(T("NCKL", 8.5, Muted, mono: true));

        var middle = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var headline = T(creative.Headline, 13, Text, bold: true);
        headline.TextWrapping = TextWrapping.Wrap;
        middle.Children.Add(headline);
        var support = T(creative.Support, 9.5, Muted);
        support.TextWrapping = TextWrapping.Wrap;
        middle.Children.Add(support);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        right.Children.Add(T("bucket " + slot.Bucket, 7.5, Muted, mono: true));
        right.Children.Add(T("publisher " + slot.Signature[..Math.Min(8, slot.Signature.Length)],
            7.5, Muted, mono: true));
        var dismiss = Btn("×", () => SetHomeAdCollapsed(true));
        dismiss.Width = 22;
        dismiss.Height = 22;
        dismiss.FontSize = 11;
        ToolTip.SetTip(dismiss, "Collapse this ad slot");
        right.Children.Add(dismiss);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12
        };
        row.Children.Add(left);
        row.Children.Add(middle);
        Grid.SetColumn(middle, 1);
        row.Children.Add(right);
        Grid.SetColumn(right, 2);

        var card = new Border
        {
            Background = C("#0b1219"),
            BorderBrush = C(creative.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = true,
            Child = row
        };
        ToolTip.SetTip(card, creative.Dest);
        card.PointerPressed += (_, e) =>
        {
            if (e.Source is Button) return;
            PromptAd(creative);
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.Source is Button) return;
            e.Handled = true;
            PromptAd(creative);
        };
        return card;
    }

    private bool LoadHomeAdPreference()
    {
        try
        {
            if (!_session.IsUnlocked) return false;
            var path = HomeAdPreferencePath;
            if (File.Exists(path))
            {
                var plaintext = _session.OpenProfileData("home-ad-pref", File.ReadAllBytes(path));
                try { return Encoding.UTF8.GetString(plaintext) == "collapsed"; }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }

            if (_session.Paths.ProfileId != "local" || !File.Exists(LegacyHomeAdPreferencePath)) return false;
            var legacyValue = File.ReadAllText(LegacyHomeAdPreferencePath).Trim();
            SaveHomeAdPreference(legacyValue == "collapsed");
            File.Delete(LegacyHomeAdPreferencePath);
            return legacyValue == "collapsed";
        }
        catch (Exception) { return false; }
    }

    private void SetHomeAdCollapsed(bool collapsed)
    {
        _homeAdCollapsed = collapsed;
        try
        {
            SaveHomeAdPreference(collapsed);
        }
        catch (IOException ex) { SetStatus("home ad preference could not be saved · " + ex.Message); }
        catch (UnauthorizedAccessException ex) { SetStatus("home ad preference could not be saved · " + ex.Message); }

        if (Active is { Page: null, Game: null } tab)
            _pageHost.Child = BuildPage(tab);
    }

    private string HomeAdPreferencePath => Path.Combine(_session.Paths.ProfileRoot, "private", "home-ad-pref.kpf");
    private string LegacyHomeAdPreferencePath => Path.Combine(_dataRoot, "shared", "nckl-home-slot.txt");

    private void SaveHomeAdPreference(bool collapsed)
    {
        if (!_session.IsUnlocked) return;
        var path = HomeAdPreferencePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plaintext = Encoding.UTF8.GetBytes(collapsed ? "collapsed" : "expanded");
        var encrypted = _session.ProtectProfileData("home-ad-pref", plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static Control HomePane(Control child) => new Border
    {
        Background = C("#0b1219"),
        BorderBrush = C(Line),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16),
        Child = child
    };

    private Control BuildArcadePane()
    {
        var panel = new StackPanel { Spacing = 9 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(T("ARCADE", 12, Accent, mono: true, bold: true));
        var count = T($"{HomeGames.Length} games", 9, Muted, mono: true);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(count);
        Grid.SetColumn(count, 1);
        panel.Children.Add(header);
        panel.Children.Add(T("THE GAMES ARRAY", 20, Text, bold: true));
        panel.Children.Add(T(
            "Signed game bundles will run in the separate wasm-1 sandbox. The first native demo is playable now.",
            10, Muted).WithWrap());

        var cards = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 196,
            ItemHeight = 118
        };
        foreach (var game in HomeGames)
            cards.Children.Add(GameCard(game));
        panel.Children.Add(cards);

        var featured = Btn("OPEN FEATURED · jttp://pinwheelexplained.com/", () => NewTab(HomeUrl));
        featured.HorizontalAlignment = HorizontalAlignment.Left;
        featured.Height = 30;
        featured.FontSize = 9.5;
        panel.Children.Add(featured);
        return panel;
    }

    private Control GameCard(HomeGame game)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(T(game.Title, 13, Text, bold: true));
        panel.Children.Add(T(game.Tagline, 9.5, Muted).WithWrap());
        panel.Children.Add(T(game.Meta, 8.5, game.Accent, mono: true));
        panel.Children.Add(T("@" + game.Author, 8.5, Muted, mono: true));

        var card = new Border
        {
            Width = 188,
            Height = 110,
            Margin = new Thickness(0, 0, 8, 8),
            Background = C("#111b26"),
            BorderBrush = C(game.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = panel
        };
        card.PointerPressed += (_, _) =>
        {
            var tab = Active;
            if (tab is null) return;
            if (game.Playable)
                OpenGame(tab, game.Id);
            else
                SetStatus(game.Title + " · queued for the sandboxed wasm-1 arcade runtime");
        };
        return card;
    }

    private Control BuildHomeSearchPane(BrowserTab tab)
    {
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(T("LEGENDDX", 12, Accent, mono: true, bold: true));
        panel.Children.Add(T("PFS TAG SEARCH", 20, Text, bold: true));
        panel.Children.Add(T(
            "Send tags for perusal. A fresh P-256 ECDH session seals the query and the result list; the registry sees neither.",
            10, Muted).WithWrap());

        var search = Input(tab.HomeQuery, "tags · e.g. svg, news, demo");
        _homeSearchBox = search;
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            RunHomeSearch(tab, search.Text ?? "");
        };
        var go = Btn("SEARCH", () => RunHomeSearch(tab, search.Text ?? ""), accent: true);
        go.Height = 32;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(search);
        row.Children.Add(go);
        Grid.SetColumn(go, 1);
        panel.Children.Add(row);

        var quick = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tag in new[] { "svg", "news", "sealed", "demo" })
        {
            var chip = Btn("#" + tag, () => RunHomeSearch(tab, tag));
            chip.Height = 24;
            chip.FontSize = 9;
            chip.Margin = new Thickness(0, 0, 5, 5);
            quick.Children.Add(chip);
        }
        panel.Children.Add(quick);

        var hint = T(tab.HomeHint, 9, Muted, mono: true);
        hint.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(hint);

        var results = new StackPanel { Spacing = 6 };
        foreach (var hit in tab.HomeResults)
            results.Children.Add(SearchHitCard(hit));
        panel.Children.Add(results);
        return panel;
    }

    private void RunHomeSearch(BrowserTab tab, string query)
    {
        query = (query ?? "").Trim();
        tab.HomeQuery = query;
        tab.HomeResults.Clear();

        if (query.Length == 0)
        {
            tab.HomeHint = DefaultHomeHint;
            _pageHost.Child = BuildPage(tab);
            Dispatcher.UIThread.Post(() => _homeSearchBox?.Focus());
            return;
        }

        try
        {
            var result = _session.Search(query);
            tab.HomeResults.AddRange(result.Results);
            tab.HomeHint = result.Results.Count == 0
                ? $"No active site carries “{query}”. Tags are required for search."
                : $"{result.Results.Count} hit(s) · tag matches rank first · nothing opens until confirmed";
            SetStatus($"PFS tag lookup · “{query}” · {result.Results.Count} hit(s)");
        }
        catch (Exception ex)
        {
            tab.HomeHint = "search failed · " + ex.Message;
            SetStatus("PFS tag lookup failed · " + ex.Message);
        }

        _pageHost.Child = BuildPage(tab);
        Dispatcher.UIThread.Post(() =>
        {
            _homeSearchBox?.Focus();
            _homeSearchBox?.SelectAll();
        });
    }

    private Control SearchHitCard(SiteSummary hit)
    {
        var captured = hit;
        var card = new Border
        {
            Background = C("#111b26"),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10)
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(T($"jttp://{hit.Host}/ · {hit.Title}", 11, Text, bold: true));
        info.Children.Add(T(hit.Headline, 10, Muted).WithWrap());
        info.Children.Add(T("updated " + Stamp(hit.UpdatedUtc), 8.5, Muted, mono: true));
        info.Children.Add(T("tags: " + string.Join(" · ", hit.Tags), 9, Accent, mono: true));
        row.Children.Add(info);
        var open = Btn("OPEN", () => PromptSite(captured), accent: true);
        open.Height = 27;
        row.Children.Add(open);
        Grid.SetColumn(open, 1);
        card.Child = row;
        return card;
    }

    // ------------------------------------------------------------------ NCKL publisher console

    private Control BuildAdNetwork(BrowserTab tab)
    {
        var stack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(40, 28, 40, 60)
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(Pill("SECUPGRADE.COM", Accent, "#0b1b2c"));
        top.Children.Add(Pill("NCKL", "#0b0b0b", "#f2cc60"));
        top.Children.Add(Pill("NO TRACKING", Green, "#102118"));
        stack.Children.Add(top);
        stack.Children.Add(T("AD NETWORKING", 28, Text, bold: true));
        stack.Children.Add(T(
            "A webmaster can opt a sealed site into NCKL. Mint a site-bound loader, paste its snippet into an ad-space div, and publish. One labelled slot; no bids, cookies, or beacons.",
            11.5, Muted).WithWrap());

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var overview = Btn("OVERVIEW", () => NavigateToActive(AdNetworkUrl),
            accent: tab.BuiltIn == "overview");
        overview.Height = 30;
        nav.Children.Add(overview);
        var publisher = Btn("PUBLISHER", () => NavigateToActive(AdPublisherUrl),
            accent: tab.BuiltIn == "publisher");
        publisher.Height = 30;
        nav.Children.Add(publisher);
        stack.Children.Add(nav);

        stack.Children.Add(tab.BuiltIn == "publisher"
            ? BuildPublisherConsole(tab)
            : BuildAdNetworkOverview());
        return new Border { Child = stack };
    }

    private Control BuildAdNetworkOverview()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.25*,1*"),
            ColumnSpacing = 14
        };
        var flow = HomePane(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                T("PUBLISHER FLOW", 11, Accent, mono: true, bold: true),
                T("1 · open the publisher", 13, Text, bold: true),
                T("Enter the jttp host you control. The token is bound to that host and one ad space.", 10.5, Muted).WithWrap(),
                T("2 · mint the loader", 13, Text, bold: true),
                T("You get a publisher id, a signed token, and nickles.js. The token cannot be moved to another site.", 10.5, Muted).WithWrap(),
                T("3 · paste the snippet", 13, Text, bold: true),
                T("Put the ad-space div where the ad should appear and the loader immediately after it, inside your inline HTML.", 10.5, Muted).WithWrap(),
                T("4 · publish", 13, Text, bold: true),
                T("Kickl validates the signature, stores one <ad> block, and mounts a native slot. A mismatch fails the publish.", 10.5, Muted).WithWrap(),
                T("5 · rotate locally", 13, Text, bold: true),
                T("The creative changes on a 10-minute bucket, identical for every reader. No impression is measured.", 10.5, Muted).WithWrap(),
                Btn("OPEN PUBLISHER", () => NavigateToActive(AdPublisherUrl), accent: true)
            }
        });
        grid.Children.Add(flow);

        var spec = HomePane(new StackPanel
        {
            Spacing = 7,
            Children =
            {
                T("SLOT SPEC", 11, Accent, mono: true, bold: true),
                T("site.inline", 20, Text, bold: true),
                T("One slot per page. 240 × 140 creative inside a 260 × 150 card.", 10.5, Muted).WithWrap(),
                T("label", 9, Green, mono: true, bold: true),
                T("AD is always visible and supplied by the client, never the creative.", 10.5, Muted).WithWrap(),
                T("script policy", 9, Green, mono: true, bold: true),
                T("Sealed pages have no script engine. nickles.js is a signed placement manifest that the publisher recognises; it is never executed or fetched at render time.", 10.5, Muted).WithWrap(),
                T("privacy", 9, Green, mono: true, bold: true),
                T("No impression, click, cookie, device id, geo, or behavioural data leaves the device.", 10.5, Muted).WithWrap(),
                T("house copy", 9, Green, mono: true, bold: true),
                T("The slice rotates a small signed catalog so the flow is testable without a live ad server.", 10.5, Muted).WithWrap()
            }
        });
        grid.Children.Add(spec);
        Grid.SetColumn(spec, 1);
        return grid;
    }

    private Control BuildPublisherConsole(BrowserTab tab)
    {
        var panel = new StackPanel { Spacing = 10 };

        if (string.IsNullOrWhiteSpace(tab.PublisherSite))
            tab.PublisherSite = SuggestedPublisherSite();
        if (string.IsNullOrWhiteSpace(tab.PublisherSpace))
            tab.PublisherSpace = Nickles.DefaultSpace;
        if (tab.PublisherResult is null)
            tab.PublisherResult = _session.NicklesPublishers()
                .FirstOrDefault(p => string.Equals(p.Site, tab.PublisherSite, StringComparison.OrdinalIgnoreCase));

        var site = Input(tab.PublisherSite, "your-name.custom or your-name.com");
        var space = Input(tab.PublisherSpace, Nickles.DefaultSpace);
        var siteField = Field("SITE HOST", site);
        var spaceField = Field("AD SPACE", space);
        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10
        };
        fields.Children.Add(siteField);
        fields.Children.Add(spaceField);
        Grid.SetColumn(spaceField, 1);
        panel.Children.Add(fields);

        var mint = Btn("MINT NCKL.JS", () =>
        {
            tab.PublisherSite = site.Text ?? "";
            tab.PublisherSpace = space.Text ?? "";
            try
            {
                var p = _session.CreateNicklesPublisher(tab.PublisherSite, tab.PublisherSpace);
                tab.PublisherResult = p;
                tab.PublisherSite = p.Site;
                tab.PublisherSpace = p.Space;
                tab.PublisherNote = "minted · the token is bound to " + p.Site + " / " + p.Space;
                SetStatus("NCKL publisher minted · " + p.Id + " · " + p.Site);
            }
            catch (Exception ex)
            {
                tab.PublisherNote = "could not mint · " + ex.Message;
                SetStatus("NCKL publisher failed · " + ex.Message);
            }
            _pageHost.Child = BuildPage(tab);
        }, accent: true);
        mint.HorizontalAlignment = HorizontalAlignment.Left;
        mint.Height = 32;
        panel.Children.Add(mint);

        if (tab.PublisherNote.Length > 0)
        {
            var note = T(tab.PublisherNote, 10, tab.PublisherNote.StartsWith("could not", StringComparison.OrdinalIgnoreCase)
                ? Red
                : Green, mono: true);
            note.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(note);
        }

        if (tab.PublisherResult is { } p)
            panel.Children.Add(BuildPublisherResult(p));

        var existing = _session.NicklesPublishers();
        if (existing.Count > 0)
        {
            var list = new StackPanel { Spacing = 5 };
            list.Children.Add(T("MINTED IN THIS PROFILE", 9, Muted, mono: true, bold: true));
            foreach (var item in existing.OrderByDescending(x => x.CreatedUtc).Take(8))
            {
                var captured = item;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var info = new StackPanel { Spacing = 1 };
                info.Children.Add(T($"{item.Site} · {item.Space}", 10.5, Text, bold: true));
                info.Children.Add(T(item.Id + " · " + Stamp(item.CreatedUtc), 8.5, Muted, mono: true));
                row.Children.Add(info);
                var load = Btn("LOAD", () =>
                {
                    tab.PublisherResult = captured;
                    tab.PublisherSite = captured.Site;
                    tab.PublisherSpace = captured.Space;
                    tab.PublisherNote = "loaded existing placement · " + captured.Id;
                    _pageHost.Child = BuildPage(tab);
                });
                load.Height = 25;
                row.Children.Add(load);
                Grid.SetColumn(load, 1);
                list.Children.Add(row);
            }
            panel.Children.Add(list);
        }

        return HomePane(panel);
    }

    private Control BuildPublisherResult(NicklesPublisher publisher)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(T("SIGNED PLACEMENT", 10, Green, mono: true, bold: true));
        panel.Children.Add(T(publisher.Id + " · " + publisher.Site + " · " + publisher.Space,
            12, Text, bold: true));
        panel.Children.Add(T("token " + publisher.Token, 8.5, Muted, mono: true).WithWrap());

        var snippet = Nickles.Snippet(publisher);
        panel.Children.Add(T("1 · PASTE THIS INTO THE AD-SPACE DIV IN YOUR INLINE HTML", 9, Accent, mono: true, bold: true));
        var snippetBox = new TextBox
        {
            Text = snippet,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Mono,
            FontSize = 10,
            Height = 132,
            Background = C("#080d13"),
            Foreground = C("#c5d3e2"),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        panel.Children.Add(snippetBox);

        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var copySnippet = Btn("COPY SNIPPET", () => CopyText(snippet, "NCKL snippet"));
        copySnippet.Height = 28;
        copyRow.Children.Add(copySnippet);
        var copyJs = Btn("COPY NCKL.JS", () => CopyText(Nickles.LoaderJs(publisher), "NCKL loader"));
        copyJs.Height = 28;
        copyRow.Children.Add(copyJs);
        var saveJs = Btn("SAVE NCKL.JS TO DOWNLOADS", () => SaveNicklesJs(publisher), accent: true);
        saveJs.Height = 28;
        copyRow.Children.Add(saveJs);
        panel.Children.Add(copyRow);

        var js = new TextBox
        {
            Text = Nickles.LoaderJs(publisher),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Mono,
            FontSize = 9.5,
            Height = 104,
            Background = C("#080d13"),
            Foreground = C("#a9bdd2"),
            BorderBrush = C(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        panel.Children.Add(T("2 · THE JS FILE IS A MANIFEST, NOT A SCRIPT", 9, Accent, mono: true, bold: true));
        panel.Children.Add(js);
        panel.Children.Add(T(
            "The loader is never executed or fetched inside a sealed page. At publish the signature is checked against this host and space, then the loader is replaced with a native <ad> block.",
            10, Muted).WithWrap());
        return new Border
        {
            Background = C("#0b1219"),
            BorderBrush = C("#245437"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(13),
            Child = panel
        };
    }

    private string SuggestedPublisherSite()
    {
        var pageTab = _tabs.LastOrDefault(t => t.Page is not null);
        if (pageTab?.Page is { } page) return page.Site;
        var existing = _session.NicklesPublishers()
            .OrderByDescending(p => p.CreatedUtc)
            .FirstOrDefault();
        return existing?.Site ?? "demo.custom";
    }

    private async void CopyText(string text, string what)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                SetStatus("clipboard unavailable in this window");
                return;
            }
            await clipboard.SetTextAsync(text);
            SetStatus(what + " copied to clipboard");
        }
        catch (Exception ex)
        {
            SetStatus("copy failed · " + ex.Message);
        }
    }

    private void SaveNicklesJs(NicklesPublisher publisher)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"nickles-{publisher.Id}.js");
            File.WriteAllText(path, Nickles.LoaderJs(publisher));
            SetStatus("saved NCKL loader · " + path);
        }
        catch (Exception ex)
        {
            SetStatus("could not save NCKL loader · " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ native arcade demo

    private Control BuildGame(BrowserTab tab)
    {
        var stack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(42, 30, 42, 60)
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(Pill("ARCADE · NATIVE DEMO", Accent, "#0b1b2c"));
        top.Children.Add(Pill("NO NETWORK · NO SCRIPT", Green, "#102118"));
        stack.Children.Add(top);

        var back = Btn("BACK TO HOME", () => OpenHome(tab, resetSearch: false));
        back.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(back);

        if (tab.Game == "memory")
            return BuildMemoryGame(tab, stack);

        if (tab.Game != "tic-tac-toe")
        {
            stack.Children.Add(T((tab.Game ?? "game").ToUpperInvariant(), 27, Text, bold: true));
            stack.Children.Add(T("This game is queued for the sandboxed wasm-1 arcade runtime.", 12, Muted).WithWrap());
            return new Border { Child = stack };
        }

        stack.Children.Add(T("TIC-TAC-TOE", 30, Text, bold: true));
        stack.Children.Add(T(tab.TttMessage, 12, tab.TttMessage.Contains("win", StringComparison.OrdinalIgnoreCase)
            ? Green
            : Muted).WithWrap());

        var board = tab.TttBoard ??= new char[9];
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("84,84,84"),
            RowDefinitions = new RowDefinitions("84,84,84"),
            ColumnSpacing = 6,
            RowSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        for (var i = 0; i < 9; i++)
        {
            var index = i;
            var mark = board[i] == '\0' ? "·" : board[i].ToString();
            var cell = new Button
            {
                Content = mark,
                Width = 84,
                Height = 84,
                FontSize = 30,
                FontWeight = FontWeight.Bold,
                Background = C("#111b26"),
                Foreground = board[i] == 'X' ? C(Accent) : board[i] == 'O' ? C("#ff7a64") : C(Muted),
                BorderBrush = C(Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7)
            };
            cell.Click += (_, _) => TicTacToeMove(tab, index);
            grid.Children.Add(cell);
            Grid.SetRow(cell, i / 3);
            Grid.SetColumn(cell, i % 3);
        }
        stack.Children.Add(grid);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var again = Btn("PLAY AGAIN", () =>
        {
            tab.TttBoard = new char[9];
            tab.TttMessage = "Your turn.";
            RenderAll();
        }, accent: true);
        again.Height = 32;
        actions.Children.Add(again);
        stack.Children.Add(actions);
        return new Border { Child = stack };
    }

    private Control BuildMemoryGame(BrowserTab tab, StackPanel stack)
    {
        var deck = tab.MemoryDeck ??= CreateMemoryDeck();
        tab.MemoryFaceUp ??= new bool[deck.Length];
        tab.MemoryMatched ??= new bool[deck.Length];
        var pairs = tab.MemoryMatched.Count(matched => matched) / 2;
        var complete = pairs == deck.Length / 2;
        stack.Children.Add(T("MEMORY MATCH", 27, Text, bold: true));
        stack.Children.Add(T(complete
            ? $"All pairs found in {tab.MemoryMoves} moves."
            : $"Find all {deck.Length / 2} pairs · {tab.MemoryMoves} move(s) · {pairs}/{deck.Length / 2} matched",
            12, complete ? Green : Muted).WithWrap());

        var symbols = new[] { "◆", "●", "▲", "■", "★", "♥", "☀", "☂" };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("76,76,76,76"),
            RowDefinitions = new RowDefinitions("76,76,76,76"),
            ColumnSpacing = 7,
            RowSpacing = 7,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        for (var i = 0; i < deck.Length; i++)
        {
            var index = i;
            var open = tab.MemoryFaceUp[index] || tab.MemoryMatched[index];
            var card = new Button
            {
                Content = open ? symbols[deck[index]] : "?",
                Width = 76,
                Height = 76,
                FontSize = open ? 28 : 22,
                FontWeight = FontWeight.Bold,
                Background = C(tab.MemoryMatched[index] ? "#102118" : open ? "#132b44" : "#111b26"),
                Foreground = C(tab.MemoryMatched[index] ? Green : open ? Accent : Muted),
                BorderBrush = C(tab.MemoryMatched[index] ? Green : Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                IsEnabled = !complete && !tab.MemoryResolving && !open
            };
            card.Click += (_, _) => FlipMemoryCard(tab, index);
            grid.Children.Add(card);
            Grid.SetRow(card, i / 4);
            Grid.SetColumn(card, i % 4);
        }
        stack.Children.Add(grid);
        var again = Btn(complete ? "PLAY AGAIN" : "RESTART", () =>
        {
            ResetMemoryGame(tab);
            RenderAll();
        }, accent: true);
        again.Height = 32;
        stack.Children.Add(again);
        return new Border { Child = stack };
    }

    private static int[] CreateMemoryDeck()
    {
        var deck = Enumerable.Range(0, 8).SelectMany(value => new[] { value, value }).ToArray();
        for (var i = deck.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }

    private static void ResetMemoryGame(BrowserTab tab)
    {
        tab.MemoryDeck = CreateMemoryDeck();
        tab.MemoryFaceUp = new bool[tab.MemoryDeck.Length];
        tab.MemoryMatched = new bool[tab.MemoryDeck.Length];
        tab.MemoryFirstCard = -1;
        tab.MemoryMoves = 0;
        tab.MemoryResolving = false;
    }

    private void FlipMemoryCard(BrowserTab tab, int index)
    {
        if (tab.MemoryDeck is null || tab.MemoryFaceUp is null || tab.MemoryMatched is null ||
            tab.MemoryResolving || tab.MemoryMatched[index] || tab.MemoryFaceUp[index]) return;
        tab.MemoryFaceUp[index] = true;
        if (tab.MemoryFirstCard < 0)
        {
            tab.MemoryFirstCard = index;
            RenderAll();
            return;
        }

        var first = tab.MemoryFirstCard;
        tab.MemoryMoves++;
        if (tab.MemoryDeck[first] == tab.MemoryDeck[index])
        {
            tab.MemoryMatched[first] = true;
            tab.MemoryMatched[index] = true;
            tab.MemoryFirstCard = -1;
            RenderAll();
            return;
        }

        tab.MemoryResolving = true;
        RenderAll();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        EventHandler? onTick = null;
        onTick = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= onTick;
            if (tab.MemoryFaceUp is { Length: > 0 } faceUp)
            {
                faceUp[first] = false;
                faceUp[index] = false;
            }
            tab.MemoryFirstCard = -1;
            tab.MemoryResolving = false;
            RenderAll();
        };
        timer.Tick += onTick;
        timer.Start();
    }

    private void TicTacToeMove(BrowserTab tab, int index)
    {
        var board = tab.TttBoard ??= new char[9];
        if (board[index] != '\0' || TicTacToeWinner(board) != '\0' || board.All(c => c != '\0'))
            return;

        board[index] = 'X';
        if (TicTacToeWinner(board) == 'X')
        {
            tab.TttMessage = "You win.";
        }
        else if (board.All(c => c != '\0'))
        {
            tab.TttMessage = "Draw.";
        }
        else
        {
            var pick = FindWinningMove(board, 'O')
                       ?? FindWinningMove(board, 'X')
                       ?? (board[4] == '\0' ? 4 : Array.FindIndex(board, c => c == '\0'));
            if (pick >= 0) board[pick] = 'O';

            tab.TttMessage = TicTacToeWinner(board) == 'O'
                ? "CPU wins."
                : board.All(c => c != '\0') ? "Draw." : "Your turn.";
        }

        RenderAll();
    }

    private static int? FindWinningMove(char[] board, char mark)
    {
        for (var i = 0; i < board.Length; i++)
        {
            if (board[i] != '\0') continue;
            board[i] = mark;
            var wins = TicTacToeWinner(board) == mark;
            board[i] = '\0';
            if (wins) return i;
        }
        return null;
    }

    private static char TicTacToeWinner(char[] board)
    {
        int[][] lines =
        [
            [0, 1, 2], [3, 4, 5], [6, 7, 8],
            [0, 3, 6], [1, 4, 7], [2, 5, 8],
            [0, 4, 8], [2, 4, 6]
        ];
        foreach (var line in lines)
        {
            var a = board[line[0]];
            if (a != '\0' && a == board[line[1]] && a == board[line[2]])
                return a;
        }
        return '\0';
    }

    private Control BuildEmpty(string title, string detail)
    {
        var stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40, 80, 40, 40)
        };
        var t = T(title, 22, Text, bold: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(t);
        var d = T(detail, 12, Muted);
        d.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(d);
        return stack;
    }

    // ------------------------------------------------------------------ search & confirmation

    private void ToggleSearch()
    {
        _searchOverlay.IsVisible = !_searchOverlay.IsVisible;
        if (_searchOverlay.IsVisible)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
        }
    }

    private void RunSearch(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return;

        try
        {
            var result = _session.Search(query);
            _searchResults.Children.Clear();
            if (result.Results.Count == 0)
            {
                _searchHint.Text = $"No active site carries “{query}”. Tags are required for search.";
                return;
            }

            _searchHint.Text = $"{result.Results.Count} hit(s) · tag matches rank first · nothing opens until confirmed";
            foreach (var hit in result.Results)
                _searchResults.Children.Add(SearchHitCard(hit));
        }
        catch (Exception ex)
        {
            _searchHint.Text = "search failed · " + ex.Message;
        }
    }

    private void PromptSite(SiteSummary hit)
    {
        _pendingSite = hit;
        _pendingExternal = null;
        _confirmTitle.Text = "DEVICE CONFIRMATION · OPEN SEALED SITE";
        _confirmOpen.Content = "OPEN ON SCREEN 1";
        _confirmText.Text =
            $"jttp://{hit.Host}/\n{hit.Title}\n{hit.Headline}\n\n" +
            $"tags      {string.Join(", ", hit.Tags)}\n" +
            $"content   {Short(hit.ContentHash)}\n" +
            $"owner     {(string.IsNullOrEmpty(hit.BadgeId) ? "no paid title" : hit.BadgeId)}\n" +
            $"updated   {Stamp(hit.UpdatedUtc)}\n\n" +
            "Nothing has been opened yet. Confirm to load the package, verify its hash, and render it.";
        _confirmOverlay.IsVisible = true;
    }

    private void PromptExternal(NewsItem item)
    {
        _pendingSite = null;
        _pendingExternal = item.Link;
        _confirmTitle.Text = "LEAVE THE SEALED LANE?";
        _confirmOpen.Content = "OPEN IN SYSTEM BROWSER";
        _confirmText.Text =
            $"{item.Title}\n{item.Host}\n\n{item.Link}\n\n" +
            "This is an http(s) link. Kickl will hand it to your system browser. " +
            "The open web sees your IP and normal browser identity; the sealed session is not involved.";
        _confirmOverlay.IsVisible = true;
    }

    private void ConfirmPending()
    {
        _confirmOverlay.IsVisible = false;
        if (_pendingSite is { } site)
        {
            _pendingSite = null;
            NewTab("jttp://" + site.Host + "/");
            return;
        }
        if (_pendingExternal is { } url)
        {
            _pendingExternal = null;
            OpenExternal(url);
        }
    }

    // ------------------------------------------------------------------ news / saved items

    private void LoadSources()
    {
        if (!_session.IsUnlocked) return;
        _sources.Clear();
        var path = SourcesPath;
        if (File.Exists(path))
        {
            try
            {
                var encrypted = File.ReadAllBytes(path);
                var plaintext = _session.OpenProfileData("news-sources", encrypted);
                List<FeedSource>? list;
                try { list = JsonSerializer.Deserialize<List<FeedSource>>(plaintext); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                if (list is not null)
                    _sources.AddRange(list.Where(s => !string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.FilePath)));
            }
            catch (Exception) { /* a broken source file is not fatal */ }
        }
        else if (_session.Paths.ProfileId == "local" && File.Exists(LegacySourcesPath))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<List<FeedSource>>(File.ReadAllText(LegacySourcesPath));
                if (legacy is not null)
                {
                    _sources.AddRange(legacy.Where(s => !string.IsNullOrWhiteSpace(s.Url)));
                    SaveSources();
                    File.Delete(LegacySourcesPath);
                }
            }
            catch (Exception) { /* leave the original file in place if migration fails */ }
        }

        if (File.Exists(SecupgradeRssFile) && !_sources.Any(source =>
                string.Equals(source.FilePath, SecupgradeRssFile, StringComparison.OrdinalIgnoreCase)))
            _sources.Insert(0, new FeedSource { FilePath = SecupgradeRssFile, Name = "secupgrade.com" });
    }

    private string SourcesPath => Path.Combine(_session.Paths.ProfileRoot, "private", "news-sources.kpf");
    private string LegacySourcesPath => Path.Combine(_dataRoot, "shared", "news-sources.json");

    private void SaveSources()
    {
        if (!_session.IsUnlocked) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcesPath)!);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(_sources,
                new JsonSerializerOptions { WriteIndented = true });
            var encrypted = _session.ProtectProfileData("news-sources", plaintext);
            CryptographicOperations.ZeroMemory(plaintext);
            var temp = SourcesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, encrypted);
                File.Move(temp, SourcesPath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (Exception ex)
        {
            _newsStatus.Text = "could not save sources · " + ex.Message;
        }
    }

    private void RefreshRegistryNews()
    {
        var items = NewsFeed.FromRegistry(LocalRegistry.LoadOrEmpty(_session.Paths));
        var first = _seenSiteIds.Count == 0;
        foreach (var item in items)
        {
            if (!first && _seenSiteIds.Add(item.Id))
                _newIds.Add(item.Id);
            else
                _seenSiteIds.Add(item.Id);
        }

        _siteNews.Clear();
        _siteNews.AddRange(NewsFeed.Merge(items, _rssNews));
        RefreshNewsView();
    }

    private void RefreshNewsView(bool force = false)
    {
        var items = _newsModeSaved ? _savedItems.Load() : _siteNews;
        var signature = (_newsModeSaved ? "saved|" : "stream|")
                        + items.Count + "|" + string.Join(",", items.Select(i => i.Id + ":" + i.PublishedUtc))
                        + "|" + _newIds.Count + "|" + _savedItems.Load().Count;
        if (!force && signature == _newsSignature) return;
        _newsSignature = signature;

        _newsFeed.Children.Clear();
        _newsCount.Text = _newsModeSaved
            ? $"{items.Count} saved"
            : $"{items.Count} items · {_newIds.Count} new";
        _newsHeaderDetail.Text = _newsModeSaved ? "saved reading" : "newest first · no ranking";

        if (items.Count == 0)
        {
            _newsFeed.Children.Add(BuildNewsEmpty());
            return;
        }

        foreach (var item in items)
            _newsFeed.Children.Add(BuildNewsCard(item));
    }

    private Control BuildNewsEmpty() => new Border
    {
        Padding = new Thickness(18, 24),
        Child = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                T(_newsModeSaved ? "SAVED LIST IS EMPTY" : "WAITING FOR THE STREAM", 10.5, Muted, bold: true),
                T(_newsModeSaved
                    ? "Save a card from the stream and it lands here. No read state, no recommendations."
                    : "Publishing to the registry puts a site card here within two seconds. Add an RSS/Atom URL above for the wider wire.",
                    11, Muted).WithWrap()
            }
        }
    };

    private Control BuildNewsCard(NewsItem item)
    {
        var isNew = _newIds.Contains(item.Id);
        var saved = _savedItems.Contains(item.Id);
        var isSiteAddition = item.IsSiteAddition;

        void OpenItem()
        {
            if (isSiteAddition)
            {
                var record = LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(_session.Paths), item.Host);
                PromptSite(record is null ? new SiteSummary
                {
                    Host = item.Host, Title = item.Title, Headline = item.Summary,
                    Tags = item.Tags, UpdatedUtc = item.PublishedUtc
                } : Phonebook.SummaryOf(record));
            }
            else
            {
                PromptExternal(item);
            }
        }

        void ToggleSaved()
        {
            _savedItems.Toggle(item);
            RefreshNewsView(force: true);
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = item.IsStory
            ? "STORY · " + item.Source.ToUpperInvariant()
            : item.IsSiteAddition ? "SITE ADDITION · PINWHEEL" : "RSS · " + item.Source.ToUpperInvariant();
        var source = T(label, 8.5,
            item.IsStory ? Amber : item.IsSiteAddition ? Green : Accent, bold: true);
        source.TextTrimming = TextTrimming.CharacterEllipsis;
        top.Children.Add(source);
        var time = T(Stamp(item.PublishedUtc), 8.5, Muted, mono: true);
        time.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(time);
        Grid.SetColumn(time, 1);

        var title = T(item.Title, 12.5, Text, bold: true);
        title.TextWrapping = TextWrapping.Wrap;

        var summary = T(item.Summary, 10.5, Muted);
        summary.TextWrapping = TextWrapping.Wrap;
        summary.LineHeight = 15;

        var byline = string.Join(" · ", new[] { item.Author, item.StoryDate, item.Location }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var host = T((item.IsSiteAddition ? "jttp://" : "") + item.Host +
                     (byline.Length > 0 ? " · " + byline : ""), 8.5, Muted, mono: true);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var open = Btn(item.IsSiteAddition ? "OPEN" : "VISIT", OpenItem, accent: true);
        open.Height = 24;
        open.FontSize = 9;
        actions.Children.Add(open);

        var save = Btn(saved ? "SAVED" : "SAVE", ToggleSaved);
        save.Height = 24;
        save.FontSize = 9;
        actions.Children.Add(save);

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(top);
        stack.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(item.Summary))
            stack.Children.Add(summary);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        bottom.Children.Add(host);
        bottom.Children.Add(actions);
        Grid.SetColumn(actions, 1);
        stack.Children.Add(bottom);

        var card = new Border
        {
            Background = C(isNew ? "#12202a" : Panel2),
            BorderBrush = C(isNew ? "#2d5b6e" : "#1a2838"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(9, 3),
            Focusable = true,
            Child = stack
        };

        if (isNew)
        {
            source.Text = "NEW · " + source.Text;
            source.Foreground = C(Amber);
        }

        card.PointerEntered += (_, _) => card.Background = C("#152433");
        card.PointerExited += (_, _) => card.Background = C(isNew ? "#12202a" : Panel2);
        card.KeyDown += (_, e) =>
        {
            if (e.Source is Button) return;
            if (e.Key == Key.Enter)
            {
                OpenItem();
                e.Handled = true;
            }
            else if (e.Key is Key.Space or Key.S)
            {
                ToggleSaved();
                e.Handled = true;
            }
            else if (e.Key is Key.Up or Key.Down)
            {
                var index = _newsFeed.Children.IndexOf(card);
                var next = index + (e.Key == Key.Down ? 1 : -1);
                if (next >= 0 && next < _newsFeed.Children.Count && _newsFeed.Children[next] is Control target)
                {
                    target.Focus();
                    target.BringIntoView();
                }
                e.Handled = true;
            }
        };
        return card;
    }

    private void SetNewsMode(bool saved)
    {
        _newsModeSaved = saved;
        StyleModeButton(_streamButton, !saved);
        StyleModeButton(_savedButton, saved);
        RefreshNewsView(force: true);
    }

    private void AddRssSource()
    {
        var url = (_rssUrl.Text ?? "").Trim();
        if (url.Length == 0) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            _newsStatus.Text = "RSS source must be an http(s) URL";
            return;
        }

        _ = AddRssSourceAsync(url, uri);
    }

    private async Task AddRssSourceAsync(string url, Uri uri)
    {
        try
        {
            _newsStatus.Text = $"fetching {uri.Host}…";
            var items = await FetchFeedAsync(url, uri);
            if (!_sources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                _sources.Add(new FeedSource { Url = url, Name = uri.Host });
                SaveSources();
            }
            var merged = NewsFeed.Merge(_rssNews, items);
            _rssNews.Clear();
            _rssNews.AddRange(merged);
            _rssUrl.Text = "";
            _newsStatus.Text = $"added {uri.Host} · {items.Count} card(s) · RSS is opt-in and IP-visible";
            _siteNews.Clear();
            _siteNews.AddRange(NewsFeed.Merge(NewsFeed.FromRegistry(LocalRegistry.LoadOrEmpty(_session.Paths)), _rssNews));
            RefreshNewsView(force: true);
        }
        catch (Exception ex)
        {
            _newsStatus.Text = "feed failed · " + ex.Message;
        }
    }

    private async Task<List<NewsItem>> FetchFeedAsync(string url, Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("KicklBrowser/0.1 news-reader");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > HttpLimits.Feed)
            throw new FormatException("Feed is larger than 2 MB.");   // chunked replies carry no length; the client's own cap covers those
        var xml = await response.Content.ReadAsStringAsync();
        return NewsFeed.ParseRss(xml, uri.Host);
    }

    private async Task<List<NewsItem>> FetchFeedAsync(FeedSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            var file = new FileInfo(source.FilePath);
            if (!file.Exists) throw new FileNotFoundException("RSS file is unavailable.");
            if (file.Length > 2_000_000) throw new FormatException("The feed is larger than 2 MB.");
            var xml = await File.ReadAllTextAsync(file.FullName);
            return NewsFeed.ParseRss(xml, source.Name);
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new FormatException("RSS source URL is invalid.");
        return await FetchFeedAsync(source.Url, uri);
    }

    private async void RefreshRssSources()
    {
        if (!_session.IsUnlocked || _rssBusy || _sources.Count == 0) return;
        var profileEpoch = _profileEpoch;
        _rssBusy = true;
        _newsStatus.Text = "refreshing RSS sources";
        var sources = _sources.ToArray();
        try
        {
            var fetched = new List<NewsItem>();
            var ok = 0;
            var failed = 0;
            foreach (var source in sources)
            {
                if (!_session.IsUnlocked || profileEpoch != _profileEpoch) return;
                try
                {
                    fetched.AddRange(await FetchFeedAsync(source));
                    ok++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }
            if (!_session.IsUnlocked || profileEpoch != _profileEpoch) return;
            if (fetched.Count > 0)
            {
                _rssNews.Clear();
                _rssNews.AddRange(NewsFeed.Merge(_rssNews, fetched));
                _siteNews.Clear();
                _siteNews.AddRange(NewsFeed.Merge(
                    NewsFeed.FromRegistry(LocalRegistry.LoadOrEmpty(_session.Paths)), _rssNews));
                RefreshNewsView(force: true);
            }
            _newsStatus.Text = $"{ok}/{sources.Length} RSS source(s) refreshed"
                               + (failed > 0 ? $" · {failed} unavailable" : "")
                               + (fetched.Count == 0 && failed == 0 ? " · no new stories" : "");
        }
        finally
        {
            _rssBusy = false;
        }
    }

    // ------------------------------------------------------------------ boot & demo

    private void Bootstrap()
    {
        if (!_demoMode)
        {
            SetSessionLocked();
            SetStatus(_session.HasProfile
                ? "local profile found · sign in to unlock"
                : "first run · create a local profile to continue");
            return;
        }

        try
        {
            if (!_session.HasProfile)
                _session.CreateProfile(DemoPin);

            var paths = _session.Paths;
            var registry = LocalRegistry.LoadOrEmpty(paths);
            var missing = DemoHosts
                .Where(host =>
                {
                    var record = LocalRegistry.Resolve(registry, host);
                    return record is null || string.IsNullOrWhiteSpace(record.ContentHash);
                })
                .ToArray();
            if (missing.Length > 0)
                PublishFixtures(paths, missing);

            _session.Unlock(DemoPin);
            EnsureAdDemo(paths);

            // A demo still starts at the gate. Only screenshot mode skips it so the
            // headless visual checks can capture the browser rather than the lock sheet.
            if (_screenshotPath is null)
            {
                _session.Lock();
                SetSessionLocked();
                SetStatus("demo profile ready · sign in with demo key 1234");
            }
            else
            {
                SetPill(_sessionPill, "UNLOCKED · PFS", Green, "#102118");
                SetStatus("demo ready · screen 1 browser · screen 2 news");
            }
        }
        catch (Exception ex)
        {
            SetSessionLocked();
            SetStatus("startup note · " + ex.Message);
        }
    }

    /// <summary>Publish one opt-in ad site per fresh demo profile so the flow is visible.</summary>
    private void EnsureAdDemo(DataPaths paths)
    {
        try
        {
            var registry = LocalRegistry.LoadOrEmpty(paths);
            if (LocalRegistry.Resolve(registry, "ad-lab.custom") is not null) return;

            var publisher = _session.CreateNicklesPublisher("ad-lab.custom", Nickles.DefaultSpace);
            _session.CreateSite(new StoryDraft
            {
                Host = "ad-lab.custom",
                Headline = "NCKL ad lab",
                Author = "demo",
                Html = """
                       <div>
                         <h2>NCKL publisher demo</h2>
                         <p>This page opted in by pasting a publisher loader into its inline HTML. The loader was minted for ad-lab.custom and site.inline.</p>
                         <p>The slot below is native: no script engine, no fetched creative, no impression beacon. Rotate by waiting for the next ten-minute bucket.</p>
                       </div>
                       """ + "\n" + Nickles.Snippet(publisher),
                Tags = ["demo", "ads"]
            });
        }
        catch (Exception ex)
        {
            SetStatus("ad demo note · " + ex.Message);
        }
    }

    private void SetSessionLocked()
    {
        SetPill(_sessionPill, "LOCKED", Amber, "#241f0d");
    }

    private static void PublishFixtures(DataPaths paths, IEnumerable<string> hosts)
    {
        var root = FindRepoRoot();
        if (root is null) return;

        var master = Keystore.Unlock(paths, DemoPin);
        try
        {
            foreach (var host in hosts)
            {
                var site = Path.Combine(root, "sites", host);
                if (!File.Exists(Path.Combine(site, "site.kml"))) continue;
                DocumentStore.Publish(paths, site, host, master);
            }
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

    // ------------------------------------------------------------------ timers & screenshot

    private void StartTimers()
    {
        var news = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        news.Tick += (_, _) => RefreshRegistryNews();
        news.Start();

        var rss = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        rss.Tick += (_, _) => RefreshRssSources();
        rss.Start();

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
            _clock.Text = DateTimeOffset.Now.ToString("HH:mm:ss · ddd d MMM", CultureInfo.InvariantCulture);
        clock.Start();

        var adRotation = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        adRotation.Tick += (_, _) =>
        {
            var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600;
            if (bucket == _homeAdBucket || Active is not { Page: null, Game: null, BuiltIn: null }) return;
            _homeAdOverlay.Children.Clear();
            _homeAdOverlay.Children.Add(BuildHomeAd());
        };
        adRotation.Start();

        if (_sources.Count > 0)
            Dispatcher.UIThread.Post(() => RefreshRssSources());
    }

    private void ScheduleScreenshot()
    {
        if (_screenshotPath is null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                SaveScreenshot();
            }
            finally
            {
                if (_exitAfterScreenshot) Close();
            }
        };
        timer.Start();
    }

    private void SaveScreenshot()
    {
        var path = Path.GetFullPath(_screenshotPath!);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream);
        SetStatus("screenshot saved · " + path);
    }

    // ------------------------------------------------------------------ keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_lockOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                _pinBox.Focus();
                e.Handled = true;
            }
            return;
        }

        if ((ctrl && e.Key == Key.L) || (alt && e.Key == Key.D))
        {
            _address.Focus();
            _address.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.T)
        {
            ReopenLastClosedTab();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.T)
        {
            NewTab();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.W)
        {
            CloseTab(_active);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.K)
        {
            LockSession();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.K)
        {
            ToggleSearch();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab)
        {
            SelectRelativeTab(shift ? -1 : 1);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D9)
        {
            SelectTab(9);
            e.Handled = true;
        }
        else if (ctrl && e.Key is >= Key.D1 and <= Key.D8)
        {
            SelectTab((int)e.Key - (int)Key.D0);
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            CycleKeyboardFocus();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.R || e.Key == Key.F5)
        {
            Reload();
            e.Handled = true;
        }
        else if (alt && e.Key == Key.Left)
        {
            Back();
            e.Handled = true;
        }
        else if (alt && e.Key == Key.Right)
        {
            Forward();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _searchOverlay.IsVisible = false;
            _confirmOverlay.IsVisible = false;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");
    private static readonly FontFamily Sans = new("Segoe UI, Inter, Arial, sans-serif");

    private static SolidColorBrush C(string hex) => new(Color.Parse(hex));

    private static TextBlock T(string text, double size, string color, bool mono = false, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = C(color),
        FontFamily = mono ? Mono : Sans,
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
    };

    private static Border Pill(string text, string color, string background) => new()
    {
        Background = C(background),
        BorderBrush = C(color),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(7, 2),
        Child = T(text, 8.5, color, bold: true)
    };

    private static void SetPill(Border pill, string text, string color, string background)
    {
        pill.Background = C(background);
        pill.BorderBrush = C(color);
        pill.Child = T(text, 8.5, color, bold: true);
    }

    private static Border Box(string text, string color, string background, bool mono = false) => new()
    {
        Background = C(background),
        BorderBrush = C(Line),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(12, 9),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = mono ? Mono : Sans,
            FontSize = 11.5,
            Foreground = C(color),
            TextWrapping = TextWrapping.Wrap
        }
    };

    private static Button Btn(string text, Action action, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 10,
            FontFamily = Mono,
            Background = C(accent ? "#132b44" : "#151f2b"),
            Foreground = C(accent ? "#9ecbff" : Text),
            BorderBrush = C(accent ? "#2f6b9a" : "#2a3a4c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button FlatButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 15,
            Background = Brushes.Transparent,
            Foreground = C(Muted),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static void StyleInput(TextBox box, string watermark)
    {
        box.Watermark = watermark;
        box.Background = C("#0a1119");
        box.Foreground = C(Text);
        box.BorderBrush = C("#26374a");
        box.BorderThickness = new Thickness(1);
        box.CornerRadius = new CornerRadius(6);
        box.Padding = new Thickness(8, 4);
    }

    private static void StyleModeButton(Button button, bool active)
    {
        button.Background = C(active ? "#16324c" : Panel);
        button.Foreground = C(active ? "#9ecbff" : Muted);
        button.BorderBrush = C(active ? "#2f6b9a" : Line);
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private static string Stamp(long unix)
    {
        if (unix <= 0) return "—";
        var local = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "—" : value.Length <= 20 ? value : value[..20] + "…";

    protected override void OnClosed(EventArgs e)
    {
        StopAllGames();
        foreach (var tab in _tabs) ClearImages(tab);
        _http.Dispose();
        _mail?.Dispose();
        _session.Dispose();
        base.OnClosed(e);
    }

    private sealed record HomeGame(
        string Id,
        string Title,
        string Tagline,
        string Author,
        string Meta,
        string Accent,
        bool Playable);

    private sealed class BrowserTab
    {
        public string Title { get; set; } = "new tab";
        public string Address { get; set; } = "";
        public List<string> History { get; } = [];
        public int HistoryIndex { get; set; } = -1;
        public PageView? Page { get; set; }
        public bool Verified { get; set; }
        public OwnershipStatus? Ownership { get; set; }
        public HashSet<string> Unlocked { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);
        public string ContentHash { get; set; } = "";
        public string? Transport { get; set; }
        public string? TransportFingerprint { get; set; }
        public StoryDraft? Draft { get; set; }
        public string? BuiltIn { get; set; }
        public string? EditGate { get; set; }
        public string PublisherSite { get; set; } = "";
        public string PublisherSpace { get; set; } = Nickles.DefaultSpace;
        public NicklesPublisher? PublisherResult { get; set; }
        public string PublisherNote { get; set; } = "";
        public string? Game { get; set; }
        public char[]? TttBoard { get; set; }
        public string TttMessage { get; set; } = "Your turn.";
        public int[]? MemoryDeck { get; set; }
        public bool[]? MemoryFaceUp { get; set; }
        public bool[]? MemoryMatched { get; set; }
        public int MemoryFirstCard { get; set; } = -1;
        public int MemoryMoves { get; set; }
        public bool MemoryResolving { get; set; }
        public string HomeQuery { get; set; } = "";
        public List<SiteSummary> HomeResults { get; } = [];
        public string HomeHint { get; set; } = DefaultHomeHint;
        public string ReturnAddress { get; set; } = HomeUrl;
        public Dictionary<string, Bitmap> Images { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HtmlBlockView.UnlockedVisual> Visuals { get; } = new(StringComparer.Ordinal);
        public string? Error { get; set; }
    }

    private sealed class FeedSource
    {
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
    }
}

internal static class TextBlockExtensions
{
    public static TextBlock WithWrap(this TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        return text;
    }
}
