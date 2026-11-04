#pragma warning disable CA1822 // Injected service; keeps call sites stable if parameters become configurable.
using System.Security.Cryptography;
using OtpNet;
using Quicker.Kernel.Time;

namespace Quicker.Identity.Security;

/// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s) compatible with every authenticator app, with replay protection.</summary>
public sealed class TotpProvider(IClock clock)
{
    public const int SecretBytes = 20;

    public byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public string ProvisioningUri(byte[] secret, string accountLabel, string issuer = "Quicker")
    {
        var uri = new OtpUri(OtpType.Totp, secret, accountLabel, issuer, OtpHashMode.Sha1, 6, 30);
        return uri.ToString();
    }

    public static string Base32(byte[] secret) => Base32Encoding.ToString(secret);

    /// <summary>Verifies a code within ±1 step and returns the matched time step so it can be stored against replay.</summary>
    public bool Verify(byte[] secret, string code, long lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var totp = new Totp(secret, step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
        var now = clock.UtcNow.UtcDateTime;
        if (!totp.VerifyTotp(now, code, out var step, new VerificationWindow(previous: 1, future: 1)))
        {
            return false;
        }

        if (step <= lastUsedStep)
        {
            return false; // replay of an already-accepted code
        }

        matchedStep = step;
        return true;
    }
}

/// <summary>Ten single-use recovery codes, shown once, stored as SHA-256 hashes.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;

    public static IReadOnlyList<string> Generate()
    {
        var codes = new string[Count];
        for (var i = 0; i < Count; i++)
        {
            var raw = Tokens.NewUrlSafe(6).ToLowerInvariant().Replace('-', 'x').Replace('_', 'y');
            codes[i] = $"{raw[..4]}-{raw[4..8]}";
        }

        return codes;
    }

    public static byte[] Hash(string code) => Tokens.Hash(code.Trim().ToLowerInvariant());
}
