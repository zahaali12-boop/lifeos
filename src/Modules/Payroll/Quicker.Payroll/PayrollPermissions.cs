using Quicker.Identity.Contracts;

namespace Quicker.Payroll;

/// <summary>Permission keys of payroll and HR: the pay set-up, the employees, leave, and the runs through calculation, approval, posting and payment.</summary>
public static class PayrollPermissions
{
    public const string SetupRead = "payroll.setup.read";
    public const string SetupManage = "payroll.setup.manage";
    public const string EmployeeRead = "payroll.employee.read";
    public const string EmployeeManage = "payroll.employee.manage";
    public const string LeaveRead = "payroll.leave.read";
    public const string LeaveManage = "payroll.leave.manage";
    public const string RunRead = "payroll.run.read";
    public const string RunManage = "payroll.run.manage";
    public const string RunPay = "payroll.run.pay";

    public static readonly PermissionDefinition[] All =
    [
        new(SetupRead, "payroll", "Read the pay components of a company"),
        new(SetupManage, "payroll", "Set up pay components: earnings, deductions and employer contributions with the accounts they land on"),
        new(EmployeeRead, "payroll", "Read employees: their job, pay, components and leave balance"),
        new(EmployeeManage, "payroll", "Hire, change and terminate employees and set their pay"),
        new(LeaveRead, "payroll", "Read leave records"),
        new(LeaveManage, "payroll", "Record and cancel leave"),
        new(RunRead, "payroll", "Read payroll runs and payslips"),
        new(RunManage, "payroll", "Create, calculate, submit, approve, post and reverse payroll runs"),
        new(RunPay, "payroll", "Pay a posted payroll run from a bank account"),
    ];
}
