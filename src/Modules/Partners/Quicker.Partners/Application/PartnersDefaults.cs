using Quicker.Kernel.Ids;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Partners.Application;

/// <summary>
/// What every new tenant starts with: the Incoterms delivery terms, the usual payment terms and a sales pipeline (lead,
/// qualified, proposal, negotiation, won, lost), all system rows an admin may rename, reorder or deactivate but not remove.
/// </summary>
public sealed class PartnersDefaults(PartnersDbContext db, IClock clock) : ITenantSetupStep
{
    public Task SetUpAsync(TenantId tenantId, string defaultLanguage, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var (code, en, ar) in new[]
        {
            ("EXW", "Ex works", "تسليم المصنع"),
            ("FCA", "Free carrier", "تسليم الناقل"),
            ("FOB", "Free on board", "تسليم على ظهر السفينة"),
            ("CFR", "Cost and freight", "التكلفة والشحن"),
            ("CIF", "Cost, insurance and freight", "التكلفة والتأمين والشحن"),
            ("DAP", "Delivered at place", "التسليم في المكان"),
            ("DDP", "Delivered duty paid", "التسليم خالص الرسوم"),
        })
        {
            db.DeliveryTerms.Add(new DeliveryTerms { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), IsSystem = true, CreatedAt = now, UpdatedAt = now });
        }

        foreach (var (code, en, ar, days, basis) in new[]
        {
            ("IMMEDIATE", "Due on receipt", "مستحق عند الاستلام", 0, DueBases.InvoiceDateValue),
            ("NET15", "Net 15 days", "صافي ١٥ يومًا", 15, DueBases.InvoiceDateValue),
            ("NET30", "Net 30 days", "صافي ٣٠ يومًا", 30, DueBases.InvoiceDateValue),
            ("NET60", "Net 60 days", "صافي ٦٠ يومًا", 60, DueBases.InvoiceDateValue),
            ("EOM30", "30 days after month end", "٣٠ يومًا بعد نهاية الشهر", 30, DueBases.EndOfMonthValue),
        })
        {
            db.PaymentTerms.Add(new PaymentTerms { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), DueBasis = basis, DueDays = days, IsSystem = true, CreatedAt = now, UpdatedAt = now });
        }

        var order = 0;
        foreach (var (code, en, ar, probability, outcome) in new[]
        {
            ("LEAD", "Lead", "عميل محتمل", 10, OpportunityOutcomes.Open),
            ("QUALIFIED", "Qualified", "مؤهل", 25, OpportunityOutcomes.Open),
            ("PROPOSAL", "Proposal", "عرض مقدم", 50, OpportunityOutcomes.Open),
            ("NEGOTIATION", "Negotiation", "تفاوض", 75, OpportunityOutcomes.Open),
            ("WON", "Won", "مكسوبة", 100, OpportunityOutcomes.Won),
            ("LOST", "Lost", "خاسرة", 0, OpportunityOutcomes.Lost),
        })
        {
            order += 10;
            db.PipelineStages.Add(new PipelineStage { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), SortOrder = order, DefaultProbability = probability, Outcome = outcome, IsSystem = true, CreatedAt = now, UpdatedAt = now });
        }

        return db.SaveChangesAsync(cancellationToken);
    }

    private static class DueBases
    {
        public const string InvoiceDateValue = Contracts.DueBases.InvoiceDate;
        public const string EndOfMonthValue = Contracts.DueBases.EndOfMonth;
    }
}
