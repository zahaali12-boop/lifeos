using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Payroll.Application;
using Quicker.Payroll.Contracts;
using Quicker.Payroll.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Printing.Contracts;
using Quicker.Workflow.Contracts;

namespace Quicker.Payroll;

public static class PayrollModule
{
    public static IServiceCollection AddPayrollModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Entries of this module belong to its runs, whose net pay keeps its subledger through Payables (A-181).
        services.AddSingleton(new PostingDocumentModule("payroll"));
        PermissionCatalog.Register(PayrollPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(PayrollDocumentTypes.Run, PayrollPermissions.RunRead), new NumberedDocumentType(PayrollDocumentTypes.Employee, PayrollPermissions.EmployeeRead));
        services.AddModuleDbContext<PayrollDbContext>();
        services.AddScoped<ComponentService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<LeaveService>();
        services.AddScoped<RunService>();
        services.AddScoped<IWorkflowSubjectProvider, PayrollWorkflowSubject>();
        services.AddScoped<IPrintModelProvider, PayslipPrintModel>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission(ComponentService.AuditEntity, PayrollPermissions.SetupRead));
        services.AddSingleton(new RecordReadPermission(EmployeeService.AuditEntity, PayrollPermissions.EmployeeRead));
        services.AddSingleton(new RecordReadPermission(LeaveService.AuditEntity, PayrollPermissions.LeaveRead));
        services.AddSingleton(new RecordReadPermission(RunService.AuditEntity, PayrollPermissions.RunRead));
        services.AddScoped<IRecordCompanies, PayrollRecordCompanies>();
        return services;
    }
}
