using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;

namespace Quicker.Collaboration.Tests;

/// <summary>
/// One API host with the SMTP provider pointed at a local fake relay, filesystem object storage in a temporary
/// directory and the object-lock anchor store, shared by the collaboration test classes.
/// </summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public FakeSmtpServer Smtp { get; private set; } = null!;

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "quicker-tests", "storage-" + Guid.NewGuid().ToString("N")[..12]);

    public async ValueTask InitializeAsync()
    {
        Smtp = FakeSmtpServer.Start();
        Api = await ApiFixture.StartAsync(builder =>
        {
            builder.UseSetting("Quicker:Email:Provider", "smtp");
            builder.UseSetting("Quicker:Email:SmtpHost", "127.0.0.1");
            builder.UseSetting("Quicker:Email:SmtpPort", Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Quicker:Email:FromAddress", "quicker@example.test");
            builder.UseSetting("Quicker:Storage:Provider", "filesystem");
            builder.UseSetting("Quicker:Storage:Path", StorageRoot);
            builder.UseSetting("Quicker:Storage:MaxUploadBytes", "1048576");
            builder.UseSetting("Quicker:Audit:Anchoring:Store", "object_lock");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        Smtp?.Dispose();
        try
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Drains the outbox and then the job queue the way the worker would.</summary>
    public async Task RunWorkerAsync()
    {
        var dispatcher = Api.Services.GetRequiredService<OutboxDispatcher>();
        var runner = Api.Services.GetRequiredService<JobRunner>();
        while (await dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        while (await runner.RunOneAsync(CancellationToken.None))
        {
        }
    }

    /// <summary>Invites a member with the given grants and accepts the invitation from the email the fake relay received.</summary>
    public async Task<(string AccessToken, Guid MembershipId, string Email)> InviteAsync(Workspace ws, string roleCode, params string[] grants)
    {
        using var owner = Api.ClientFor(ws.AccessToken);
        var role = await (await owner.PostAsJsonAsync("/api/v1/roles", new { code = roleCode, name = new { en = roleCode }, description = "", grants }, ApiFixture.Json)).ReadJsonAsync();
        var email = $"{roleCode}-{ws.Slug}@example.test";
        var invited = await (await owner.PostAsJsonAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } }, ApiFixture.Json)).ReadJsonAsync();
        var mail = await Smtp.WaitForAsync(m => m.To.Contains(email, StringComparer.OrdinalIgnoreCase), TimeSpan.FromSeconds(10));
        var token = TokenIn(mail.Body);
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return (accepted.GetProperty("accessToken").GetString()!, invited.GetProperty("membershipId").GetGuid(), email);
    }

    /// <summary>The value after "token=" up to the first character that cannot be part of a token.</summary>
    private static string TokenIn(string text)
    {
        var start = text.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = start;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '_' or '-' or '.'))
        {
            end++;
        }

        return text[start..end];
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "collaboration-api";
}

public sealed record ReceivedEmail(string From, IReadOnlyList<string> To, string Raw)
{
    public string Header(string name)
    {
        var headers = Raw.Split("\r\n\r\n", 2)[0];
        var line = headers.Split("\r\n").FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
        return line is null ? string.Empty : line[(name.Length + 1)..].Trim();
    }

    /// <summary>The body with the transfer encoding undone (7bit, quoted-printable or base64).</summary>
    public string Body
    {
        get
        {
            var parts = Raw.Split("\r\n\r\n", 2);
            var body = parts.Length > 1 ? parts[1] : string.Empty;
            var encoding = Header("Content-Transfer-Encoding").ToLowerInvariant();
            if (encoding == "base64")
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\r\n", string.Empty, StringComparison.Ordinal)));
            }

            if (encoding == "quoted-printable")
            {
                var joined = body.Replace("=\r\n", string.Empty, StringComparison.Ordinal);
                var bytes = new List<byte>(joined.Length);
                for (var i = 0; i < joined.Length; i++)
                {
                    if (joined[i] == '=' && i + 2 < joined.Length && byte.TryParse(joined.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
                    {
                        bytes.Add(value);
                        i += 2;
                    }
                    else
                    {
                        bytes.Add((byte)joined[i]);
                    }
                }

                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            return body;
        }
    }
}

/// <summary>A minimal SMTP relay on the loopback interface: accepts every message and keeps it; can be told to refuse the next one.</summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private Task? _accepting;

    public int Port { get; private set; }

    public ConcurrentQueue<ReceivedEmail> Received { get; } = new();

    /// <summary>When set, the next DATA is answered with a permanent 554 and the flag is cleared.</summary>
    public bool RejectNext { get; set; }

    public static FakeSmtpServer Start()
    {
        var server = new FakeSmtpServer();
        server._listener.Start();
        server.Port = ((IPEndPoint)server._listener.LocalEndpoint).Port;
        server._accepting = Task.Run(server.AcceptAsync);
        return server;
    }

    public async Task<ReceivedEmail> WaitForAsync(Func<ReceivedEmail, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = Received.FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No matching email arrived.");
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1);
            using var writer = new StreamWriter(stream, Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP ready");
            var from = string.Empty;
            var to = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (upper.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                {
                    from = line[10..].Trim().Trim('<', '>');
                    await writer.WriteLineAsync("250 OK");
                }
                else if (upper.StartsWith("RCPT TO:", StringComparison.Ordinal))
                {
                    to.Add(line[8..].Trim().Split(' ')[0].Trim('<', '>'));
                    await writer.WriteLineAsync("250 OK");
                }
                else if (upper == "DATA")
                {
                    if (RejectNext)
                    {
                        RejectNext = false;
                        await writer.WriteLineAsync("554 relay refused this message");
                        continue;
                    }

                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } body && body != ".")
                    {
                        data.Append(body.StartsWith("..", StringComparison.Ordinal) ? body[1..] : body).Append("\r\n");
                    }

                    Received.Enqueue(new ReceivedEmail(from, to.ToArray(), data.ToString()));
                    to = [];
                    await writer.WriteLineAsync("250 OK queued");
                }
                else if (upper == "QUIT")
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            _accepting?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}

internal static class JsonHelpers
{
    public static Dictionary<string, string> Map(this JsonElement element) =>
        element.EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
}
