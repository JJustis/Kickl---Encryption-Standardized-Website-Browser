using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Kickl.Core.Crypto;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.Util;

namespace Kickl.Core.Ads;

/// <summary>
/// A webmaster's NCKL placement. The token is derived from the local master key and binds the
/// publisher id to one site and one ad space, so a snippet copied to another site fails closed.
/// </summary>
public sealed class NicklesPublisher
{
    public string Id { get; set; } = "";
    public string Site { get; set; } = "";
    public string Space { get; set; } = Nickles.DefaultSpace;
    public string Token { get; set; } = "";
    public long CreatedUtc { get; set; }

    public NicklesPlacement Placement => new(Id, Site, Space, Token);
}

public sealed record NicklesPlacement(string PublisherId, string Site, string Space, string Token);

public sealed record NicklesCreative(
    string Id,
    string Headline,
    string Support,
    string Dest,
    string Alt,
    string Accent,
    string Hash);

public sealed record NicklesSlot(long Bucket, NicklesCreative Creative, string Signature);

/// <summary>
/// The slice-local NCKL publisher surface. It is deliberately not a script runtime: the
/// <c>nickles.js</c> file is a signed placement manifest. The publisher recognises it while
/// sealing a page and stores only an <c>&lt;ad&gt;</c> block; no site script is ever executed.
/// </summary>
public static class Nickles
{
    public const string NetworkHost = "secupgrade.com";
    public const string NetworkRoot = "/adnetworking";
    public const string PublisherPath = "/adnetworking/publisher";
    public const string LoaderPath = "/adnetworking/nickles.js";
    public const string DefaultSpace = "site.inline";
    public const string Label = "AD";
    public const string SlotMarker = "<kickl-ad-slot></kickl-ad-slot>";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Regex SpacePattern = new(
        "^[a-z0-9][a-z0-9._-]{1,47}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Id, string Headline, string Support, string Dest, string Alt, string Accent)[] Seed =
    [
        ("quiet-slot", "YOUR AD HERE", "NCKL · quiet ads for sealed sites",
            "https://secupgrade.com/adnetworking", "NCKL ad space", "#58a6ff"),
        ("sealed-hosting", "SEALED HOSTING", "Pinwheel blob storage for jttp sites",
            "https://secupgrade.com/hosting", "Sealed hosting", "#78c8c8"),
        ("nickles-testnet", "NICKLES TESTNET", "No value. No promises. Just a ledger.",
            "https://secupgrade.com/nickles", "Nickles testnet", "#f2cc60"),
        ("build-quiet", "BUILD A QUIET SITE", "Inline SVG. No scripts. No trackers.",
            "https://secupgrade.com/start", "Build a quiet site", "#ff7a64")
    ];

    public static IReadOnlyList<NicklesCreative> Creatives { get; } = Seed
        .Select(s => new NicklesCreative(s.Id, s.Headline, s.Support, s.Dest, s.Alt, s.Accent,
            HashCreative(s.Id, s.Headline, s.Support, s.Dest, s.Alt, s.Accent)))
        .ToList();

    public static IReadOnlyList<NicklesPublisher> Publishers(DataPaths paths, byte[] master)
    {
        var path = paths.NicklesPublishers;
        if (File.Exists(path))
        {
            var encrypted = File.ReadAllBytes(path);
            var plaintext = ProfileDataCipher.Open(master, "nickles-publishers", encrypted);
            try
            {
                return JsonSerializer.Deserialize<List<NicklesPublisher>>(plaintext) ?? [];
            }
            catch (JsonException ex)
            {
                throw new KmlException("The NCKL publisher store is unreadable: " + ex.Message);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        var profileLegacy = Path.Combine(paths.ProfileRoot, "ads", "publishers.json");
        var sharedLegacy = Path.Combine(paths.Root, "shared", "ads", "publishers.json");
        var legacy = File.Exists(profileLegacy) ? profileLegacy :
            string.Equals(paths.ProfileId, "local", StringComparison.Ordinal) && File.Exists(sharedLegacy)
                ? sharedLegacy : null;
        if (legacy is null) return [];

        try
        {
            var publishers = JsonSerializer.Deserialize<List<NicklesPublisher>>(File.ReadAllText(legacy)) ?? [];
            SavePublishers(paths, master, publishers);
            File.Delete(legacy);
            return publishers;
        }
        catch (JsonException ex)
        {
            throw new KmlException("The legacy NCKL publisher store is unreadable: " + ex.Message);
        }
    }

    public static NicklesPublisher CreatePublisher(DataPaths paths, byte[] master, string site, string space)
    {
        site = NormalizeSite(site);
        space = NormalizeSpace(space);

        var list = Publishers(paths, master).ToList();
        var existing = list.FirstOrDefault(p =>
            string.Equals(p.Site, site, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Space, space, StringComparison.Ordinal));
        if (existing is not null) return existing;

        var publisher = new NicklesPublisher
        {
            Id = "pub_" + Hashing.Hex(RandomNumberGenerator.GetBytes(8)),
            Site = site,
            Space = space,
            CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        publisher.Token = Sign(master, publisher.Id, publisher.Site, publisher.Space);

        list.Add(publisher);
        SavePublishers(paths, master, list);
        return publisher;
    }

    private static void SavePublishers(DataPaths paths, byte[] master, List<NicklesPublisher> publishers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.NicklesPublishers)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(publishers, Json);
        var encrypted = ProfileDataCipher.Protect(master, "nickles-publishers", plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        var temp = paths.NicklesPublishers + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, paths.NicklesPublishers, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Read the official loader from inline HTML. Absence is fine (the page is simply not an ad
    /// publisher); a malformed official loader is an error so half-configured ads never ship.
    /// </summary>
    public static NicklesPlacement? ReadSnippet(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var doc = new HtmlDocument
        {
            OptionOutputOriginalCase = true,
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        doc.LoadHtml(html);

        var loaders = doc.DocumentNode.Descendants("script")
            .Where(s => IsLoaderSource(s.GetAttributeValue("src", "")))
            .ToList();
        if (loaders.Count == 0) return null;
        if (loaders.Count > 1)
            throw new KmlException("Only one NCKL loader is allowed per page.");

        var loader = loaders[0];
        var publisher = Decode(loader.GetAttributeValue("data-nickles-publisher", ""));
        var site = Decode(loader.GetAttributeValue("data-nickles-site", ""));
        var space = Decode(loader.GetAttributeValue("data-nickles-space", ""));
        var token = Decode(loader.GetAttributeValue("data-nickles-token", ""));
        if (publisher.Length == 0 || site.Length == 0 || space.Length == 0 || token.Length == 0)
            throw new KmlException("The NCKL loader is missing its publisher, site, space, or token.");

        site = NormalizeSite(site);
        space = NormalizeSpace(space);

        var slots = doc.DocumentNode.Descendants("div")
            .Where(d => d.Attributes.Contains("data-nickles-space"))
            .ToList();
        if (slots.Count == 0)
            throw new KmlException("The NCKL loader needs an ad-space <div data-nickles-space=\"…\">.");
        if (slots.Count > 1)
            throw new KmlException("Only one NCKL ad space is allowed per page.");

        var slot = slots[0].GetAttributeValue("data-nickles-space", "").Trim();
        if (!string.Equals(slot, space, StringComparison.Ordinal))
            throw new KmlException("The NCKL loader and ad-space div disagree about the placement.");

        return new NicklesPlacement(publisher, site, space, token);
    }

    /// <summary>
    /// Replace the opted-in ad-space div with a publish-time marker. The marker survives the
    /// sanitizer as an inert element and lets the composer put the native slot exactly where the
    /// webmaster asked for it.
    /// </summary>
    public static string MarkSlot(string? html, string space)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var doc = new HtmlDocument
        {
            OptionOutputOriginalCase = true,
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        doc.LoadHtml(html);
        var slot = doc.DocumentNode.Descendants("div").FirstOrDefault(d =>
            string.Equals(d.GetAttributeValue("data-nickles-space", "").Trim(), space,
                StringComparison.Ordinal));
        if (slot?.ParentNode is { } parent)
            parent.ReplaceChild(HtmlNode.CreateNode(SlotMarker), slot);

        var body = doc.DocumentNode.SelectSingleNode("//body");
        return (body ?? doc.DocumentNode).InnerHtml.Trim();
    }

    /// <summary>Validate the snippet against the host being published and the local ad store.</summary>
    public static NicklesPlacement? ValidateSnippet(string? html, string host, DataPaths paths, byte[] master)
    {
        var placement = ReadSnippet(html);
        if (placement is null) return null;

        if (!string.Equals(placement.Site, host, StringComparison.OrdinalIgnoreCase))
            throw new KmlException(
                $"NCKL placement {placement.PublisherId} is married to {placement.Site}, not {host}.");

        var publisher = Publishers(paths, master).FirstOrDefault(p => p.Id == placement.PublisherId)
            ?? throw new KmlException($"NCKL publisher {placement.PublisherId} is not in this profile.");

        if (!string.Equals(publisher.Site, placement.Site, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(publisher.Space, placement.Space, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(publisher.Token), Encoding.UTF8.GetBytes(placement.Token)))
            throw new KmlException("The NCKL placement does not match its signed publisher record.");

        var expected = Sign(master, placement.PublisherId, placement.Site, placement.Space);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(placement.Token)))
            throw new KmlException("The NCKL placement signature is invalid.");

        return placement;
    }

    /// <summary>The current deterministic creative for a signed placement.</summary>
    public static NicklesSlot ResolveSlot(DataPaths paths, byte[] master, NicklesPlacement placement,
        DateTimeOffset? now = null)
    {
        var publisher = Publishers(paths, master).FirstOrDefault(p => p.Id == placement.PublisherId)
            ?? throw new KmlException("NCKL publisher is not known in this profile.");
        if (!string.Equals(publisher.Site, placement.Site, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(publisher.Space, placement.Space, StringComparison.Ordinal))
            throw new KmlException("NCKL placement is not bound to this site.");

        var expected = Sign(master, placement.PublisherId, placement.Site, placement.Space);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(placement.Token))
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(publisher.Token), Encoding.UTF8.GetBytes(placement.Token)))
            throw new KmlException("NCKL placement signature check failed.");

        var bucket = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 600;
        var key = AdKey(master);
        var seed = HMACSHA256.HashData(key, Utf8.B(placement.Token + "|" + bucket));
        var index = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(seed) % (uint)Creatives.Count);
        var creative = Creatives[index];
        if (!string.Equals(creative.Hash,
                HashCreative(creative.Id, creative.Headline, creative.Support, creative.Dest,
                    creative.Alt, creative.Accent),
                StringComparison.Ordinal))
            throw new KmlException("NCKL creative hash mismatch; the slot was not rendered.");
        var signature = B64u.Encode(HMACSHA256.HashData(key,
            Utf8.B(bucket + "|" + creative.Hash)));
        return new NicklesSlot(bucket, creative, signature);
    }

    /// <summary>Resolve the home placement from the same local creative catalog.</summary>
    public static NicklesSlot ResolveHomeSlot(byte[] master, DateTimeOffset? now = null)
    {
        var bucket = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 600;
        var selection = SHA256.HashData(Utf8.B($"kickl/v1/nckl/home.sidebar.lowerleft|{bucket}"));
        var index = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(selection) % (uint)Creatives.Count);
        var creative = Creatives[index];
        var expectedHash = HashCreative(creative.Id, creative.Headline, creative.Support,
            creative.Dest, creative.Alt, creative.Accent);
        if (!string.Equals(creative.Hash, expectedHash, StringComparison.Ordinal))
            throw new KmlException("NCKL creative hash mismatch; the home slot was not rendered.");

        var signature = B64u.Encode(HMACSHA256.HashData(AdKey(master),
            Utf8.B($"home.sidebar.lowerleft|{bucket}|{creative.Hash}")));
        return new NicklesSlot(bucket, creative, signature);
    }

    public static string Snippet(NicklesPublisher publisher) =>
        $"""
        <div data-nickles-space="{publisher.Space}"></div>
        <script src="jttp://{NetworkHost}{LoaderPath}"
                data-nickles-publisher="{publisher.Id}"
                data-nickles-site="{publisher.Site}"
                data-nickles-space="{publisher.Space}"
                data-nickles-token="{publisher.Token}"></script>
        """;

    /// <summary>
    /// The JS file a webmaster downloads. It is a frozen manifest, not code that runs inside
    /// a sealed page: Kickl has no script engine and never fetches this URL at render time.
    /// </summary>
    public static string LoaderJs(NicklesPublisher publisher) =>
        $$"""
        /* NCKL publisher loader · secupgrade.com/adnetworking
         * Sealed pages do not execute scripts. Kickl recognises this signed manifest while
         * publishing and mounts a native NCKL slot. No tracking, no cookies, no bids. */
        window.KicklNickles = Object.freeze({
          network: "{{NetworkHost}}",
          publisher: "{{publisher.Id}}",
          site: "{{publisher.Site}}",
          space: "{{publisher.Space}}",
          token: "{{publisher.Token}}"
        });
        """;

    public static string NormalizeSite(string site)
    {
        var raw = (site ?? "").Trim();
        if (raw.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
            raw = raw[7..];
        var cut = raw.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) raw = raw[..cut];
        if (!JttpUrl.TryParse("jttp://" + raw + "/", out var url, out var error))
            throw new KmlException("Publisher site must be a jttp host: " + (error ?? "invalid host"));
        return url!.Host;
    }

    public static string NormalizeSpace(string? space)
    {
        var value = string.IsNullOrWhiteSpace(space) ? DefaultSpace : space.Trim().ToLowerInvariant();
        if (!SpacePattern.IsMatch(value))
            throw new KmlException("Ad space must be 2–48 lowercase letters, digits, dots, dashes, or underscores.");
        return value;
    }

    private static bool IsLoaderSource(string src)
    {
        if (!Uri.TryCreate(src.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Host, NetworkHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.AbsolutePath, LoaderPath, StringComparison.Ordinal)) return false;
        return uri.Scheme is "jttp" or "http" or "https";
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value ?? "").Trim();

    private static string Sign(byte[] master, string id, string site, string space) =>
        B64u.Encode(HMACSHA256.HashData(AdKey(master),
            Utf8.B($"kickl/v1/nckl/publisher|{id}|{site}|{space}")));

    private static byte[] AdKey(byte[] master) =>
        Hkdf.Derive(master, Utf8.B("kickl/v1/nckl"), "nckl-publisher-key", 32);

    private static string HashCreative(string id, string headline, string support, string dest,
        string alt, string accent) =>
        Hashing.Hex(Hashing.Sha256(Utf8.B(string.Join("|", id, headline, support, dest, alt, accent))));
}
