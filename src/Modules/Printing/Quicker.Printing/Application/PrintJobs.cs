using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Printing.Persistence;

namespace Quicker.Printing.Application;

public sealed record PrintRenderPayload(Guid RenderId);

public sealed record PrintEmailPayload(Guid EmailId);

/// <summary>Renders one queued PDF in the worker. A missing Chromium is permanent; anything else retries with the job's backoff, the row showing the last error meanwhile.</summary>
public sealed class PrintRenderJob(PrintService printing, PrintFailureRecorder failures) : IJobHandler<PrintRenderPayload>
{
    public static string JobType => PrintService.RenderJobType;

    public async Task<object?> ExecuteAsync(PrintRenderPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var rendered = await printing.RenderNowAsync(payload.RenderId, cancellationToken);
            if (rendered.IsFailure)
            {
                throw new JobFailedException($"{rendered.Error!.Code}: {rendered.Error.Message}");
            }

            return new { rendered.Value.Status, rendered.Value.SizeBytes, evidence = rendered.Value.IsEvidence };
        }
        catch (PdfRendererUnavailableException ex)
        {
            await failures.RenderFailedAsync(payload.RenderId, ex.Message, exhausted: true, cancellationToken);
            throw new JobFailedException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JobFailedException)
        {
            await failures.RenderFailedAsync(payload.RenderId, $"{ex.GetType().Name}: {ex.Message}", exhausted: context.Attempt >= PrintService.RenderMaxAttempts, cancellationToken);
            throw;
        }
    }
}

/// <summary>Sends one queued email with its PDF, rendering first when the render job has not run yet.</summary>
public sealed class PrintEmailJob(PrintService printing, PrintFailureRecorder failures) : IJobHandler<PrintEmailPayload>
{
    public static string JobType => PrintService.EmailJobType;

    public async Task<object?> ExecuteAsync(PrintEmailPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var sent = await printing.SendNowAsync(payload.EmailId, cancellationToken);
            if (sent.IsFailure)
            {
                throw new JobFailedException($"{sent.Error!.Code}: {sent.Error.Message}");
            }

            return new { sent.Value.Status, sent.Value.To };
        }
        catch (PdfRendererUnavailableException ex)
        {
            await failures.EmailFailedAsync(payload.EmailId, ex.Message, exhausted: true, cancellationToken);
            throw new JobFailedException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JobFailedException)
        {
            await failures.EmailFailedAsync(payload.EmailId, $"{ex.GetType().Name}: {ex.Message}", exhausted: context.Attempt >= PrintService.EmailMaxAttempts, cancellationToken);
            throw;
        }
    }
}

/// <summary>Records a failure in a transaction of its own, since the job's own unit of work rolls back with the exception.</summary>
public sealed class PrintFailureRecorder(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, IUnitOfWorkAccessor unitOfWork, IClock clock)
{
    public async Task RenderFailedAsync(Guid renderId, string error, bool exhausted, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        var db = scope.ServiceProvider.GetRequiredService<PrintingDbContext>();
        var render = await db.Renders.SingleOrDefaultAsync(r => r.Id == renderId, cancellationToken);
        if (render is not null && render.Status != "ready")
        {
            render.Status = exhausted ? "failed" : "queued";
            render.Error = Truncate(error);
            await db.SaveChangesAsync(cancellationToken);
        }

        await uow.CommitAsync(cancellationToken);
    }

    public async Task EmailFailedAsync(Guid emailId, string error, bool exhausted, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        var db = scope.ServiceProvider.GetRequiredService<PrintingDbContext>();
        var mail = await db.Emails.SingleOrDefaultAsync(e => e.Id == emailId, cancellationToken);
        if (mail is not null && mail.Status != "sent")
        {
            mail.Status = exhausted ? "failed" : "queued";
            mail.Error = Truncate(error);
            await db.SaveChangesAsync(cancellationToken);
        }

        _ = clock;
        await uow.CommitAsync(cancellationToken);
    }

    private static string Truncate(string error) => error.Length <= 2000 ? error : error[..2000];
}
