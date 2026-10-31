namespace Quicker.Payroll.Application;

public sealed record SavePayComponentRequest(
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    string Calculation,
    string? Code = null,
    decimal DefaultAmount = 0m,
    decimal DefaultRatePct = 0m,
    bool AppliesToAll = false,
    string? ExpenseAccountCode = null,
    string? LiabilityAccountCode = null,
    int SortOrder = 0,
    bool IsActive = true);

public sealed record PayComponentSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    string Calculation,
    decimal DefaultAmount,
    decimal DefaultRatePct,
    bool AppliesToAll,
    string? ExpenseAccountCode,
    string? LiabilityAccountCode,
    int SortOrder,
    bool IsActive,
    DateTimeOffset UpdatedAt);

/// <summary>A component the employee carries, with the amount or rate that overrides the component's default when given.</summary>
public sealed record EmployeeComponentRequest(Guid ComponentId, decimal? Amount = null, decimal? RatePct = null, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

public sealed record SaveEmployeeRequest(
    Guid CompanyId,
    Guid PartnerId,
    DateOnly HireDate,
    decimal BasicSalary,
    string? Number = null,
    Guid? BranchId = null,
    Guid? DepartmentValueId = null,
    IReadOnlyDictionary<string, string>? JobTitle = null,
    DateOnly? TerminationDate = null,
    string PaymentMethod = "transfer",
    string? BankName = null,
    string? Iban = null,
    string? NationalId = null,
    int LeaveEntitlementDays = 21,
    string? Notes = null,
    IReadOnlyList<EmployeeComponentRequest>? Components = null);

public sealed record EmployeeComponentSummary(Guid Id, Guid ComponentId, string ComponentCode, IReadOnlyDictionary<string, string> ComponentName, string Kind, string Calculation, decimal? Amount, decimal? RatePct, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record EmployeeSummary(
    Guid Id,
    Guid CompanyId,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> Name,
    string Number,
    Guid? BranchId,
    Guid? DepartmentValueId,
    string? DepartmentCode,
    IReadOnlyDictionary<string, string> JobTitle,
    DateOnly HireDate,
    DateOnly? TerminationDate,
    decimal BasicSalary,
    string Currency,
    string PaymentMethod,
    string? BankName,
    string? Iban,
    string? NationalId,
    int LeaveEntitlementDays,
    decimal LeaveTakenThisYear,
    decimal LeaveBalance,
    string Status,
    string? Notes,
    bool CanBePaid,
    IReadOnlyList<EmployeeComponentSummary> Components,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveLeaveRequest(Guid EmployeeId, string Kind, DateOnly FromDate, DateOnly ToDate, decimal? Days = null, string? Notes = null);

public sealed record LeaveSummary(Guid Id, Guid CompanyId, Guid EmployeeId, string EmployeeNumber, IReadOnlyDictionary<string, string> EmployeeName, string Kind, DateOnly FromDate, DateOnly ToDate, decimal Days, string Status, string? Notes, DateTimeOffset CreatedAt);

public sealed record CreateRunRequest(Guid CompanyId, int Year, int Month, DateOnly? PayDate = null, DateOnly? PostingDate = null, string? Notes = null);

public sealed record PayRunRequest(Guid BankAccountId, DateOnly? PayDate = null, string? Reference = null);

public sealed record ReverseRunRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record PayrollRunItemSummary(Guid Id, Guid ComponentId, string ComponentCode, IReadOnlyDictionary<string, string> ComponentName, string Kind, string Calculation, decimal BaseAmount, decimal RatePct, decimal Amount, int SortOrder);

public sealed record PayrollRunLineSummary(
    Guid Id,
    int LineNo,
    Guid EmployeeId,
    Guid PartnerId,
    string EmployeeNumber,
    IReadOnlyDictionary<string, string> EmployeeName,
    Guid? BranchId,
    int DaysInPeriod,
    decimal PaidDays,
    decimal UnpaidDays,
    decimal BasicSalary,
    decimal BasicAmount,
    decimal EarningsTotal,
    decimal Gross,
    decimal DeductionsTotal,
    decimal EmployerTotal,
    decimal Net,
    Guid? OpenItemId,
    Guid? PaymentId,
    string? PaymentNumber,
    DateOnly? PaidOn,
    IReadOnlyList<PayrollRunItemSummary> Items);

public sealed record PayrollRunSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    int PeriodYear,
    int PeriodMonth,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly PostingDate,
    DateOnly PayDate,
    string Status,
    Guid? ApprovalRequestId,
    string? RejectionReason,
    int EmployeesCount,
    decimal TotalBasic,
    decimal TotalEarnings,
    decimal TotalGross,
    decimal TotalDeductions,
    decimal TotalEmployer,
    decimal TotalNet,
    string Currency,
    Guid? JournalEntryId,
    Guid? ReversalEntryId,
    string? Notes,
    IReadOnlyList<PayrollRunLineSummary> Lines,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
