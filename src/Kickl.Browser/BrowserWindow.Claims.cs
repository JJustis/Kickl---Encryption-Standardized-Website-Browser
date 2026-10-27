using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Kickl.Core.Names;
using Kickl.Core.Ownership;

namespace Kickl.Browser;

/// <summary>
/// Claiming a name (paid at the BTC shop, which hands the buyer an ownership certificate to download) and
/// proving ownership at <c>jttp://name/edit</c>. Until a name is claimed anyone may create and edit it;
/// afterwards editing asks for the certificate.
/// </summary>
public sealed partial class BrowserWindow
{
    private Grid _claimOverlay = null!;
    private readonly StackPanel _claimBody = new() { Spacing = 9 };
    private readonly List<(string Text, string Color)> _claimLog = [];
    private string _claimHost = "";

    private string _gateNote = "";
    private readonly TextBox _ownerPassBox = new() { PasswordChar = '●', Watermark = "owner passphrase (8+ characters)", FontSize = 11 };
    private readonly TextBox _certPassBox = new() { PasswordChar = '●', Watermark = "copy passphrase (8+ characters)", FontSize = 11 };
    private DateTime _lastRevocationRefresh = DateTime.MinValue;
    private readonly HashSet<string> _sealTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Send a receipt to the CA without being asked: right after it is imported, and once per session when the owner
    /// opens their own claimed site and it is not sealed yet. A failure is reported once and can be retried.
    /// </summary>
    private async Task AutoSeal(string host, bool report)
    {
        if (!_sealTried.Add(host)) return;
        try
        {
            await _session.SubmitToCaAsync(host, ownerPassphrase: _ownerPassBox.Text);
            if (report) Say($"Sealed automatically. The Kickl CA countersigned jttp://{host}/ and logged it; visitors now see VERIFIED OWNER.", Green);
            SetStatus($"jttp://{host}/ sealed by the Kickl CA");
            if (Active is { } tab && string.Equals(tab.Page?.Site, host, StringComparison.OrdinalIgnoreCase))
                tab.Ownership = _session.OwnershipOf(host);
            RenderAll(resetScroll: false);
        }
        catch (Exception ex)
        {
            _sealTried.Remove(host);   // allow a retry on the next open
            if (report) Say("The CA could not seal it yet: " + ex.Message + " Press SEND TO CA to try again.", Amber);
            SetStatus("CA sealing failed · " + ex.Message);
        }
        if (report) RebuildClaim();
    }

    private void AutoSealIfNeeded(string site)
    {
        if (_session.IsUnlocked && _session.HoldsCert(site) && !_session.OwnershipOf(site).Verified)
            _ = AutoSeal(site, report: false);
    }

    /// <summary>
    /// Keep the CA's revocation list fresh without ever blocking a page: at most every six hours, in the
    /// background, and a failure just leaves the list we already hold in force.
    /// </summary>
    private void RefreshCaRevocationsSoon()
    {
        if (!_session.IsUnlocked || DateTime.UtcNow - _lastRevocationRefresh < TimeSpan.FromHours(6)) return;
        _lastRevocationRefresh = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            await _session.RefreshCaRevocationsAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Active is { Page: { } page } tab && _session.IsUnlocked)
                {
                    var after = _session.OwnershipOf(page.Site);
                    if (after.Verified != tab.Ownership?.Verified)
                    {
                        tab.Ownership = after;
                        RenderAll(resetScroll: false);
                    }
                }
            });
        });
    }

    // ------------------------------------------------------------------ claim dialog

    private Grid BuildClaimOverlay()
    {
        var sheet = new Border
        {
            Background = C(Panel2),
            BorderBrush = C("#3a5570"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Width = 600,
            MaxHeight = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = _claimBody }
        };

        var grid = new Grid { IsVisible = false };
        grid.Children.Add(new Border { Background = C("#aa000000") });
        grid.Children.Add(sheet);
        return grid;
    }

    private void Say(string text, string? color = null) => _claimLog.Add((text, color ?? Text));

    private void OpenClaim(string host)
    {
        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            SetStatus("unlock the profile to claim a site");
            return;
        }

        _claimHost = host.ToLowerInvariant();
        _claimLog.Clear();
        Say($"Until it is claimed, anyone can create and edit jttp://{_claimHost}/ for free.");
        Say("Claiming costs Bitcoin. After you pay, the name is yours: only a holder of your ownership certificate can publish to it.");
        Say("Step 1 · press OPEN BTC SHOP. The site name is already filled in.", Accent);
        Say("Step 2 · pay the exact amount shown, then press \"download ownership certificate\" on the shop page.", Accent);
        Say($"Step 3 · come back here and press IMPORT CERTIFICATE, or open jttp://{_claimHost}/edit and choose the file.", Accent);
        Say("Step 4 · press SEND TO CA. The Kickl CA checks the receipt, logs it, and countersigns it. Then every visitor sees a VERIFIED OWNER badge on your site.", Accent);

        var already = _session.OwnershipOf(_claimHost);
        if (already.Verified) Say("This name is claimed and sealed by the Kickl CA.", Green);
        else if (already.Owned) Say("This name already has an active owner on this device. It is not sealed by the CA yet.", Amber);

        RebuildClaim();
        _claimOverlay.IsVisible = true;
    }

    private void RebuildClaim()
    {
        _claimBody.Children.Clear();
        var host = _claimHost;

        _claimBody.Children.Add(T("CLAIM SITE", 12, Accent, bold: true));
        _claimBody.Children.Add(T("jttp://" + host + ".claim", 14, Text, mono: true, bold: true));

        foreach (var (text, color) in _claimLog)
        {
            _claimBody.Children.Add(new Border
            {
                BorderBrush = C(color),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(9, 2, 4, 2),
                Child = T(text, 10.5, color == Text ? "#c9d6e4" : color)
            });
        }

        _claimBody.Children.Add(new Border { Height = 1, Background = C(Line) });
        _claimBody.Children.Add(T("Owner passphrase: sealed into your claim by the CA, so the certificate file alone cannot edit this name. " +
                                  "Enter it before SEND TO CA (first seal only) and to unlock the name later:", 9.5, Muted));
        _claimBody.Children.Add(_ownerPassBox);
        _claimBody.Children.Add(T("Passphrase for certificate copies (protects a saved copy; needed to import one):", 9.5, Muted));
        _claimBody.Children.Add(_certPassBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Btn("OPEN BTC SHOP", OpenShop, accent: true));
        buttons.Children.Add(Btn("IMPORT CERTIFICATE…", () => _ = ImportCertificate(host, fromDialog: true), accent: true));
        if (_session.NeedsOwnerPassphrase(host))
            buttons.Children.Add(Btn("UNLOCK WITH OWNER PASSPHRASE", () => UnlockOwner(host), accent: true));
        if (_session.HoldsCert(host))
        {
            buttons.Children.Add(Btn("SEND TO CA", () => _ = SendToCa(), accent: true));
            buttons.Children.Add(Btn("SAVE A COPY…", () => _ = SaveCertCopy()));
        }
        buttons.Children.Add(Btn("CLOSE", () => _claimOverlay.IsVisible = false));
        _claimBody.Children.Add(buttons);

        _claimBody.Children.Add(T("The shop's certificate file can edit this name for whoever holds it, so import it and then keep it offline. Copies saved here are passphrase-locked. " +
                                  "This profile keeps its copy encrypted.", 9, Muted, mono: true));
    }

    private void OpenShop()
    {
        try
        {
            var link = _session.ClaimShopLink(_claimHost);
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            Say("The BTC shop opened in your system browser, filled in for jttp://" + _claimHost + ".claim. Pay the exact amount, then download the certificate there.", Green);
        }
        catch (Exception ex)
        {
            Say("Could not open the BTC shop: " + ex.Message, Red);
        }
        RebuildClaim();
    }

    /// <summary>Pick a .kcert file, check it, and remember it in this profile.</summary>
    private async Task<bool> ImportCertificate(string host, bool fromDialog)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your ownership certificate",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Kickl ownership certificate") { Patterns = ["*" + OwnershipCert.Extension] }]
        });
        if (files.Count == 0) return false;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var cert = await _session.ImportCertAsync(host, buffer.ToArray(), passphrase: _certPassBox.Text ?? "");
            var expires = DateTimeOffset.FromUnixTimeSeconds(cert.Expires).ToLocalTime();

            if (fromDialog)
            {
                Say($"Certificate accepted. jttp://{host}/ is yours until {expires:yyyy-MM-dd}. This profile can now edit it at jttp://{host}/edit.", Green);
                if (!_session.OwnershipOf(host).Verified)
                {
                    Say("Sending it to the Kickl CA to be sealed…", Muted);
                    RebuildClaim();
                    _ = AutoSeal(host, report: true);
                }
                if (Active is { } tab && string.Equals(tab.Page?.Site, host, StringComparison.OrdinalIgnoreCase))
                    tab.Ownership = _session.OwnershipOf(host);
                RenderAll(resetScroll: false);
                RebuildClaim();
            }
            return true;
        }
        catch (Exception ex)
        {
            if (fromDialog)
            {
                Say("Certificate refused: " + ex.Message, Red);
                RebuildClaim();
            }
            else
            {
                _gateNote = ex.Message;
            }
            return false;
        }
    }

    private void UnlockOwner(string host)
    {
        try
        {
            _session.ProveOwner(host, _ownerPassBox.Text ?? "");
            Say($"Owner passphrase accepted. jttp://{host}/ is unlocked for this session.", Green);
            if (Active is { } tab && string.Equals(tab.Page?.Site, host, StringComparison.OrdinalIgnoreCase))
                tab.Ownership = _session.OwnershipOf(host);
        }
        catch (Exception ex)
        {
            Say(ex.Message, Red);
        }
        RebuildClaim();
    }

    private async Task SendToCa()
    {
        Say("Sending your receipt to the Kickl CA…", Muted);
        RebuildClaim();
        try
        {
            var check = await _session.SubmitToCaAsync(_claimHost, ownerPassphrase: _ownerPassBox.Text);
            Say($"Sealed. The CA countersigned jttp://{_claimHost}/ and wrote it to its transparency log. " +
                "The seal is published with your site; visitors now see VERIFIED OWNER.", Green);
            if (Active is { } tab && string.Equals(tab.Page?.Site, _claimHost, StringComparison.OrdinalIgnoreCase))
                tab.Ownership = _session.OwnershipOf(_claimHost);
            RenderAll(resetScroll: false);
        }
        catch (Exception ex)
        {
            Say(ex.Message, Red);
        }
        RebuildClaim();
    }

    private async Task SaveCertCopy()
    {
        try
        {
            var bytes = _session.ExportCert(_claimHost, _certPassBox.Text ?? "");
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save a copy of your ownership certificate",
                SuggestedFileName = OwnershipCert.FileName(_claimHost),
                DefaultExtension = "kcert",
                FileTypeChoices = [new FilePickerFileType("Kickl ownership certificate") { Patterns = ["*" + OwnershipCert.Extension] }]
            });
            if (target is null) return;
            await using var stream = await target.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            Say("Certificate copy saved, locked with your passphrase. Keep the passphrase safe: without it the copy cannot be opened.", Green);
        }
        catch (Exception ex)
        {
            Say("Not saved: " + ex.Message, Red);
        }
        RebuildClaim();
    }

    // ------------------------------------------------------------------ jttp://name/edit

    private static bool TryEditRoute(string address, out string host)
    {
        host = "";
        if (!JttpUrl.TryParse(address, out var url, out _)) return false;
        if (!url!.Path.TrimEnd('/').Equals("/edit", StringComparison.OrdinalIgnoreCase)) return false;
        host = url.Host;
        return true;
    }

    private void OpenEditRoute(BrowserTab tab, string host, bool addHistory)
    {
        if (!_session.IsUnlocked)
        {
            _lockOverlay.IsVisible = true;
            SetStatus("unlock the profile to edit");
            return;
        }

        var (claimed, mayEdit) = _session.EditAccess(host);
        if (!claimed || mayEdit)
        {
            OpenStoryEditor(tab, host);
            SetStatus(claimed
                ? $"jttp://{host}/ is yours · editing"
                : $"jttp://{host}/ is unclaimed · anyone can edit it for free");
            return;
        }

        var canonical = $"jttp://{host}/edit";
        tab.Page = null;
        tab.Draft = null;
        tab.Error = null;
        tab.BuiltIn = null;
        tab.EditGate = host;
        tab.Address = canonical;
        tab.Title = "ownership certificate · " + host;
        tab.Unlocked.Clear();
        tab.Texts.Clear();
        ClearImages(tab);
        _gateNote = "";

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
        SetStatus($"jttp://{host}/ is claimed · present the ownership certificate to edit");
        RenderAll(resetScroll: true);
    }

    private Control BuildEditGate(BrowserTab tab)
    {
        var host = tab.EditGate!;
        var stack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(42, 40, 42, 60)
        };

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        pills.Children.Add(Pill("jttp://" + host + "/edit", Accent, "#0b1b2c"));
        pills.Children.Add(Pill("CLAIMED", Amber, "#241f0d"));
        stack.Children.Add(pills);

        stack.Children.Add(T("This site is owned", 26, Text, bold: true));
        stack.Children.Add(T($"jttp://{host}/ has been claimed, so editing it needs the ownership certificate the owner " +
                             "downloaded from the BTC shop. Choose that file to continue.", 12, Muted));

        stack.Children.Add(T("If you sealed this name with an owner passphrase, enter it first:", 10.5, Muted));
        stack.Children.Add(_ownerPassBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        actions.Children.Add(Btn("CHOOSE OWNERSHIP CERTIFICATE…", () => _ = PresentCertificate(tab), accent: true));
        actions.Children.Add(Btn("CLAIM THIS SITE", () => OpenClaim(host)));
        actions.Children.Add(Btn("CANCEL", () =>
        {
            tab.EditGate = null;
            NavigateToActive("jttp://" + host + "/", addHistory: false);
        }));
        stack.Children.Add(actions);

        if (_gateNote.Length > 0)
            stack.Children.Add(T(_gateNote, 11, Red));

        stack.Children.Add(T("The certificate is the shop's signed statement for this name. It is checked against the shop key " +
                             "this device pinned, kept encrypted in your profile once accepted, and stops working when the claim expires.",
            9, Muted, mono: true));
        return new Border { Child = stack };
    }

    private async Task PresentCertificate(BrowserTab tab)
    {
        var host = tab.EditGate!;
        if (!await ImportCertificate(host, fromDialog: false))
        {
            RenderAll(resetScroll: false);
            return;
        }

        if (_session.NeedsOwnerPassphrase(host))
        {
            try { _session.ProveOwner(host, _ownerPassBox.Text ?? ""); }
            catch (Exception ex)
            {
                _gateNote = ex.Message + " The certificate is imported, but this name is locked to its owner's passphrase.";
                RenderAll(resetScroll: false);
                return;
            }
        }

        tab.EditGate = null;
        OpenStoryEditor(tab, host);
        SetStatus($"certificate accepted · editing jttp://{host}/");
        AutoSealIfNeeded(host);
    }
}
