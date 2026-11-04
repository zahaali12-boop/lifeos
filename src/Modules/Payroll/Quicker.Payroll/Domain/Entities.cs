using Quicker.Kernel.Text;
using Quicker.Payroll.Contracts;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Payroll.Domain;

/// <summary>A pay component (DOMAIN_MODEL "Payroll and HR"): what it is, how it is calculated, and the accounts it lands on (kept as posting rules keyed by the component).</summary>
public sealed class PayComponent : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Kind { get; set; } = PayComponentKinds.Earning;

    public string Calculation { get; set; } = PayComponentCalculations.Fixed;

    public decimal DefaultAmount { get; set; }

    public decimal DefaultRatePct { get; set; }

    public bool AppliesToAll { get; set; }

    public string? ExpenseAccountCode { get; set; }

    public string? LiabilityAccountCode { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An employee of a company: the partner with the employee role, the job, the pay and what the employee carries.</summary>
public sealed class Employee : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid PartnerId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid? BranchId { get; set; }

    public Guid? DepartmentValueId { get; set; }

    public Guid? DimensionSetId { get; set; }

    public LocalizedText JobTitle { get; set; } = new();

    public DateOnly HireDate { get; set; }

    public DateOnly? TerminationDate { get; set; }

    public decimal BasicSalary { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string PaymentMethod { get; set; } = "transfer";

    public string? BankName { get; set; }

    public string? Iban { get; set; }

    public string? NationalId { get; set; }

    public int LeaveEntitlementDays { get; set; } = 21;

    public string Status { get; set; } = "active";

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<EmployeeComponent> Components { get; } = [];
}

public sealed class EmployeeComponent : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid ComponentId { get; set; }

    public decimal? Amount { get; set; }

    public decimal? RatePct { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }
}

public sealed class Leave : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid EmployeeId { get; set; }

    public string Kind { get; set; } = LeaveKinds.Annual;

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public decimal Days { get; set; }

    public string Status { get; set; } = "approved";

    public string? Notes { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A payroll run: one period of one company, calculated from the employees active in it, approved, posted and paid.</summary>
public sealed class PayrollRun : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public int PeriodYear { get; set; }

    public int PeriodMonth { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public DateOnly PostingDate { get; set; }

    public DateOnly PayDate { get; set; }

    public string Status { get; set; } = PayrollRunStatuses.Draft;

    public Guid? ApprovalRequestId { get; set; }

    public string? RejectionReason { get; set; }

    public int EmployeesCount { get; set; }

    public decimal TotalBasic { get; set; }

    public decimal TotalEarnings { get; set; }

    public decimal TotalGross { get; set; }

    public decimal TotalDeductions { get; set; }

    public decimal TotalEmployer { get; set; }

    public decimal TotalNet { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversalEntryId { get; set; }

    public string? Notes { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PayrollRunLine> Lines { get; } = [];
}

/// <summary>One employee in a run: the days paid, the basic prorated, the items, the net and the payable it opened.</summary>
public sealed class PayrollRunLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public int LineNo { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid PartnerId { get; set; }

    public string EmployeeNumber { get; set; } = string.Empty;

    public LocalizedText EmployeeName { get; set; } = new();

    public Guid? BranchId { get; set; }

    public Guid? DimensionSetId { get; set; }

    public int DaysInPeriod { get; set; } = 30;

    public decimal PaidDays { get; set; } = 30m;

    public decimal UnpaidDays { get; set; }

    public decimal BasicSalary { get; set; }

    public decimal BasicAmount { get; set; }

    public decimal EarningsTotal { get; set; }

    public decimal Gross { get; set; }

    public decimal DeductionsTotal { get; set; }

    public decimal EmployerTotal { get; set; }

    public decimal Net { get; set; }

    public Guid? OpenItemId { get; set; }

    public Guid? PaymentId { get; set; }

    public string? PaymentNumber { get; set; }

    public DateOnly? PaidOn { get; set; }

    public List<PayrollRunItem> Items { get; } = [];
}

public sealed class PayrollRunItem : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid LineId { get; set; }

    public Guid ComponentId { get; set; }

    public string ComponentCode { get; set; } = string.Empty;

    public LocalizedText ComponentName { get; set; } = new();

    public string Kind { get; set; } = PayComponentKinds.Earning;

    public string Calculation { get; set; } = PayComponentCalculations.Fixed;

    public decimal BaseAmount { get; set; }

    public decimal RatePct { get; set; }

    public decimal Amount { get; set; }

    public int SortOrder { get; set; }
}
