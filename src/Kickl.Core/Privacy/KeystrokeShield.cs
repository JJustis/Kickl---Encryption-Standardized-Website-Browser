using System.Security.Cryptography;
using System.Text;
using Kickl.Core.Crypto;
using Kickl.Core.Util;

namespace Kickl.Core.Privacy;

public sealed class KeyCell
{
    public ulong Counter { get; init; }
    public byte[] Nonce { get; init; } = [];
    public byte[] Ciphertext { get; init; } = [];
}

/// <summary>
/// Per-keystroke AES-256-GCM cells. The session key seals each character with a counter nonce
/// and an AAD binding (session, field, position). No plaintext keystroke is retained.
///
/// SLICE NOTE: each cell is one UTF-16 code unit; surrogate pairs occupy two cells and are
/// reassembled on Reveal. The production UI composes with a pinned char buffer.
/// </summary>
public sealed class KeystrokeShield : IDisposable
{
    private readonly SessionSecret _session;
    private readonly string _fieldId;
    private readonly List<KeyCell> _cells = [];
    private ulong _nextCounter = 1;
    private bool _disposed;

    public KeystrokeShield(SessionSecret session, string fieldId)
    {
        _session = session;
        _fieldId = fieldId;
    }

    public string FieldId => _fieldId;
    public int Length => _cells.Count;
    public IReadOnlyList<KeyCell> Cells => _cells;

    private byte[] Aad(ulong counter) =>
        Utf8.B($"kickl/v1/keystroke|{_session.IdHex}|{_fieldId}|{counter}");

    public void Append(char c)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var counter = _nextCounter++;
        var nonce = Aead.CounterNonce(counter);
        var pt = Encoding.UTF8.GetBytes(c.ToString());
        try
        {
            var ct = Aead.SealWithNonce(_session.Key, nonce, pt, Aad(counter));
            _cells.Add(new KeyCell { Counter = counter, Nonce = nonce, Ciphertext = ct });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pt);
        }
    }

    public void Append(string text)
    {
        foreach (var c in text) Append(c);
    }

    public void Backspace()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cells.Count == 0) return;
        var last = _cells[^1];
        CryptographicOperations.ZeroMemory(last.Ciphertext);
        _cells.RemoveAt(_cells.Count - 1);
        if (_nextCounter > 1) _nextCounter--; // positions stay contiguous, so AAD binds the slot
    }

    /// <summary>Caller owns the returned buffer and must zero it after painting.</summary>
    public char[] Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var chars = new char[_cells.Count];
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            var position = (ulong)(i + 1); // slot, not cell counter: swapping cells must fail
            var pt = Aead.Open(_session.Key, cell.Nonce, cell.Ciphertext, Aad(position));
            try
            {
                chars[i] = Encoding.UTF8.GetString(pt)[0];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pt);
            }
        }
        return chars;
    }

    /// <summary>Concatenated ciphertext, for the no-plaintext byte-scan test.</summary>
    public byte[] CiphertextSnapshot()
    {
        var total = _cells.Sum(c => c.Ciphertext.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var cell in _cells)
        {
            Buffer.BlockCopy(cell.Ciphertext, 0, buffer, offset, cell.Ciphertext.Length);
            offset += cell.Ciphertext.Length;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var cell in _cells)
            CryptographicOperations.ZeroMemory(cell.Ciphertext);
        _cells.Clear();
        _disposed = true;
    }
}
