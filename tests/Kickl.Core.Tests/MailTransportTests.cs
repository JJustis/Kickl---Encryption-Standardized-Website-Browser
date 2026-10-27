using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Kickl.Core.Mail;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public sealed class MailTransportTests
{
    /// <summary>A recipient with a real vault, identity and TLS listener on a free loopback port. Nothing touches the registry.</summary>
    private sealed class Recipient : IDisposable
    {
        private readonly TempDir _temp = new();
        public MailService Service { get; }
        public MailIdentityBundle Bundle => Service.Store.Bundle!;
        public string Endpoint => MailEndpoint.Loopback(Bundle.Endpoint);

        public Recipient()
        {
            Service = new MailService(new DataPaths(_temp.Path));
            Service.Store.CreateVault("correct horse battery");
            Service.Store.EnsureIdentity("rcpt");
            Service.StartListener();
        }

        public void Dispose()
        {
            Service.Dispose();
            _temp.Dispose();
        }
    }

    private static MailIdentityBundle At(MailIdentityBundle bundle, string endpoint) => new()
    {
        V = bundle.V, Id = bundle.Id, ShortId = bundle.ShortId, Handle = bundle.Handle, Endpoint = endpoint,
        RsaPublicKey = bundle.RsaPublicKey, MlDsaPublicKey = bundle.MlDsaPublicKey, CreatedAt = bundle.CreatedAt, Sig = bundle.Sig
    };

    private static async Task DeliverAsync(Recipient recipient, MailIdentityBundle target, MailIdentity sender, string subject = "hi")
    {
        var sealedMail = MailCrypto.Seal(sender, recipient.Bundle, subject, "body");
        using var client = new MailClient();
        await client.DeliverAsync(target, sealedMail.Envelope, sealedMail.Image);
    }

    [Fact]
    public async Task Delivery_OverPinnedTls_ReachesTheMailbox()
    {
        using var recipient = new Recipient();
        using var sender = MailIdentity.Generate("alice");
        await DeliverAsync(recipient, At(recipient.Bundle, recipient.Endpoint), sender, "secret subject");
        Assert.Equal(1, recipient.Service.LastDelivered);
    }

    [Fact]
    public async Task WrongPin_IsRefused_AndNothingIsStored()
    {
        using var recipient = new Recipient();
        using var sender = MailIdentity.Generate("alice");
        var wrong = MailEndpoint.WithPin(recipient.Endpoint, new string('A', MailTls.PinLength));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => DeliverAsync(recipient, At(recipient.Bundle, wrong), sender));
        Assert.IsType<AuthenticationException>(ex.GetBaseException());
        Assert.Equal(0, recipient.Service.LastDelivered);
    }

    [Fact]
    public async Task PlainHttpEndpoint_IsRefused_WithoutConnecting()
    {
        using var recipient = new Recipient();
        using var sender = MailIdentity.Generate("alice");
        var http = recipient.Endpoint.Replace("https://", "http://", StringComparison.Ordinal);
        await Assert.ThrowsAsync<MailServiceException>(() => DeliverAsync(recipient, At(recipient.Bundle, http), sender));
        var unpinned = recipient.Endpoint[..recipient.Endpoint.IndexOf('#')];
        await Assert.ThrowsAsync<MailServiceException>(() => DeliverAsync(recipient, At(recipient.Bundle, unpinned), sender));
    }

    [Fact]
    public async Task Impostor_WithAnotherKey_IsRefused()
    {
        using var recipient = new Recipient();
        using var sender = MailIdentity.Generate("alice");
        using var impostorCert = MailTls.CreateSelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            using var tls = new SslStream(tcp.GetStream());
            try { await tls.AuthenticateAsServerAsync(impostorCert); } catch (Exception) { }
        });
        try
        {
            var target = At(recipient.Bundle, MailEndpoint.WithPin($"https://127.0.0.1:{port}", recipient.Service.Store.Identity!.TlsPin));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => DeliverAsync(recipient, target, sender));
            Assert.IsType<AuthenticationException>(ex.GetBaseException());
        }
        finally { listener.Stop(); await accepted; }
    }

    [Fact]
    public async Task Eavesdropper_SeesNoPlaintext()
    {
        using var recipient = new Recipient();
        using var sender = MailIdentity.Generate("visible-handle-xyz");
        var realPort = MailEndpoint.PortOf(recipient.Endpoint);

        // A transparent TCP proxy that records every byte in both directions
        var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint) proxy.LocalEndpoint).Port;
        var captured = new MemoryStream();
        var relay = Task.Run(async () =>
        {
            using var inbound = await proxy.AcceptTcpClientAsync();
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, realPort);
            async Task Pump(NetworkStream from, NetworkStream to)
            {
                var buffer = new byte[8192];
                int n;
                try
                {
                    while ((n = await from.ReadAsync(buffer)) > 0)
                    {
                        lock (captured) captured.Write(buffer, 0, n);
                        await to.WriteAsync(buffer.AsMemory(0, n));
                    }
                }
                catch (IOException) { }
            }
            await Task.WhenAny(Pump(inbound.GetStream(), outbound.GetStream()), Pump(outbound.GetStream(), inbound.GetStream()));
            await Task.Delay(200);
        });

        var target = At(recipient.Bundle, MailEndpoint.WithPin($"https://127.0.0.1:{proxyPort}", recipient.Service.Store.Identity!.TlsPin));
        await DeliverAsync(recipient, target, sender);
        proxy.Stop();
        await relay;

        string text;
        lock (captured) text = Encoding.Latin1.GetString(captured.ToArray());
        Assert.True(captured.Length > 1000);
        Assert.DoesNotContain("visible-handle-xyz", text);
        Assert.DoesNotContain("/v1/mail/deliver", text);
        Assert.DoesNotContain("POST", text);
        Assert.DoesNotContain(recipient.Bundle.ShortId, text);
    }

    [Fact]
    public async Task PlainHttpRequest_ToTheTlsPort_IsDropped_AndServerSurvives()
    {
        using var recipient = new Recipient();
        var port = MailEndpoint.PortOf(recipient.Endpoint);
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /v1/mail/deliver HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}"));
            var buffer = new byte[256];
            var read = 0;
            try { read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); } catch (IOException) { }
            // Closed, or at most a TLS alert; never an HTTP answer
            Assert.DoesNotContain("HTTP/1.1", Encoding.ASCII.GetString(buffer, 0, read));
        }

        using var sender = MailIdentity.Generate("alice");
        await DeliverAsync(recipient, At(recipient.Bundle, recipient.Endpoint), sender);
        Assert.Equal(1, recipient.Service.LastDelivered);
    }

    [Fact]
    public async Task SilentConnections_AreCut_AtTheDeadline()
    {
        using var recipient = new Recipient();
        var server = (MailDeliveryServer) typeof(MailService).GetField("_listener",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(recipient.Service)!;
        server.ConnectionDeadline = TimeSpan.FromMilliseconds(500);
        var port = MailEndpoint.PortOf(recipient.Endpoint);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var read = await tcp.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, read);   // the server hung up on a peer that never spoke
    }

    [Fact]
    public async Task ConnectionCap_DropsTheExcess_AndRecovers()
    {
        using var recipient = new Recipient();
        var port = MailEndpoint.PortOf(recipient.Endpoint);
        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < MailDeliveryServer.MaxConnections; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                held.Add(c);
            }
            await Task.Delay(300);

            using var extra = new TcpClient();
            await extra.ConnectAsync(IPAddress.Loopback, port);
            var read = await extra.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, read);   // dropped immediately rather than queued
        }
        finally { foreach (var c in held) c.Dispose(); }

        await Task.Delay(500);
        using var sender = MailIdentity.Generate("alice");
        await DeliverAsync(recipient, At(recipient.Bundle, recipient.Endpoint), sender);
        Assert.Equal(1, recipient.Service.LastDelivered);
    }

    [Fact]
    public void OldIdentity_WithoutTlsKey_IsUpgradedAndResigned()
    {
        using var original = MailIdentity.Generate("old");
        var legacy = At(original.Bundle, "http://192.168.1.20:5001");
        legacy.Sig = original.Sign(legacy.CanonicalBytes());
        using var restored = MailIdentity.Restore(legacy, original.Token, original.RsaPkcs8, original.MlDsaPkcs8, null);

        Assert.True(restored.TransportUpgraded);
        Assert.True(restored.Bundle.VerifySignature());
        Assert.Equal($"https://192.168.1.20:5001#pin={restored.TlsPin}", restored.Bundle.Endpoint);
    }

    [Fact]
    public void Identity_RoundTripsItsTlsKey()
    {
        using var original = MailIdentity.Generate("same");
        using var restored = MailIdentity.Restore(original.Bundle, original.Token, original.RsaPkcs8, original.MlDsaPkcs8, original.TlsPkcs12);
        Assert.False(restored.TransportUpgraded);
        Assert.Equal(original.TlsPin, restored.TlsPin);
    }

    [Fact]
    public void Endpoint_WithPin_Normalizes()
    {
        var pin = new string('B', MailTls.PinLength);
        Assert.Equal($"https://10.0.0.5:4000#pin={pin}", MailEndpoint.WithPin("http://10.0.0.5:4000/", pin));
        Assert.Equal($"https://10.0.0.5:4000#pin={pin}", MailEndpoint.WithPin("https://10.0.0.5:4000#pin=old", pin));
        Assert.Throws<ArgumentException>(() => MailEndpoint.WithPin("http://10.0.0.5", pin));
        Assert.Throws<ArgumentException>(() => MailEndpoint.WithPin("ftp://10.0.0.5:1", pin));
        Assert.Throws<ArgumentException>(() => MailEndpoint.WithPin("http://10.0.0.5:4000", "short"));
    }

    [Theory]
    [InlineData("http://10.0.0.5:4000")]
    [InlineData("https://10.0.0.5:4000")]
    [InlineData("https://10.0.0.5:4000#pin=short")]
    [InlineData("https://10.0.0.5:4000#pin=!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void Endpoint_Secure_RejectsDowngrades(string endpoint) =>
        Assert.Throws<FormatException>(() => MailEndpoint.Secure(endpoint));
}

public sealed class FreeChatHardeningTests
{
    [Fact]
    public void ServerAddress_MustBeHttps()
    {
        Assert.Throws<Kickl.Core.FreeChat.FreeChatException>(() => new Kickl.Core.FreeChat.FreeChatClient("http://chat.example.com/api/"));
        Assert.Throws<Kickl.Core.FreeChat.FreeChatException>(() => new Kickl.Core.FreeChat.FreeChatClient("ftp://chat.example.com/api/"));
        using var ok = new Kickl.Core.FreeChat.FreeChatClient("https://chat.example.com/api/");
    }

    [Fact]
    public void KeyFingerprint_IsStable_AndDiffersPerKey()
    {
        using var a = System.Security.Cryptography.MLKem.GenerateKey(System.Security.Cryptography.MLKemAlgorithm.MLKem1024);
        using var b = System.Security.Cryptography.MLKem.GenerateKey(System.Security.Cryptography.MLKemAlgorithm.MLKem1024);
        var ma = new Kickl.Core.FreeChat.FreeChatMember(1, "a", Convert.ToBase64String(a.ExportEncapsulationKey()), false);
        var mb = new Kickl.Core.FreeChat.FreeChatMember(2, "b", Convert.ToBase64String(b.ExportEncapsulationKey()), false);
        Assert.Equal(Kickl.Core.FreeChat.FreeChatClient.KeyFingerprint(ma), Kickl.Core.FreeChat.FreeChatClient.KeyFingerprint(ma));
        Assert.NotEqual(Kickl.Core.FreeChat.FreeChatClient.KeyFingerprint(ma), Kickl.Core.FreeChat.FreeChatClient.KeyFingerprint(mb));
        Assert.Equal("invalid", Kickl.Core.FreeChat.FreeChatClient.KeyFingerprint(ma with { PublicKeyBase64 = "!!" }));
    }
}

public sealed class FreeChatKeyPinTests
{
    [Fact]
    public void ChangedKey_IsNotTrusted_UntilAccepted()
    {
        using var client = new Kickl.Core.FreeChat.FreeChatClient("https://chat.example.com/api/");
        var check = typeof(Kickl.Core.FreeChat.FreeChatClient).GetMethod("CheckPin",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using var first = System.Security.Cryptography.MLKem.GenerateKey(System.Security.Cryptography.MLKemAlgorithm.MLKem1024);
        using var swapped = System.Security.Cryptography.MLKem.GenerateKey(System.Security.Cryptography.MLKemAlgorithm.MLKem1024);
        var real = new Kickl.Core.FreeChat.FreeChatMember(7, "alice", Convert.ToBase64String(first.ExportEncapsulationKey()), false);
        var fake = real with { PublicKeyBase64 = Convert.ToBase64String(swapped.ExportEncapsulationKey()) };

        var saved = 0;
        client.PinsChanged += () => saved++;
        Assert.True((bool) check.Invoke(client, [real])!);          // first sight: pinned
        Assert.Equal(1, saved);
        Assert.True((bool) check.Invoke(client, [real])!);          // same key again: fine
        Assert.False((bool) check.Invoke(client, [fake])!);         // a swapped key gets nothing
        Assert.Contains("alice", client.KeyAlerts);

        // the pin survives a restart
        using var restarted = new Kickl.Core.FreeChat.FreeChatClient("https://chat.example.com/api/");
        restarted.LoadPins(client.KeyPins);
        Assert.False((bool) check.Invoke(restarted, [fake])!);
        Assert.True((bool) check.Invoke(restarted, [real])!);
    }
}

public sealed class ChatSignatureTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8];

    private static (System.Security.Cryptography.MLDsa Key, byte[] Kem) NewMember()
    {
        var key = System.Security.Cryptography.MLDsa.GenerateKey(System.Security.Cryptography.MLDsaAlgorithm.MLDsa65);
        return (key, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    [Fact]
    public void SignedMessage_VerifiesAndStripsToThePlainPicture()
    {
        var (key, kem) = NewMember();
        using var _ = key;
        var wire = Kickl.Core.FreeChat.ChatSignature.Sign(key, Png, 9, 4, 2, 7, "Alice", kem);
        var parsed = Kickl.Core.FreeChat.ChatSignature.TryParse(wire)!;
        Assert.Equal(Png, parsed.Png);
        Assert.True(Kickl.Core.FreeChat.ChatSignature.Verify(parsed, 9, 4, 2, 7, "alice"));
        Assert.Equal(kem, parsed.KemHash);
    }

    [Theory]
    [InlineData(8, 4, 2, 7, "alice")]    // another room
    [InlineData(9, 5, 2, 7, "alice")]    // another epoch
    [InlineData(9, 4, 3, 7, "alice")]    // another position (replay)
    [InlineData(9, 4, 2, 8, "alice")]    // another user id
    [InlineData(9, 4, 2, 7, "mallory")]  // another name
    public void Signature_IsBoundToRoomPositionAndSender(int session, int epoch, int index, int user, string name)
    {
        var (key, kem) = NewMember();
        using var _ = key;
        var parsed = Kickl.Core.FreeChat.ChatSignature.TryParse(Kickl.Core.FreeChat.ChatSignature.Sign(key, Png, 9, 4, 2, 7, "alice", kem))!;
        Assert.False(Kickl.Core.FreeChat.ChatSignature.Verify(parsed, session, epoch, index, user, name));
    }

    [Fact]
    public void TamperedPicture_OrKemHash_FailsVerification()
    {
        var (key, kem) = NewMember();
        using var _ = key;
        var wire = Kickl.Core.FreeChat.ChatSignature.Sign(key, Png, 1, 1, 1, 1, "a", kem);
        var p1 = wire.ToArray(); p1[5] ^= 1;
        Assert.False(Kickl.Core.FreeChat.ChatSignature.Verify(Kickl.Core.FreeChat.ChatSignature.TryParse(p1)!, 1, 1, 1, 1, "a"));
        var p2 = wire.ToArray(); p2[Png.Length + 3] ^= 1;   // inside the signed room-key hash
        Assert.False(Kickl.Core.FreeChat.ChatSignature.Verify(Kickl.Core.FreeChat.ChatSignature.TryParse(p2)!, 1, 1, 1, 1, "a"));
    }

    [Fact]
    public void MalformedTrailers_AreNeverAnException()
    {
        Assert.Null(Kickl.Core.FreeChat.ChatSignature.TryParse(Png));
        Assert.Null(Kickl.Core.FreeChat.ChatSignature.TryParse([]));
        var rng = new Random(5);
        var (key, kem) = NewMember();
        using var _ = key;
        var good = Kickl.Core.FreeChat.ChatSignature.Sign(key, Png, 1, 1, 1, 1, "a", kem);
        for (var i = 0; i < 3000; i++)
        {
            var b = good.ToArray();
            for (var k = 0; k < 1 + i % 5; k++) b[rng.Next(b.Length)] = (byte) rng.Next(256);
            if (i % 3 == 0) b = b[..rng.Next(b.Length)];
            var parsed = Kickl.Core.FreeChat.ChatSignature.TryParse(b);
            if (parsed is not null) Kickl.Core.FreeChat.ChatSignature.Verify(parsed, 1, 1, 1, 1, "a");
        }
    }

    [Fact]
    public void Client_ClassifiesSenders()
    {
        using var client = new Kickl.Core.FreeChat.FreeChatClient("https://chat.example.com/api/");
        var auth = typeof(Kickl.Core.FreeChat.FreeChatClient).GetMethod("Authenticate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Kickl.Core.FreeChat.SenderTrust Check(byte[] payload, string name)
        {
            var args = new object[] { payload, 4, 2, 7, name };
            return (Kickl.Core.FreeChat.SenderTrust) auth.Invoke(client, args)!;
        }

        var (key, kem) = NewMember();
        using var _ = key;
        var (other, _) = NewMember();
        using var __ = other;
        var wire = Kickl.Core.FreeChat.ChatSignature.Sign(key, Png, 0, 4, 2, 7, "alice", kem);

        Assert.Equal(Kickl.Core.FreeChat.SenderTrust.Unsigned, Check(Png, "alice"));
        Assert.Equal(Kickl.Core.FreeChat.SenderTrust.FirstSeen, Check(wire, "alice"));
        Assert.Equal(Kickl.Core.FreeChat.SenderTrust.Verified, Check(wire, "alice"));
        Assert.Equal(Kickl.Core.FreeChat.SenderTrust.Invalid, Check(wire, "mallory"));   // a signed message replayed under another name

        var impostor = Kickl.Core.FreeChat.ChatSignature.Sign(other, Png, 0, 4, 2, 7, "alice", kem);
        Assert.Equal(Kickl.Core.FreeChat.SenderTrust.KeyChanged, Check(impostor, "alice"));
        Assert.Contains("alice", client.KeyAlerts);
    }
}

public sealed class FreeChatForProfileTests
{
    private static readonly System.Reflection.MethodInfo CheckPin = typeof(Kickl.Core.FreeChat.FreeChatClient).GetMethod("CheckPin",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    [Fact]
    public void ForProfile_BuildsAWorkingClient_WithThePinsAndTheSigningKeyWired()
    {
        var known = new string('a', 64);
        IReadOnlyDictionary<string, string>? saved = null;
        using var client = Kickl.Core.FreeChat.FreeChatClient.ForProfile(
            new Dictionary<string, string> { ["bob"] = known }, Kickl.Core.FreeChat.FreeChatClient.NewSigningKey(),
            pins => saved = pins, "https://chat.example.com/api/");

        Assert.Equal(known, client.KeyPins["bob"]);                      // what the profile already knew is loaded
        Assert.Null(saved);

        // learning a member's key for the first time reaches the caller, so the profile can save it
        using var kem = System.Security.Cryptography.MLKem.GenerateKey(System.Security.Cryptography.MLKemAlgorithm.MLKem1024);
        var alice = new Kickl.Core.FreeChat.FreeChatMember(2, "alice", Convert.ToBase64String(kem.ExportEncapsulationKey()), false);
        Assert.True((bool) CheckPin.Invoke(client, [alice])!);
        Assert.NotNull(saved);
        Assert.True(saved!.ContainsKey("alice"));
        Assert.Equal(known, saved["bob"]);
    }

    [Fact]
    public void ForProfile_RefusesABadSigningKey_AndAnInsecureServer_WithoutLeakingAClient()
    {
        Assert.ThrowsAny<Exception>(() => Kickl.Core.FreeChat.FreeChatClient.ForProfile(
            new Dictionary<string, string>(), [1, 2, 3], _ => { }, "https://chat.example.com/api/"));
        Assert.Throws<Kickl.Core.FreeChat.FreeChatException>(() => Kickl.Core.FreeChat.FreeChatClient.ForProfile(
            new Dictionary<string, string>(), Kickl.Core.FreeChat.FreeChatClient.NewSigningKey(), _ => { }, "http://chat.example.com/api/"));
    }
}

public sealed class MailClientRetryTests
{
    private sealed class Scripted(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(script(n));
        }
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string LookupBody(MailIdentity id) =>
        System.Text.Json.JsonSerializer.Serialize(new { bundle = id.Bundle }, MailJson.Options);

    [Fact]
    public async Task ADroppedConnection_IsRetried_AndTheLookupSucceeds()
    {
        using var id = MailIdentity.Generate("rt");
        var handler = new Scripted(n => n < 3 ? throw new HttpRequestException("connection dropped") : Json(System.Net.HttpStatusCode.OK, LookupBody(id)));
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        var bundle = await client.LookupAsync(id.Bundle.ShortId);
        Assert.Equal(id.Bundle.ShortId, bundle.ShortId);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ATimeout_IsRetried()
    {
        using var id = MailIdentity.Generate("rt");
        var handler = new Scripted(n => n == 1
            ? throw new TaskCanceledException("timed out", new TimeoutException())
            : Json(System.Net.HttpStatusCode.OK, LookupBody(id)));
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        await client.LookupAsync(id.Bundle.ShortId);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AnAnswerFromTheServer_EvenAnError_IsFinal_AndNotRetried()
    {
        var handler = new Scripted(_ => Json(System.Net.HttpStatusCode.NotFound, "{\"error\":\"No mail identity has that PFS id.\"}"));
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<MailServiceException>(() => client.LookupAsync("12345678"));
        Assert.Contains("No mail identity", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ANetworkThatNeverWorks_GivesUpAfterTheLimit_WithTheRealError()
    {
        var handler = new Scripted(_ => throw new HttpRequestException("no route"));
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.LookupAsync("12345678"));
        Assert.Equal(MailClient.MaxAttempts, handler.Calls);
    }

    [Fact]
    public async Task ACancelledRequest_IsNotRetried()
    {
        var handler = new Scripted(_ => throw new HttpRequestException("x"));
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LookupAsync("12345678", cts.Token));
        Assert.True(handler.Calls <= 1);
    }

    [Fact]
    public async Task RegisteringAnIdentity_IsRetriedToo()
    {
        using var id = MailIdentity.Generate("rt");
        var handler = new Scripted(n => n < 2 ? throw new HttpRequestException("dropped") : Json(System.Net.HttpStatusCode.Created, "{\"ok\":true}"));
        using var client = new MailClient("https://example.test/api", new HttpClient(handler));
        await client.RegisterAsync(id.Bundle, id.Token);
        Assert.Equal(2, handler.Calls);
    }
}
