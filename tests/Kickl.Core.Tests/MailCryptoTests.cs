using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Mail;
using Kickl.Core.Util;

namespace Kickl.Core.Tests;

public sealed class MailCryptoTests
{
    [Fact]
    public void IdentityBundle_IsSelfSigned()
    {
        using var identity = MailIdentity.Generate("nyx");
        Assert.True(identity.Bundle.VerifySignature());
        Assert.Equal(8, identity.Bundle.ShortId.Length);
        Assert.Equal("nyx", identity.Bundle.Handle);
        MailIdentityBundle.Parse(identity.Bundle.ToJson()).Validate();
    }

    [Fact]
    public void SealAndOpen_RoundTrips_NinetyThousandCharacters()
    {
        using var sender = MailIdentity.Generate("sender");
        using var recipient = MailIdentity.Generate("recipient");
        var body = new string('z', 90_000);
        var sealedMail = MailCrypto.Seal(sender, recipient.Bundle, "long letter", body);
        try
        {
            Assert.Equal(90000, body.Length);
            Assert.True(MailImage.HasPayload(sealedMail.Image));
            Assert.DoesNotContain("long letter", Encoding.UTF8.GetString(sealedMail.Image));
            var opened = MailCrypto.Open(sealedMail.Envelope, sealedMail.Image, recipient.Rsa,
                recipient.Bundle.ShortId);
            try
            {
                Assert.Equal("long letter", opened.Content.Subject);
                Assert.Equal(body, opened.Content.Body);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(opened.MessageKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealedMail.MessageKey);
        }
    }

    [Fact]
    public void Open_RejectsTamperedImage()
    {
        using var sender = MailIdentity.Generate("sender");
        using var recipient = MailIdentity.Generate("recipient");
        var sealedMail = MailCrypto.Seal(sender, recipient.Bundle, "subject", "body");
        try
        {
            var tampered = sealedMail.Image.ToArray();
            tampered[100] ^= 0x40;
            Assert.Throws<CryptographicException>(() =>
                MailCrypto.Open(sealedMail.Envelope, tampered, recipient.Rsa, recipient.Bundle.ShortId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealedMail.MessageKey);
        }
    }

    [Fact]
    public void Open_RejectsWrongRecipient()
    {
        using var sender = MailIdentity.Generate("sender");
        using var recipient = MailIdentity.Generate("recipient");
        using var other = MailIdentity.Generate("other");
        var sealedMail = MailCrypto.Seal(sender, recipient.Bundle, "subject", "body");
        try
        {
            Assert.ThrowsAny<CryptographicException>(() =>
                MailCrypto.Open(sealedMail.Envelope, sealedMail.Image, other.Rsa, other.Bundle.ShortId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sealedMail.MessageKey);
        }
    }

    [Fact]
    public void MailStore_RequiresPassword_AndPersistsSealedImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "kickl-mail-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DataPaths(root, "local");
            paths.Ensure();
            using var sender = MailIdentity.Generate("sender");
            var store = new MailStore(paths);
            store.CreateVault("correct horse battery");
            var recipient = store.EnsureIdentity("recipient");
            var sealedMail = MailCrypto.Seal(sender, recipient.Bundle, "hello", "sealed body");
            try
            {
                store.SaveIncoming(sealedMail.Envelope, sealedMail.Image, sealedMail.MessageKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sealedMail.MessageKey);
            }

            Assert.Equal("sealed body", store.Read(sealedMail.Envelope.Id).Body);
            var databaseBytes = File.ReadAllBytes(paths.MailDatabase);
            Assert.Equal(-1, IndexOf(databaseBytes, Encoding.UTF8.GetBytes("sealed body")));
            Assert.Equal(-1, IndexOf(databaseBytes, Encoding.UTF8.GetBytes("hello")));
            store.Lock();
            Assert.False(store.IsUnlocked);
            Assert.Throws<CryptographicException>(() => store.Unlock("wrong password"));
            Assert.False(store.IsUnlocked);

            store.Unlock("correct horse battery");
            Assert.Equal("sealed body", store.Read(sealedMail.Envelope.Id).Body);
            Assert.Single(store.List());
            store.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            if (match) return i;
        }
        return -1;
    }
}
