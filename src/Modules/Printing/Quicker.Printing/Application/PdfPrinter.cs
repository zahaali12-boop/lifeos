using Quicker.Kernel.Results;
using Quicker.Printing.Contracts;

namespace Quicker.Printing.Application;

/// <summary>The print pipeline as other modules call it: compose, render now, hand back the bytes.</summary>
public sealed class PdfPrinter(PrintService printing) : IPdfPrinter
{
    public Task<Result<PrintedPdf>> PrintAsync(PrintSubject subject, string variant = PrintVariants.Standard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return printing.PrintNowAsync(new PrintRequest(subject.DocumentType, subject.DocumentId, subject.Parameters, variant), cancellationToken);
    }
}
