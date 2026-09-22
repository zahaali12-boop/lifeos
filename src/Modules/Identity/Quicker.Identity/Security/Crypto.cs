#pragma warning disable CA1822 // Hashing services are injected so parameters can come from configuration later.
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Quicker.Identity.Security;

/// <summary>Argon2id password hashing with parameters encoded in the hash string (rehash when they change).</summary>
public sealed class PasswordHasher
{
    // Tuned for ~100 ms on a 2024 server core; raise with hardware. Encoded in the hash so old hashes still verify.
    private const int MemoryKb = 64 * 1024;
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKb, Iterations, Parallelism);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !string.Equals(parts[0], "argon2id", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = parts[2].Split(',').Select(static p => p.Split('=')).ToDictionary(static p => p[0], static p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var salt = Convert.FromBase64String(parts[3]);
        var expected = Convert.FromBase64String(parts[4]);
        var actual = Derive(password, salt, parameters["m"], parameters["t"], parameters["p"]);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string encoded) => !encoded.Contains($"m={MemoryKb},t={Iterations},p={Parallelism}", StringComparison.Ordinal);

    private static byte[] Derive(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }
}

/// <summary>Email addresses are stored lower-cased and trimmed; compare with the normalised form.</summary>
public static class Emails
{
    public static string Normalize(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>Random tokens and their SHA-256 hashes (refresh tokens, API keys, one-time tokens are stored hashed).</summary>
public static class Tokens
{
    public static string NewUrlSafe(int bytes = 32)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string NewNumericCode(int digits = 6)
    {
        var max = (int)Math.Pow(10, digits);
        return RandomNumberGenerator.GetInt32(max).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}

/// <summary>
/// AES-256-GCM protection for secrets at rest (TOTP seeds, OIDC client secrets) with a key from configuration
/// (ADR-0025). The output carries a key id so keys can be rotated.
/// </summary>
public sealed class SecretProtector
{
    private readonly byte[] _key;
    private readonly byte _keyId;

    public SecretProtector(string base64Key, byte keyId = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new ArgumentException("Secret protection key must be 32 bytes (base64).", nameof(base64Key));
        }

        _keyId = keyId;
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var output = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        output[0] = _keyId;
        nonce.CopyTo(output, 1);
        tag.CopyTo(output, 1 + nonce.Length);
        ciphertext.CopyTo(output, 1 + nonce.Length + tag.Length);
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (protectedBytes.Length < 1 + nonceLength + tagLength || protectedBytes[0] != _keyId)
        {
            throw new CryptographicException("Protected payload has an unknown key id or is malformed.");
        }

        var nonce = protectedBytes.Slice(1, nonceLength);
        var tag = protectedBytes.Slice(1 + nonceLength, tagLength);
        var ciphertext = protectedBytes[(1 + nonceLength + tagLength)..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, tagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public string ProtectString(string value) => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value)));

    public string UnprotectString(string protectedBase64) => Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedBase64)));
}
