using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Quicker.Messaging;

/// <summary>Settings of transactional email (section <c>Quicker:Email</c>).</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Quicker:Email";

    /// <summary><c>capture</c> (kept in memory and logged; development and tests) or <c>smtp</c> (Mailpit locally, any relay in production).</summary>
    public string Provider { get; set; } = "capture";

    public string SmtpHost { get; set; } = "localhost";

    public int SmtpPort { get; set; } = 1025;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Negotiate TLS after connecting (STARTTLS).</summary>
    public bool UseStartTls { get; set; }

    public string FromAddress { get; set; } = "no-reply@quicker.local";

    public string FromName { get; set; } = "Quicker";

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Sends through an SMTP relay. Every message carries the configured sender and the caller's headers.</summary>
public sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromName),
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };
        mail.To.Add(string.IsNullOrEmpty(message.ToName) ? new MailAddress(message.To) : new MailAddress(message.To, message.ToName));
        if (message.HtmlBody is not null)
        {
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, null, "text/html"));
        }

        if (message.Headers is not null)
        {
            foreach (var (name, value) in message.Headers)
            {
                mail.Headers[name] = value;
            }
        }

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.UseStartTls,
            Timeout = options.TimeoutSeconds * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(options.Username))
        {
            client.Credentials = new NetworkCredential(options.Username, options.Password);
        }

        await client.SendMailAsync(mail, cancellationToken);
    }
}

public static class EmailRegistration
{
    /// <summary>The configured <see cref="IEmailSender"/>; the capturing sender stays registered so tests and the dev inbox can read what was sent.</summary>
    public static IServiceCollection AddQuickerEmail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton(static sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value);
        services.AddSingleton<CapturingEmailSender>();
        services.AddSingleton<IEmailSender>(static sp =>
        {
            var options = sp.GetRequiredService<EmailOptions>();
            return options.Provider.ToLowerInvariant() switch
            {
                "capture" => sp.GetRequiredService<CapturingEmailSender>(),
                "smtp" => new SmtpEmailSender(options),
                _ => throw new InvalidOperationException($"Unknown email provider '{options.Provider}'. Supported: capture, smtp."),
            };
        });
        return services;
    }
}
