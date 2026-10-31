using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Organization.Contracts;
using Quicker.Payroll.Contracts;
using Quicker.Payroll.Persistence;
using Quicker.Printing.Contracts;

namespace Quicker.Payroll.Application;

/// <summary>What one employee's line of a payroll run exposes to the payslip template (ADR-0022): the period and the days paid, the earnings, the deductions and the employer's contributions item by item, the net and how it was paid.</summary>
public sealed class PayslipPrintModel(PayrollDbContext db, ICompanyDirectory companies, IDimensionDirectory dimensions, ICurrentPrincipal principal) : IPrintModelProvider
{
    public string DocumentType => PayrollDocumentTypes.Payslip;

    public IReadOnlyList<PrintVariant> Variants { get; } = [new(PrintVariants.Standard, LocalizedText.Bilingual("Payslip", "قسيمة راتب"))];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (principal.Principal is { } who && !who.Has(PayrollPermissions.RunRead))
        {
            return Error.Forbidden("print.forbidden", "Reading payroll runs is not permitted.");
        }

        if (subject.DocumentId is not { } id)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "The payslip to print is named by the id of its run line.");
        }

        var line = await db.RunLines.AsNoTracking().Include(static l => l.Items.OrderBy(static i => i.SortOrder)).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (line is null)
        {
            return Error.NotFound(PayrollDocumentTypes.Payslip, id);
        }

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == line.RunId, cancellationToken);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == line.EmployeeId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken);
        var currency = company?.FunctionalCurrency.Code ?? employee?.Currency ?? string.Empty;
        var minor = company?.FunctionalCurrency.MinorUnits ?? 2;
        string Money(decimal amount) => amount.ToString("N" + Math.Clamp(minor, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var department = employee?.DepartmentValueId is { } valueId ? await dimensions.FindValueAsync(valueId, cancellationToken) : null;
        var period = new DateTime(run.PeriodYear, run.PeriodMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);

        List<Dictionary<string, object?>> Items(string kind) => line.Items.Where(i => i.Kind == kind).OrderBy(static i => i.SortOrder).Select(i => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = i.ComponentCode,
            ["name"] = i.ComponentName,
            ["calculation"] = i.Calculation,
            ["has_rate"] = i.Calculation is PayComponentCalculations.PercentOfBasic or PayComponentCalculations.PercentOfGross,
            ["base"] = Money(i.BaseAmount),
            ["rate"] = i.RatePct.ToString("0.##", CultureInfo.InvariantCulture),
            ["amount"] = Money(i.Amount),
        }).ToList();

        var deductions = Items(PayComponentKinds.Deduction);
        var employer = Items(PayComponentKinds.Employer);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = run.Status,
            ["run_number"] = run.Number,
            ["period"] = LocalizedText.Bilingual(period.ToString("MMMM yyyy", CultureInfo.InvariantCulture), period.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ar"))),
            ["period_start"] = run.PeriodStart,
            ["period_end"] = run.PeriodEnd,
            ["document_date"] = run.PeriodEnd,
            ["pay_date"] = run.PayDate,
            ["currency"] = currency,
            ["employee_number"] = line.EmployeeNumber,
            ["employee_name"] = line.EmployeeName,
            ["job_title"] = employee is null || employee.JobTitle.IsEmpty ? null : employee.JobTitle,
            ["department"] = department?.Code,
            ["hire_date"] = employee?.HireDate,
            ["days_in_period"] = line.DaysInPeriod,
            ["paid_days"] = line.PaidDays.ToString("0.##", CultureInfo.InvariantCulture),
            ["unpaid_days"] = line.UnpaidDays.ToString("0.##", CultureInfo.InvariantCulture),
            ["has_unpaid"] = line.UnpaidDays != 0m,
            ["basic_salary"] = Money(line.BasicSalary),
            ["basic_amount"] = Money(line.BasicAmount),
            ["earnings"] = Items(PayComponentKinds.Earning),
            ["deductions"] = deductions,
            ["employer"] = employer,
            ["has_deductions"] = deductions.Count > 0,
            ["has_employer"] = employer.Count > 0,
            ["gross"] = Money(line.Gross),
            ["deductions_total"] = Money(line.DeductionsTotal),
            ["employer_total"] = Money(line.EmployerTotal),
            ["net"] = Money(line.Net),
            ["payment_method"] = employee?.PaymentMethod,
            ["payment_method_label"] = employee?.PaymentMethod == "cash" ? LocalizedText.Bilingual("Cash", "نقدًا") : LocalizedText.Bilingual("Bank transfer", "حوالة بنكية"),
            ["bank_name"] = employee?.BankName,
            ["iban"] = employee?.Iban,
            ["is_paid"] = line.PaymentId is not null,
            ["paid_on"] = line.PaidOn,
            ["payment_number"] = line.PaymentNumber,
        };
        var number = $"{run.Number}-{line.LineNo.ToString("00", CultureInfo.InvariantCulture)}";
        var final = run.Status is PayrollRunStatuses.Posted or PayrollRunStatuses.Paid;
        return new PrintModel(run.CompanyId, line.BranchId, LocalizedText.Bilingual("Payslip", "قسيمة راتب"), number, run.Status, final, run.PeriodEnd, currency, data, line.PartnerId, null, $"{number}.pdf");
    }
}
