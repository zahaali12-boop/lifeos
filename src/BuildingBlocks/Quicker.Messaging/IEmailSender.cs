using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Quicker.Messaging;

/// <summary>A file sent with an email: a rendered PDF, say (roadmap 5.9).</summary>
public sealed record EmailAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null, string? ToName = null, IReadOnlyDictionary<string, string>? Headers = null, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>Sends transactional email. The SMTP/API provider implementation ships with the Collaboration module (M1.8).</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Development and test sender: logs the message and keeps it in memory so tests can read tokens out of it.</summary>
public sealed class CapturingEmailSender(ILogger<CapturingEmailSender> logger) : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyCollection<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _sent.Enqueue(message);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Email to {To}: {Subject}\n{Body}", message.To, message.Subject, message.TextBody);
        }

        return Task.CompletedTask;
    }

    public EmailMessage? LastTo(string address) => _sent.LastOrDefault(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase));
}
