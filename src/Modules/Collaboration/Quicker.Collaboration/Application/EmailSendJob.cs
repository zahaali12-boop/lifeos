using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;

namespace Quicker.Collaboration.Application;

public sealed record EmailSendPayload(Guid EmailId);

/// <summary>
/// Sends one logged email through the configured provider. The outcome is recorded in a transaction of its own
/// before a failure is thrown, so the log shows every attempt while the job's backoff drives the retries.
/// </summary>
public sealed class EmailSendJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, IUnitOfWorkAccessor unitOfWork, IEmailSender sender, IClock clock) : IJobHandler<EmailSendPayload>
{
    public static string JobType => NotificationService.EmailJobType;

    public async Task<object?> ExecuteAsync(EmailSendPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        EmailLog? email;
        await using (var scope = scopeFactory.CreateAsyncScope())
        await using (var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken))
        {
            scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
            email = await scope.ServiceProvider.GetRequiredService<CollaborationDbContext>().Emails.SingleOrDefaultAsync(e => e.Id == payload.EmailId, cancellationToken);
            await uow.RollbackAsync(cancellationToken);
        }

        if (email is null)
        {
            throw new JobFailedException($"Email {payload.EmailId} no longer exists.");
        }

        if (email.Status == "sent")
        {
            return new { sent = true, alreadySent = true };
        }

        string? error = null;
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Quicker-Email"] = email.Id.ToString() };
            await sender.SendAsync(new EmailMessage(email.ToAddress, email.Subject, email.TextBody, Headers: headers), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        var exhausted = context.Attempt >= NotificationService.EmailMaxAttempts;
        await RecordAsync(email.Id, context.Attempt, error is null ? "sent" : exhausted ? "failed" : "queued", error, cancellationToken);
        if (error is not null)
        {
            throw new InvalidOperationException($"Email {email.Id} attempt {context.Attempt}: {error}");
        }

        return new { sent = true, to = email.ToAddress };
    }

    private async Task RecordAsync(Guid emailId, int attempt, string status, string? error, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        var db = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
        var email = await db.Emails.SingleAsync(e => e.Id == emailId, cancellationToken);
        email.Attempts = attempt;
        email.Status = status;
        email.LastError = error;
        email.SentAt = status == "sent" ? clock.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }
}
