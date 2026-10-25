using System.Security.Cryptography;

namespace Kickl.Core.Identity;

/// <summary>
/// The 8-digit "phone number" alias for an identity: six assigned digits plus two
/// ISO 7064 MOD 97-10 check digits. It is a directory alias, never an authenticator.
/// </summary>
public static class ShortCode
{
    public const int Length = 8;
    private const int SeedDigits = 6;
    private const int Modulus = 97;

    /// <summary>Build the canonical 8-digit code for a six-digit assignment.</summary>
    public static string Create(int seed)
    {
        if (seed < 0 || seed >= 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(seed), "The seed must be a six-digit number.");

        var check = Modulus + 1 - (seed * 100 % Modulus);
        return seed.ToString("D6") + check.ToString("D2");
    }

    /// <summary>Assign a random code. The registry makes it unique; this only formats it.</summary>
    public static string Create() => Create(RandomNumberGenerator.GetInt32(1_000_000));

    public static string Format(string code) =>
        NormalizeDigits(code) is { Length: Length } digits ? digits[..4] + " " + digits[4..] : code;

    public static bool IsValid(string? input) => TryParse(input, out _, out _);

    public static bool TryParse(string? input, out string code, out string error)
    {
        code = "";
        error = "";

        var digits = NormalizeDigits(input ?? "");
        if (digits.Length != Length)
        {
            error = $"A short code is exactly {Length} digits.";
            return false;
        }

        if (long.Parse(digits) % Modulus != 1)
        {
            error = "The short code check digits are invalid.";
            return false;
        }

        code = digits;
        return true;
    }

    private static string NormalizeDigits(string input)
    {
        Span<char> digits = stackalloc char[Length];
        var count = 0;
        foreach (var c in input)
        {
            if (c is ' ' or '-' or '\u00a0') continue;
            if (c < '0' || c > '9') return "";
            if (count == Length) return "";
            digits[count++] = c;
        }
        return count == Length ? new string(digits) : "";
    }
}
