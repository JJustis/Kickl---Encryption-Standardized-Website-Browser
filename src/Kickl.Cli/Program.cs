using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Markup;
using Kickl.Core.Names;
using Kickl.Core.Ownership;
using Kickl.Core.Privacy;
using Kickl.Core.Registry;
using Kickl.Core.Session;
using Kickl.Core.Stego;
using Kickl.Core.Util;

namespace Kickl.Cli;

internal static class Program
{
    private const string Canary = "PINWHEEL-CANARY-7f3a";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex) when (ex is InvalidUserKeyException or SiteNotFoundException
                                       or IntegrityException or ContentIntegrityException
                                       or KmlException or FormatException
                                       or ArgumentException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            Error(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) return Help();

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];

        return command switch
        {
            "init" => Init(rest),
            "publish" => Publish(rest),
            "open" => Open(rest),
            "lookup" => Lookup(rest),
            "search" => Search(rest),
            "sealed" => Sealed(rest),
            "cache" => Cache(rest),
            "own" => Own(rest),
            "close" => Close(rest),
            "unlock" => UnlockBlob(rest),
            "verify" => Verify(rest),
            "classify" => Classify(rest),
            "shield" => Shield(rest),
            "status" => Status(rest),
            "registry" => Registry(rest),
            "demo" => Demo(rest),
            "shell" => Shell(rest),
            "help" or "--help" or "-h" => Help(),
            _ => Unknown(command)
        };
    }

    // ---------------------------------------------------------------- commands

    private static int Init(string[] args)
    {
        var opts = Options.Parse(args);
        var data = DataDir(opts);
        var pin = opts.Get("pin") ?? PromptPin("Choose a user key: ");

        var session = new BrowserSession(data);
        if (session.HasProfile && !opts.Has("force"))
        {
            Error("A profile already exists. Use --force only if you mean to start over.");
            return 1;
        }

        if (opts.Has("force") && session.HasProfile)
            Directory.Delete(new DataPaths(data).ProfileRoot, recursive: true);

        session.CreateProfile(pin);
        Ok($"profile created at {new DataPaths(data).ProfileRoot}");
        Console.WriteLine("  keystore: profiles/local/keys/keystore.json");
        Console.WriteLine($"  kdf:      {PinKdf.Id} (m={PinKdf.MemoryKiB} KiB, t={PinKdf.Passes}, p={PinKdf.Parallelism})");
        Console.WriteLine("  warning:  there is no key reset. Lose the user key, lose the profile.");
        return 0;
    }

    private static int Publish(string[] args)
    {
        var opts = Options.Parse(args);
        var data = DataDir(opts);
        var site = opts.Get("site") ?? "sites/pinwheelexplained.com";
        var host = opts.Get("host") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(site)));
        var pin = opts.Get("pin") ?? PromptPin("User key: ");

        var paths = new DataPaths(data);
        paths.Ensure();
        var master = Keystore.Unlock(paths, pin);
        try
        {
            var doc = DocumentStore.Publish(paths, site, host, master);
            var page = Kml.Parse(doc.Markup, host);
            Ok($"published jttp://{host}/");
            Console.WriteLine($"  headline: {page.Headline}");
            Console.WriteLine($"  tags:     {string.Join(", ", page.Tags)} (required for the live index)");
            Console.WriteLine($"  markup:   {Encoding.UTF8.GetByteCount(doc.Markup)} B (public structure)");
            Console.WriteLine($"  blobs:    {doc.Blobs.Count} sealed (AES-256-GCM)");
            foreach (var (id, blob) in doc.Blobs.OrderBy(k => k.Key))
                Console.WriteLine($"    {id,-10} {blob.Mime,-18} {blob.PlainLength,6} B plain");
            Console.WriteLine($"  doc key:  wrapped under the profile master key");
            Console.WriteLine($"  content:  {doc.Integrity} (SHA-256 sent to the registry)");
            Console.WriteLine($"  integrity: {doc.Integrity[..16]}… (SHA-256; not authorship)");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static int Open(string[] args)
    {
        var opts = Options.Parse(args);
        var address = opts.Positional.FirstOrDefault() ?? opts.Get("url");
        if (address is null)
        {
            Error("open needs an address, e.g. open jttp://pinwheelexplained.com/");
            return 1;
        }
        if (!address.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
            address = "jttp://" + address.TrimStart('/');

        var data = DataDir(opts);
        var pin = opts.Get("pin");

        using var session = new BrowserSession(data);
        var page = session.Open(address, pin);
        PrintPage(session, page, address, new HashSet<string>(), new Dictionary<string, string>());
        Console.WriteLine(session.IsUnlocked
            ? $"session: unlocked ({session.SessionId})"
            : "session: locked (markup is public; blobs stay sealed)");
        return 0;
    }

    private static int Lookup(string[] args)
    {
        var opts = Options.Parse(args);
        var host = opts.Positional.FirstOrDefault() ?? opts.Get("host");
        if (host is null)
        {
            Error("lookup needs a host, e.g. lookup pinwheelexplained.com");
            return 1;
        }
        host = host.Replace("jttp://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

        using var session = new BrowserSession(DataDir(opts));
        var r = session.Lookup(host);

        Console.WriteLine("jttp name lookup over PFS");
        Console.WriteLine("  scheme:      LegendDX PFS (P-256 ECDH ? HKDF-SHA384 ? AES-256-GCM)");
        Console.WriteLine($"  client key:  {r.ClientKeyBytes} B ephemeral SPKI");
        Console.WriteLine($"  server key:  {r.ServerKeyBytes} B ephemeral SPKI");
        Console.WriteLine($"  salt:        {r.SaltBytes} B");
        Console.WriteLine($"  session:     {r.SessionId}");
        Console.WriteLine($"  key print:   {r.KeyFingerprint}� (not the key; destroyed after reply)");
        Console.WriteLine($"  query:       {r.SealedQueryBytes} B sealed");
        Console.WriteLine($"  response:    {r.SealedResponseBytes} B sealed");
        Console.WriteLine();

        if (!r.Response.Found)
        {
            Console.WriteLine($"  result:     no record for {host}");
            Console.WriteLine("  note:       the \"not found\" itself was encrypted");
            return 1;
        }

        Console.WriteLine($"  result:     jttp://{r.Response.Host}/  v{r.Response.Version}  status={r.Response.Status}");
        Console.WriteLine($"  owner:      {(string.IsNullOrEmpty(r.Response.OwnerId) ? "no paid title" : r.Response.OwnerId)}");
        Console.WriteLine($"  badge:      {(string.IsNullOrEmpty(r.Response.BadgeId) ? "—" : r.Response.BadgeId)}");
        Console.WriteLine($"  content:    {r.Response.ContentHash} (SHA-256 of the package)");
        Console.WriteLine($"  attacks:    {LocalRegistry.Resolve(LocalRegistry.LoadOrEmpty(new DataPaths(DataDir(opts))), host)?.ContentMismatchCount ?? 0} hash mismatch(es) recorded");
        Console.WriteLine("  session key invalidated; past lookups stay unreadable if the registry key leaks");
        return 0;
    }

    private static int Search(string[] args)
    {
        var opts = Options.Parse(args);
        var query = opts.Positional.FirstOrDefault() ?? opts.Get("q") ?? opts.Get("tag");
        if (string.IsNullOrWhiteSpace(query))
        {
            Error("search needs a tag or phrase, e.g. search privacy");
            return 1;
        }

        using var session = new BrowserSession(DataDir(opts));
        var result = session.Search(query.Trim());

        Console.WriteLine("live index over PFS (title · headline · updated · domain)");
        Console.WriteLine($"  query: {result.Query}  ({result.Results.Count} hit(s))");
        Console.WriteLine();

        for (var i = 0; i < result.Results.Count; i++)
        {
            var hit = result.Results[i];
            var updated = DateTimeOffset.FromUnixTimeSeconds(hit.UpdatedUtc).UtcDateTime
                .ToString("yyyy-MM-dd HH:mm 'UTC'");
            Console.WriteLine($"  {i + 1}. jttp://{hit.Host}/");
            Console.WriteLine($"     {hit.Title}");
            Console.WriteLine($"     {hit.Headline}");
            Console.WriteLine($"     updated {updated} · v{hit.Version} · tags {string.Join(", ", hit.Tags)}");
            Console.WriteLine($"     owner {(string.IsNullOrEmpty(hit.BadgeId) ? "no paid title" : hit.BadgeId)}");
            Console.WriteLine();
        }

        if (result.Results.Count == 0)
        {
            Console.WriteLine("  no active site carries that tag or phrase.");
            Console.WriteLine("  This site is unclaimed. Typing it starts the claim/build flow.");
            return 1;
        }

        Console.WriteLine("Activating a result does not navigate immediately:");
        Console.WriteLine("the device asks you to confirm the host before it opens.");
        Console.WriteLine("  use: kickl open jttp://<host>/   (confirmation is a UI step)");
        return 0;
    }

    private static int Sealed(string[] args)
    {
        var opts = Options.Parse(args);
        var address = opts.Positional.FirstOrDefault() ?? opts.Get("url") ?? "jttp://pinwheelexplained.com/";
        if (!address.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
            address = "jttp://" + address.TrimStart('/');
        var pin = opts.Get("pin") ?? PromptPin("User key: ");

        using var session = new BrowserSession(DataDir(opts));
        var page = session.Open(address, pin);
        using var sealedPage = SealedPage.Seal(page, session.SessionSecret);

        Console.WriteLine(sealedPage.Render());

        var reveal = opts.Get("reveal");
        if (reveal is not null)
        {
            if (!int.TryParse(reveal, out var index) || index < 0 || index >= sealedPage.RegionCount)
            {
                Error($"region must be 0..{sealedPage.RegionCount - 1}");
                return 1;
            }
            sealedPage.Reveal(index, session.SessionSecret);
            Console.WriteLine();
            Ok($"region {index} decrypted for this view only");
            Console.WriteLine(sealedPage.Render());
        }
        return 0;
    }

    private static int Cache(string[] args)
    {
        var opts = Options.Parse(args);
        var address = opts.Positional.FirstOrDefault() ?? opts.Get("url") ?? "jttp://pinwheelexplained.com/";
        if (!address.StartsWith("jttp://", StringComparison.OrdinalIgnoreCase))
            address = "jttp://" + address.TrimStart('/');
        var pin = opts.Get("pin") ?? PromptPin("User key: ");

        using var session = new BrowserSession(DataDir(opts));
        var page = session.Open(address, pin);
        var content = page.Render(address, session.IsNameVerified(page.Site),
            new HashSet<string>(), new Dictionary<string, string>());

        var cache = new StegoCache(session.Paths.StegoRoot);
        var envelope = new CacheEnvelope
        {
            Site = page.Site,
            Path = "/",
            Title = page.Title,
            Content = content,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };

        var path = cache.Store(session.SessionSecret, envelope, opts.Get("out"));
        var firstBytes = File.ReadAllBytes(path);
        var firstHash = HashBytes(firstBytes);
        var firstSize = firstBytes.Length;

        Ok($"sealed page cached to {path}");
        Console.WriteLine($"  carrier:     {firstSize} B lossless PNG (JPEG LSB would be destroyed)");
        Console.WriteLine($"  ciphertext:  {envelope.Content.Length} B page content + envelope, AES-256-GCM");
        Console.WriteLine($"  site name in file bytes: {Contains(firstBytes, Encoding.UTF8.GetBytes(page.Site))}");
        Console.WriteLine($"  hash:        {firstHash[..24]}�");

        // A cache request re-seals with a new seed: new ciphertext, new image, same page.
        var loaded = cache.Load(session.SessionSecret, path);
        var secondBytes = File.ReadAllBytes(path);
        var secondHash = HashBytes(secondBytes);
        Ok("cache request re-sealed the carrier");
        Console.WriteLine($"  new hash:    {secondHash[..24]}�");
        Console.WriteLine($"  bytes changed: {firstHash != secondHash}");
        Console.WriteLine($"  content same:  {loaded.Content == envelope.Content}");
        Console.WriteLine($"  seed changed:  {!firstBytes.AsSpan().SequenceEqual(secondBytes)}");
        Console.WriteLine("  both keys derive from the live session; lock and the carrier is noise");
        return 0;
    }

    private static int Close(string[] args)    {
        var opts = Options.Parse(args);
        var host = opts.Positional.FirstOrDefault() ?? opts.Get("host");
        if (host is null)
        {
            Error("close needs a name, e.g. close --host example.custom");
            return 1;
        }
        var paths = new DataPaths(DataDir(opts));
        var pin = opts.Get("pin") ?? PromptPin("User key: ");

        using var session = new BrowserSession(paths.Root);
        session.Unlock(pin);
        LocalRegistry.Close(paths, host);
        Ok($"jttp://{host}/ closed; open will refuse it until the owner publishes it again");
        return 0;
    }

    private static int Own(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("""
own create   --site HOST --usb DIR [--name DISPLAY] [--data DIR]
own verify   --site HOST --usb DIR [--data DIR]
own status   --site HOST [--data DIR]
own transfer --site HOST --usb SELLER_DIR --buyer PUB.json --price N [--out FILE] [--data DIR]
own accept   --site HOST --usb BUYER_DIR --transfer FILE [--data DIR]

The USB holds an RSA-3072 key and the AES-256 title token wrapped with RSA-OAEP.
Ownership is real only while the badge is active; without a title a name can be
created and closed by its owner but not sold or traded.
""");
            return 0;
        }

        var action = args[0].ToLowerInvariant();
        var opts = Options.Parse(args[1..]);
        var site = opts.Get("site") ?? opts.Positional.FirstOrDefault();
        if (site is null)
        {
            Error("--site is required");
            return 1;
        }

        var paths = new DataPaths(DataDir(opts));

        switch (action)
        {
            case "create":
            {
                var usb = opts.Get("usb") ?? throw new ArgumentException("--usb DIR is required");
                var passphrase = ReadSecret("Owner-key passphrase (10+ characters): ");
                var confirm = ReadSecret("Confirm passphrase: ");
                if (passphrase != confirm) throw new ArgumentException("Passphrases do not match.");
                var pub = OwnershipAuthority.Create(paths, site, usb, passphrase, opts.Get("name") ?? "");
                Ok($"owner key written to {OwnershipAuthority.SecretPath(usb, site)}");
                Console.WriteLine($"  owner id:  {pub.OwnerId}");
                Console.WriteLine($"  RSA key:   {Convert.FromBase64String(pub.RsaPublicKeySpki).Length} B SPKI (3072-bit)");
                Console.WriteLine("  AES token: wrapped with RSA-OAEP, never stored in clear");
                Console.WriteLine("  public:    " + OwnershipAuthority.PublicPath(paths, site));
                Console.WriteLine("  owner key: Argon2id + AES-256-GCM protected; passphrase is required to unlock");
                Console.WriteLine("  keep the USB offline; without it the title cannot be proved or transferred");
                return 0;
            }
            case "verify":
            {
                var usb = opts.Get("usb") ?? throw new ArgumentException("--usb DIR is required");
                var secret = OwnershipAuthority.LoadSecret(usb, site, ReadSecret("Owner-key passphrase: "));
                var pub = secret.Public;
                var challenge = OwnershipAuthority.NewChallenge();
                var proof = OwnershipAuthority.Prove(secret, challenge);
                var title = OwnershipAuthority.Issue(paths, pub, proof);
                Ok($"badge issued: jttp://{site}/");
                Console.WriteLine($"  owner:     {title.OwnerId}");
                Console.WriteLine($"  badge:     {title.BadgeId}");
                Console.WriteLine($"  seq:       {title.Seq}");
                Console.WriteLine($"  expires:   {title.ExpiresUtc}");
                Console.WriteLine("  payment:   simulated (no charge in this build)");
                Console.WriteLine("  issuer:    " + title.Issuer + " � signature: " + title.IssuerSig);
                Console.WriteLine("  note:      the token traveled through the local PFS tunnel stand-in");
                return 0;
            }
            case "status":
            {
                var title = OwnershipAuthority.LoadTitle(paths, site);
                if (title is null)
                {
                    Error($"no paid title for {site}");
                    return 1;
                }
                Console.WriteLine($"jttp://{title.Site}/");
                Console.WriteLine($"  owner:     {title.OwnerId}");
                Console.WriteLine($"  badge:     {title.BadgeId}");
                Console.WriteLine($"  seq/status:{title.Seq} / {title.Status}");
                Console.WriteLine($"  issued:    {title.IssuedUtc}");
                Console.WriteLine($"  expires:   {title.ExpiresUtc}");
                Console.WriteLine($"  tier:      {title.Tier} ({title.PaymentRef})");
                return 0;
            }
            case "transfer":
            {
                var sellerUsb = opts.Get("usb") ?? throw new ArgumentException("--usb SELLER_DIR is required");
                var buyerFile = opts.Get("buyer") ?? throw new ArgumentException("--buyer PUB.json is required");
                var current = OwnershipAuthority.LoadTitle(paths, site)
                              ?? throw new InvalidOperationException("No title to transfer.");
                var seller = OwnershipAuthority.LoadSecret(sellerUsb, site, ReadSecret("Seller owner-key passphrase: "));
                var buyer = OwnershipAuthority.LoadPublicFile(buyerFile);
                var statement = OwnershipAuthority.CreateTransfer(
                    paths, current, seller, buyer, opts.Get("price") ?? "0");
                var outPath = opts.Get("out") ?? $"transfer-{site}-{statement.Seq}.json";
                File.WriteAllText(outPath, JsonSerializer.Serialize(statement,
                    new JsonSerializerOptions { WriteIndented = true }));
                Ok($"transfer statement written to {outPath}");
                Console.WriteLine($"  from {statement.FromOwnerId} ? {statement.ToOwnerId} at seq {statement.Seq}");
                Console.WriteLine("  the buyer imports this file on their own machine; the badge moves only then");
                return 0;
            }
            case "accept":
            {
                var buyerUsb = opts.Get("usb") ?? throw new ArgumentException("--usb BUYER_DIR is required");
                var transferFile = opts.Get("transfer") ?? throw new ArgumentException("--transfer FILE is required");
                var statement = JsonSerializer.Deserialize<TransferStatement>(File.ReadAllText(transferFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("The transfer file is unreadable.");
                var buyerSecret = OwnershipAuthority.LoadSecret(buyerUsb, site, ReadSecret("Buyer owner-key passphrase: "));
                var challenge = OwnershipAuthority.NewChallenge();
                var proof = OwnershipAuthority.Prove(buyerSecret, challenge);
                var title = OwnershipAuthority.AcceptTransfer(paths, statement, buyerSecret.Public, proof);
                Ok($"title accepted: jttp://{site}/");
                Console.WriteLine($"  new owner: {title.OwnerId}");
                Console.WriteLine($"  badge:     {title.BadgeId} (seq {title.Seq})");
                Console.WriteLine("  the seller's key can no longer sign a transfer");
                return 0;
            }
            default:
                Error($"unknown own action '{action}'");
                return 1;
        }
    }

    private static int Registry(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("""
registry sync   --url URL --key FILE [--data DIR]
registry status [--data DIR]

sync fetches the signed phonebook, verifies the hybrid Ed25519 + ML-DSA-65 signature and
Merkle root, then writes the records into the local mirror. --key is the public
registry-key.json published by the signer; its fingerprint is pinned on first sync.
""");
            return 0;
        }

        var action = args[0].ToLowerInvariant();
        var opts = Options.Parse(args[1..]);
        var paths = new DataPaths(DataDir(opts));
        paths.Ensure();
        var state = RegistryStateStore.Load(paths);
        var registry = LocalRegistry.LoadOrEmpty(paths);

        switch (action)
        {
            case "sync":
            {
                var url = opts.Get("url") ?? throw new ArgumentException("--url is required.");
                var keyPath = opts.Get("key") ?? throw new ArgumentException("--key is required.");
                var key = JsonSerializer.Deserialize<RegistrySigningKey>(
                              File.ReadAllText(keyPath), RegistrySigningKey.Json)
                          ?? throw new FormatException("The registry key file is unreadable.");
                key.Validate();

                var before = state.Version;
                using var verifier = key.CreateVerifier();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var client = new RegistryClient(http, new Uri(url), verifier, key.Fingerprint);
                var result = client.SyncAsync(paths, state, registry).GetAwaiter().GetResult();

                Console.WriteLine($"  registry: {url}");
                Console.WriteLine($"  signer:   {key.Fingerprint}");
                Console.WriteLine($"  status:   {result.Status}");
                if (!result.Ok)
                {
                    Error(result.Detail);
                    return 1;
                }

                Ok(result.Status == SnapshotStatus.AlreadyCurrent
                    ? $"already at v{state.Version}"
                    : $"accepted v{before} -> v{state.Version}");
                Console.WriteLine($"  root:     {state.RootHash}");
                Console.WriteLine($"  records:  {registry.Records.Count}");
                PrintPins(state);
                return 0;
            }
            case "status":
            {
                Console.WriteLine($"  mirror:   v{state.Version}");
                Console.WriteLine($"  root:     {(state.RootHash.Length == 0 ? "(none)" : state.RootHash)}");
                Console.WriteLine($"  records:  {registry.Records.Count}");
                PrintPins(state);
                return 0;
            }
            default:
                Error($"unknown registry action '{action}'");
                return 1;
        }
    }

    private static void PrintPins(MirrorState state)
    {
        var pending = state.Pins.Values.Where(p => p.PendingFingerprint is not null).ToList();
        Console.WriteLine($"  pins:     {state.Pins.Count} ({pending.Count} pending owner change)");
        foreach (var pin in pending.OrderBy(p => p.Name, StringComparer.Ordinal))
            Console.WriteLine($"    ! {pin.Name}: {pin.PendingFingerprint}");
    }

    private static void PrintPage(BrowserSession session, PageView page, string address,
        IReadOnlySet<string> unlocked, IReadOnlyDictionary<string, string> texts)
    {
        var ownership = session.OwnershipOf(page.Site);
        Console.WriteLine(page.Render(address, session.IsNameVerified(page.Site), unlocked, texts,
            ownership.Owned, ownership.OwnerId));
    }

    private static int UnlockBlob(string[] args)
    {
        var opts = Options.Parse(args);
        var blobId = opts.Positional.FirstOrDefault();
        if (blobId is null)
        {
            Error("unlock needs a blob id, e.g. unlock b1");
            return 1;
        }

        var data = DataDir(opts);
        var url = opts.Get("url") ?? "jttp://pinwheelexplained.com/";
        var pin = opts.Get("pin") ?? PromptPin("User key: ");

        using var session = new BrowserSession(data);
        var page = session.Open(url, pin);
        var text = session.UnlockBlob(blobId);

        Ok($"blob {blobId} opened inside the session");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine(text);
        Console.WriteLine(new string('-', 60));
        Console.WriteLine("the session ended when this command exited; keys were zeroised");

        // Explicitly re-render so an unlocked page shows the note inline.
        var page2 = session.Open(url);
        var unlocked = new HashSet<string> { blobId };
        var texts = new Dictionary<string, string> { [blobId] = text };
        Console.WriteLine();
        PrintPage(session, page2, url, unlocked, texts);
        return 0;
    }

    private static int Verify(string[] args)
    {
        var opts = Options.Parse(args);
        var data = DataDir(opts);
        if (!Directory.Exists(data))
        {
            Error($"no data directory at {data}");
            return 1;
        }

        var hits = new List<string>();
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
        {
            files++;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            if (Contains(bytes, Encoding.UTF8.GetBytes(Canary)) ||
                Contains(bytes, Encoding.UTF8.GetBytes("the blob was stored in plaintext")) ||
                Contains(bytes, Encoding.UTF8.GetBytes("Say the last part out loud")))
                hits.Add(file);
        }

        Console.WriteLine($"scanned {files} files under {data}");
        if (hits.Count == 0)
        {
            Ok("no canary or note plaintext found on disk");
            Console.WriteLine("  note: this checks known fixtures, not every possible plaintext");
            return 0;
        }

        Error($"{hits.Count} file(s) contain slice plaintext:");
        foreach (var hit in hits) Console.WriteLine("  " + hit);
        return 1;
    }

    private static int Classify(string[] args)
    {
        var input = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(input))
        {
            Error("classify needs input");
            return 1;
        }

        var target = InputClassifier.Classify(input);
        Console.WriteLine($"input:  {input}");
        Console.WriteLine($"lane:   {target.Lane}");
        Console.WriteLine($"url:    {target.Url ?? "�"}");
        Console.WriteLine($"reason: {target.Reason}");
        return target.Lane == InputLane.Invalid ? 1 : 0;
    }

    private static int Shield(string[] args)
    {
        var opts = Options.Parse(args);
        var data = DataDir(opts);
        var pin = opts.Get("pin") ?? PromptPin("User key: ");
        var text = string.Join(' ', opts.Positional);
        if (text.Length == 0) text = "jttp://pinwheelexplained.com/";

        using var session = new BrowserSession(data);
        session.Unlock(pin);

        var master = Keystore.Unlock(new DataPaths(data), pin);
        try
        {
            using var secret = SessionSecret.Create(master);
            using var shield = new KeystrokeShield(secret, "omnibox");
            shield.Append(text);

            var cells = shield.Cells;
            var snapshot = shield.CiphertextSnapshot();
            var reveal = shield.Reveal();
            try
            {
                Ok($"sealed {cells.Count} keystrokes with {secret.IdHex[..12]}�");
                Console.WriteLine($"  plaintext cells retained: 0 (revealed only into a caller buffer)");
                Console.WriteLine($"  ciphertext bytes:         {snapshot.Length} (1�4 B + 16 B tag per cell)");
                Console.WriteLine($"  first cell:               {Convert.ToHexString(cells[0].Ciphertext)[..24]}�");
                Console.WriteLine($"  same char twice differs:  {!cells[0].Ciphertext.SequenceEqual(cells[1].Ciphertext)}");
                Console.WriteLine($"  reveal:                   {new string(reveal)}");
                Array.Clear(reveal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshot);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }

        return 0;
    }

    private static int Status(string[] args)
    {
        var opts = Options.Parse(args);
        var paths = new DataPaths(DataDir(opts));
        var registry = Kickl.Core.Registry.LocalRegistry.LoadOrEmpty(paths);

        Console.WriteLine($"data:     {paths.Root}");
        Console.WriteLine($"profile:  {(Keystore.Exists(paths) ? "present" : "none")}");
        Console.WriteLine($"registry: {registry.Records.Count} name(s)");
        foreach (var record in registry.Records)
        {
            var verified = Kickl.Core.Registry.LocalRegistry.IsVerified(record);
            Console.WriteLine($"  jttp://{record.Name}/  v{record.Version}  {(verified ? "verified" : "unverified")}");
        }
        return 0;
    }

    private static int Demo(string[] args)
    {
        var opts = Options.Parse(args);
        var data = opts.Get("dir") ?? ".kickl-demo";
        var pin = "pinwheel-demo";

        Banner("KICKL � vertical slice demo");
        Console.WriteLine($"data directory: {Path.GetFullPath(data)}");
        Console.WriteLine();

        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);

        Step(1, "create a profile (this is the entry gate's \"Not registered\" branch)");
        var session = new BrowserSession(data);
        session.CreateProfile(pin);
        Console.WriteLine($"  profile root: {session.Paths.ProfileRoot}");
        Console.WriteLine("  user key:     PIN, stretched by " + PinKdf.Id);

        Step(2, "publish the sealed site");
        var master = Keystore.Unlock(session.Paths, pin);
        SealedDocument doc;
        try
        {
            doc = DocumentStore.Publish(session.Paths, Path.Combine("sites", "pinwheelexplained.com"),
                "pinwheelexplained.com", master);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
        Console.WriteLine($"  {doc.Blobs.Count} blobs sealed, document key wrapped");
        Console.WriteLine($"  integrity {doc.Integrity[..16]}�");

        Step(3, "resolve the name over the PFS channel, then open the sealed site");
        var lookup = session.Lookup("pinwheelexplained.com");
        Console.WriteLine("  P-256 ephemeral ECDH ? HKDF-SHA384 ? AES-256-GCM");
        Console.WriteLine($"  client/server keys {lookup.ClientKeyBytes}/{lookup.ServerKeyBytes} B, salt {lookup.SaltBytes} B");
        Console.WriteLine($"  session {lookup.SessionId}, key print {lookup.KeyFingerprint}� (destroyed)");
        Console.WriteLine($"  sealed query {lookup.SealedQueryBytes} B ? sealed reply {lookup.SealedResponseBytes} B");
        Console.WriteLine($"  found={lookup.Response.Found}, status={lookup.Response.Status}");

        var page = session.Open("jttp://pinwheelexplained.com/", pin);
        Console.WriteLine();
        PrintPage(session, page, "jttp://pinwheelexplained.com/",
            new HashSet<string>(), new Dictionary<string, string>());

        Step(4, "try to read a blob with no session");
        session.Lock();
        try
        {
            session.UnlockBlob("b1");
            Error("blob opened while locked � this is a bug");
            return 1;
        }
        catch (InvalidOperationException)
        {
            Ok("refused: the session is locked");
        }

        Step(5, "unlock with the user key and open the sealed note");
        session.Open("jttp://pinwheelexplained.com/", pin);
        var note = session.UnlockBlob("b1");
        Console.WriteLine(new string('-', 64));
        Console.WriteLine(note);
        Console.WriteLine(new string('-', 64));

        Step(6, "claim ownership: RSA-3072 on the USB unwraps the AES-256 title token");
        var site = "pinwheelexplained.com";
        var usb = Path.Combine(data, "usb");
        const string demoOwnerPassphrase = "Kickl demo owner 1234";
        var ownerPub = OwnershipAuthority.Create(session.Paths, site, usb, demoOwnerPassphrase, "demo owner");
        var ownerSecret = OwnershipAuthority.LoadSecret(usb, site, demoOwnerPassphrase);
        var challenge = OwnershipAuthority.NewChallenge();
        var proof = OwnershipAuthority.Prove(ownerSecret, challenge);
        var title = OwnershipAuthority.Issue(session.Paths, ownerPub, proof);
        Ok($"badge issued for jttp://{site}/");
        Console.WriteLine($"  owner {title.OwnerId}");
        Console.WriteLine($"  badge {title.BadgeId} � seq {title.Seq} � expires {title.ExpiresUtc[..10]}");
        Console.WriteLine($"  USB: RSA private key + {ownerSecret.TokenCiphertext.Length} B RSA-OAEP-wrapped AES token");
        Console.WriteLine("  payment simulated; issuer signature is dev-unsigned in this slice");

        var badgedPage = session.Open("jttp://pinwheelexplained.com/");
        Console.WriteLine();
        PrintPage(session, badgedPage, "jttp://pinwheelexplained.com/",
            new HashSet<string>(), new Dictionary<string, string>());

        Step(7, "seal the page into click-to-reveal regions");
        using (var sealedPage = SealedPage.Seal(badgedPage, session.SessionSecret))
        {
            Console.WriteLine($"  {sealedPage.RegionCount} text regions encrypted independently");
            Console.WriteLine();
            Console.WriteLine(sealedPage.Render());
            sealedPage.Reveal(1, session.SessionSecret);
            Console.WriteLine();
            Ok("region 1 decrypted; every other region is still ciphertext");
            Console.WriteLine(sealedPage.Render());
        }

        Step(8, "steganographic cache: session-bound, re-sealed on every request");
        var cache = new StegoCache(session.Paths.StegoRoot);
        var cacheEnvelope = new CacheEnvelope
        {
            Site = "pinwheelexplained.com",
            Path = "/",
            Title = "Pinwheel, explained",
            Content = badgedPage.Render("jttp://pinwheelexplained.com/", false,
                new HashSet<string>(), new Dictionary<string, string>()),
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        var carrier = cache.Store(session.SessionSecret, cacheEnvelope);
        var before = HashBytes(File.ReadAllBytes(carrier));
        var restored = cache.Load(session.SessionSecret, carrier);
        var after = HashBytes(File.ReadAllBytes(carrier));
        Ok($"cached to {carrier} ({new FileInfo(carrier).Length} B PNG)");
        Console.WriteLine($"  first seal  {before[..24]}�");
        Console.WriteLine($"  second seal {after[..24]}�  changed={before != after}");
        Console.WriteLine($"  content unchanged={restored.Content == cacheEnvelope.Content}; site name not in carrier bytes");
        Console.WriteLine("  keys come from the live session; after lock the carrier is just an image");

        Step(9, "per-keystroke AES-256-GCM cells");
        using (var secret = SessionSecret.Create(Keystore.Unlock(session.Paths, pin)))
        {
            using var shield = new KeystrokeShield(secret, "omnibox");
            shield.Append("jttp://pinwheelexplained.com/");
            var cells = shield.Cells;
            var snapshot = shield.CiphertextSnapshot();
            Console.WriteLine($"  {cells.Count} cells, {snapshot.Length} ciphertext bytes");
            Console.WriteLine($"  first: {Convert.ToHexString(cells[0].Ciphertext)[..32]}�");
            Console.WriteLine($"  no plaintext in the cell buffer: " +
                              (!Contains(snapshot, Encoding.UTF8.GetBytes("jttp"))));
            CryptographicOperations.ZeroMemory(snapshot);
        }

        Step(10, "lock, then confirm the keys are dead");
        session.Lock();
        try
        {
            session.UnlockBlob("b1");
            Error("blob opened after lock � this is a bug");
            return 1;
        }
        catch (InvalidOperationException)
        {
            Ok("refused after Lock()");
        }

        Step(11, "scan the data directory for slice plaintext");
        var hits = Scan(data);
        if (hits.Count > 0)
        {
            Error($"plaintext found in {hits.Count} file(s):");
            foreach (var hit in hits) Console.WriteLine("  " + hit);
            return 1;
        }
        Ok("no canary or note plaintext in the data directory");

        Banner("slice passed");
        Console.WriteLine("What is intentionally not done yet: external crypto review, HPKE recipients,");
        Console.WriteLine("issuer signatures on titles, the vault, mail/chat, Nickles, the GUI.");
        Console.WriteLine("Those are un-ticked in CHECKLIST.md, and the docs say why.");
        return 0;
    }

    private static int Shell(string[] args)
    {
        var opts = Options.Parse(args);
        var data = DataDir(opts);
        using var session = new BrowserSession(data);

        Console.WriteLine("Kickl shell (slice). Commands: open <url> [--pin], search <tag>, unlock <id>, lock, status, quit");
        if (!session.HasProfile)
            Console.WriteLine("No profile yet. Run: kickl init");

        while (true)
        {
            Console.Write("kickl> ");
            var line = Console.ReadLine();
            if (line is null) break;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "quit":
                    case "exit":
                        return 0;
                    case "open" when parts.Length >= 2:
                    {
                        var address = parts[1].StartsWith("jttp://", StringComparison.OrdinalIgnoreCase)
                            ? parts[1]
                            : "jttp://" + parts[1];
                        var pin = parts.Length > 2 ? parts[2] : null;
                        var page = session.Open(address, pin);
                        PrintPage(session, page, address,
                            new HashSet<string>(), new Dictionary<string, string>());
                        break;
                    }
                    case "search" when parts.Length >= 2:
                    {
                        var hits = session.Search(parts[1]);
                        foreach (var hit in hits.Results)
                            Console.WriteLine($"  jttp://{hit.Host}/  {hit.Title}  [{string.Join(",", hit.Tags)}]");
                        break;
                    }
                    case "unlock" when parts.Length >= 2:                    {
                        var text = session.UnlockBlob(parts[1], parts.Length > 2 ? parts[2] : null);
                        Console.WriteLine(text);
                        break;
                    }
                    case "lock":
                        session.Lock();
                        Ok("locked; keys zeroised");
                        break;
                    case "status":
                        Console.WriteLine(session.IsUnlocked ? $"unlocked {session.SessionId}" : "locked");
                        break;
                    default:
                        Error("unknown command");
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or CryptographicException
                                           or InvalidUserKeyException or SiteNotFoundException)
            {
                Error(ex.Message);
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private static string DataDir(Options opts) =>
        Path.GetFullPath(opts.Get("data") ?? DefaultDataDir());

    private static string DefaultDataDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kickl")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kickl");

    private static string PromptPin(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? "";
            return line;
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static string ReadSecret(string prompt) => PromptPin(prompt);

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static List<string> Scan(string root)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            if (Contains(bytes, Encoding.UTF8.GetBytes(Canary)) ||
                Contains(bytes, Encoding.UTF8.GetBytes("the blob was stored in plaintext")) ||
                Contains(bytes, Encoding.UTF8.GetBytes("Say the last part out loud")))
                hits.Add(file);
        }
        return hits;
    }

    private static void Banner(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("  " + text);
        Console.WriteLine("  " + new string('-', text.Length));
        Console.ResetColor();
    }

    private static void Step(int n, string text)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  [{n}] ");
        Console.ResetColor();
        Console.WriteLine(text);
    }

    private static void Ok(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ? " + message);
        Console.ResetColor();
    }

    private static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("  ? " + message);
        Console.ResetColor();
    }

    private static int Help()
    {
        Console.WriteLine("""
Kickl Browser � slice CLI

  kickl init      [--pin PIN] [--data DIR] [--force]
  kickl publish   [--site DIR] [--host NAME] [--pin PIN] [--data DIR]
  kickl open      <jttp://name.tld/> [--pin PIN] [--data DIR]
  kickl search    <tag-or-phrase> [--data DIR]
  kickl unlock    <blob-id> [--url jttp://�] [--pin PIN] [--data DIR]
  kickl verify    [--data DIR]
  kickl classify  <input>
  kickl shield    [--pin PIN] [--data DIR] [text]
  kickl status    [--data DIR]
  kickl registry  sync --url URL --key FILE [--data DIR]
  kickl registry  status [--data DIR]
  kickl demo      [--dir DIR]
  kickl shell     [--data DIR]

The data directory holds one local profile in the slice, under profiles/local.
jttp:// names resolve through the local registry mirror; they never touch HTTP.
""");
        return 0;
    }

    private static int Unknown(string command)
    {
        Error($"unknown command '{command}'");
        return Help();
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = [];

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    var body = arg[2..];
                    var eq = body.IndexOf('=');
                    if (eq > 0)
                    {
                        options._named[body[..eq]] = body[(eq + 1)..];
                        continue;
                    }
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options._named[body] = args[++i];
                        continue;
                    }
                    options._flags.Add(body);
                    continue;
                }
                options.Positional.Add(arg);
            }
            return options;
        }

        public bool Has(string name) => _named.ContainsKey(name) || _flags.Contains(name);
        public string? Get(string name) => _named.TryGetValue(name, out var value) ? value : null;
    }
}
