using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quicker.Audit.Application;

/// <summary>A chain head as written outside the database.</summary>
public sealed record AnchorRecord(string Chain, Guid? TenantId, long Seq, string HeadHash, DateTimeOffset AnchoredAt);

/// <summary>Where the store put an anchor and the proof it gives back.</summary>
public sealed record AnchorReceipt(string Store, string Reference, string Receipt);

/// <summary>External, append-only storage for chain heads (ADR-0015). Verification reads anchors back from here.</summary>
public interface IAuditAnchorStore
{
    string Name { get; }

    Task<AnchorReceipt> WriteAsync(AnchorRecord record, CancellationToken cancellationToken);

    Task<AnchorRecord?> ReadAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>
/// Anchors in a local append-only JSON-lines file, each line carrying the SHA-256 of the previous line so the file
/// itself is a chain. The on-premise default; the object-lock store ships with the storage client in M1.8.
/// </summary>
public sealed class FileAuditAnchorStore(string directory) : IAuditAnchorStore
{
    private const string FileName = "anchors.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private sealed record Line(string Chain, Guid? TenantId, long Seq, string HeadHash, DateTimeOffset AnchoredAt, string Prev);

    public string Name => "file";

    public string Directory { get; } = directory;

    public async Task<AnchorReceipt> WriteAsync(AnchorRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, FileName);
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken) : [];
            var previous = lines.Length == 0 ? string.Empty : Digest(lines[^1]);
            var line = JsonSerializer.Serialize(new Line(record.Chain, record.TenantId, record.Seq, record.HeadHash, record.AnchoredAt, previous), Json);
            await File.AppendAllTextAsync(path, line + "\n", Utf8NoBom, cancellationToken);
            return new AnchorReceipt(Name, $"{FileName}:{(lines.Length + 1).ToString(CultureInfo.InvariantCulture)}", Digest(line));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AnchorRecord?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var parts = reference.Split(':');
        if (parts.Length != 2 || !string.Equals(parts[0], FileName, StringComparison.Ordinal) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            return null;
        }

        var path = Path.Combine(Directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (number > lines.Length)
        {
            return null;
        }

        var line = JsonSerializer.Deserialize<Line>(lines[number - 1], Json);
        if (line is null)
        {
            return null;
        }

        var expectedPrevious = number == 1 ? string.Empty : Digest(lines[number - 2]);
        if (!string.Equals(line.Prev, expectedPrevious, StringComparison.Ordinal))
        {
            return null; // the file was edited: the line no longer follows its predecessor
        }

        return new AnchorRecord(line.Chain, line.TenantId, line.Seq, line.HeadHash, line.AnchoredAt);
    }

    private static string Digest(string line) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
}
