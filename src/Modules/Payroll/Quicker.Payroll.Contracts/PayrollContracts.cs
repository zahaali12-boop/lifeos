namespace Quicker.Payroll.Contracts;

/// <summary>The document types of payroll: the run of a period, the payslip of one employee in it, and the employee record.</summary>
public static class PayrollDocumentTypes
{
    public const string Run = "payroll_run";
    public const string Payslip = "payslip";
    public const string Employee = "employee";
}

/// <summary>What a pay component is: pay to the employee, a deduction from it, or a cost the employer bears on top.</summary>
public static class PayComponentKinds
{
    public const string Earning = "earning";
    public const string Deduction = "deduction";
    public const string Employer = "employer";

    public static readonly IReadOnlyList<string> All = [Earning, Deduction, Employer];
}

/// <summary>How a component's amount is found: the employee's basic salary (prorated for the days paid), a fixed amount, or a percentage of the basic or of the gross pay.</summary>
public static class PayComponentCalculations
{
    public const string Basic = "basic";
    public const string Fixed = "fixed";
    public const string PercentOfBasic = "percent_of_basic";
    public const string PercentOfGross = "percent_of_gross";

    public static readonly IReadOnlyList<string> All = [Basic, Fixed, PercentOfBasic, PercentOfGross];
}

public static class LeaveKinds
{
    public const string Annual = "annual";
    public const string Sick = "sick";
    public const string Unpaid = "unpaid";

    public static readonly IReadOnlyList<string> All = [Annual, Sick, Unpaid];
}

public static class PayrollRunStatuses
{
    public const string Draft = "draft";
    public const string Calculated = "calculated";
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Posted = "posted";
    public const string Paid = "paid";
    public const string Reversed = "reversed";
    public const string Cancelled = "cancelled";
}
