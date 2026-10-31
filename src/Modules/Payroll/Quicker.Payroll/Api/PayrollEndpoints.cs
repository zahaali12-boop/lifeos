using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Payroll.Application;
using Quicker.Web;

namespace Quicker.Payroll.Api;

/// <summary>Payroll and HR under /api/v1/payroll: the pay components, the employees, leave, and the runs through calculation, approval, posting and payment.</summary>
public static class PayrollEndpoints
{
    public static RouteGroupBuilder MapPayrollEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var payroll = api.MapGroup("/payroll").WithTags("Payroll").RequireAuthorization();

        var components = payroll.MapGroup("/components");
        components.MapGet("/", async (Guid companyId, bool? isActive, ComponentService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, isActive, ct)))
            .RequirePermission(PayrollPermissions.SetupRead)
            .WithSummary("The pay components of a company (BASIC, the basic salary, is created on first use): earnings, deductions and employer contributions with how each is calculated and the accounts it lands on.");
        components.MapPost("/", async (SavePayComponentRequest request, ComponentService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static c => $"/api/v1/payroll/components/{c.Id}"))
            .RequirePermission(PayrollPermissions.SetupManage)
            .WithSummary("Creates a pay component; its accounts become posting rules keyed by the component in the company's posting profile.");
        components.MapGet("/{componentId:guid}", async (Guid componentId, ComponentService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(componentId, ct)))
            .RequirePermission(PayrollPermissions.SetupRead);
        components.MapPut("/{componentId:guid}", async (Guid componentId, SavePayComponentRequest request, ComponentService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(componentId, request, ct)))
            .RequirePermission(PayrollPermissions.SetupManage);

        var employees = payroll.MapGroup("/employees");
        employees.MapGet("/", async (Guid companyId, string? status, EmployeeService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(PayrollPermissions.EmployeeRead)
            .WithSummary("The employees of a company with their job, pay, components, leave balance and whether they can be paid.");
        employees.MapPost("/", async (SaveEmployeeRequest request, EmployeeService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static e => $"/api/v1/payroll/employees/{e.Id}"))
            .RequirePermission(PayrollPermissions.EmployeeManage)
            .WithSummary("Hires a partner with the employee role into the company: number (EMP-0001 when none is given), job, hire date, basic salary, the components carried and how the employee is paid.");
        employees.MapGet("/{employeeId:guid}", async (Guid employeeId, EmployeeService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(employeeId, ct)))
            .RequirePermission(PayrollPermissions.EmployeeRead);
        employees.MapPut("/{employeeId:guid}", async (Guid employeeId, SaveEmployeeRequest request, EmployeeService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(employeeId, request, ct)))
            .RequirePermission(PayrollPermissions.EmployeeManage)
            .WithSummary("Changes an employee's job, pay or components, or terminates them on a date.");

        var leaves = payroll.MapGroup("/leaves");
        leaves.MapGet("/", async (Guid companyId, Guid? employeeId, int? year, LeaveService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, employeeId, year, ct)))
            .RequirePermission(PayrollPermissions.LeaveRead)
            .WithSummary("Leave records of a company, newest first, filtered by employee or year.");
        leaves.MapPost("/", async (SaveLeaveRequest request, LeaveService service, CancellationToken ct) => ApiProblems.Created(await service.RecordAsync(request, ct), static l => $"/api/v1/payroll/leaves/{l.Id}"))
            .RequirePermission(PayrollPermissions.LeaveManage)
            .WithSummary("Records approved leave: annual (against the yearly entitlement), sick, or unpaid (deducted from the pay of the period it falls in).");
        leaves.MapPost("/{leaveId:guid}/cancel", async (Guid leaveId, LeaveService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(leaveId, ct)))
            .RequirePermission(PayrollPermissions.LeaveManage)
            .WithSummary("Cancels a leave record; unpaid leave in a period already paid stays until the run is reversed.");

        var runs = payroll.MapGroup("/runs");
        runs.MapGet("/", async (Guid companyId, string? status, int? year, RunService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, year, ct)))
            .RequirePermission(PayrollPermissions.RunRead)
            .WithSummary("Payroll runs of a company, newest period first, with their lines and items.");
        runs.MapPost("/", async (CreateRunRequest request, RunService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/payroll/runs/{r.Id}"))
            .RequirePermission(PayrollPermissions.RunManage)
            .WithSummary("Creates and calculates the run of a month: every employee employed in it, prorated over thirty days for the days not employed and unpaid leave, with the components applied in order.");
        runs.MapGet("/{runId:guid}", async (Guid runId, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(runId, ct)))
            .RequirePermission(PayrollPermissions.RunRead);
        runs.MapPost("/{runId:guid}/calculate", async (Guid runId, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.CalculateAsync(runId, ct)))
            .RequirePermission(PayrollPermissions.RunManage)
            .WithSummary("Recalculates a draft or calculated run after employees, components or leave changed.");
        runs.MapPost("/{runId:guid}/submit", async (Guid runId, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitAsync(runId, ct)))
            .RequirePermission(PayrollPermissions.RunManage)
            .WithSummary("Submits the run: held for approval when a workflow definition is active for payroll runs, approved at once otherwise.");
        runs.MapPost("/{runId:guid}/post", async (Guid runId, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(runId, ct)))
            .RequirePermission(PayrollPermissions.RunManage)
            .WithSummary("Posts an approved run: salaries and employer contributions by component against the liabilities withheld, and each employee's net pay as a payable due on the pay date.");
        runs.MapPost("/{runId:guid}/pay", async (Guid runId, PayRunRequest request, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.PayAsync(runId, request, ct)))
            .RequirePermission(PayrollPermissions.RunPay)
            .WithSummary("Pays every unpaid employee of a posted run from one bank account on a date, one payment per employee settling the payable.");
        runs.MapPost("/{runId:guid}/reverse", async (Guid runId, ReverseRunRequest request, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(runId, request, ct)))
            .RequirePermission(PayrollPermissions.RunManage)
            .WithSummary("Reverses a posted, unpaid run: the journal is mirrored and the payables closed; the period can then be run again.");
        runs.MapPost("/{runId:guid}/cancel", async (Guid runId, RunService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(runId, ct)))
            .RequirePermission(PayrollPermissions.RunManage)
            .WithSummary("Cancels a run that is not yet posted, withdrawing its approval request when one is pending.");

        return api;
    }
}
