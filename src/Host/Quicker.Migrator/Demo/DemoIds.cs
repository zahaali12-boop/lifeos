using System.Security.Cryptography;
using System.Text;

namespace Quicker.Migrator.Demo;

/// <summary>
/// Stable identifiers for the demo tenant (ADR-0029: a deterministic seeder). Every id keeps the version-7 layout
/// the platform sorts and pages by: a fixed epoch plus an ordinal in the timestamp bits, the rest from a hash of
/// the name, so a reseed produces the same ids in the same order.
/// </summary>
public static class DemoIds
{
    private static readonly long Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static Guid For(string name, int ordinal = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("quicker-demo:" + name));
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = Epoch + ordinal;
        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;
        bytes[6] = (byte)(0x70 | (hash[0] & 0x0F));
        bytes[7] = hash[1];
        bytes[8] = (byte)(0x80 | (hash[2] & 0x3F));
        hash.AsSpan(3, 7).CopyTo(bytes[9..]);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>A stable pseudo-random integer in [0, <paramref name="range"/>) for a named series point.</summary>
    public static int Draw(string series, int point, int range)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"quicker-demo:{series}:{point}"));
        var value = (uint)(hash[0] << 24 | hash[1] << 16 | hash[2] << 8 | hash[3]);
        return (int)(value % (uint)range);
    }
}
