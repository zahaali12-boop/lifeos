using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Expenses.Application;
using Quicker.Expenses.Contracts;
using Quicker.Expenses.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Workflow.Contracts;

namespace Quicker.Expenses;

public static class ExpensesModule
{
    public static IServiceCollection AddExpensesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Entries of this module belong to its documents, which keep their own subledgers through Payables and Banking (A-140).
        services.AddSingleton(new PostingDocumentModule("expenses"));
        PermissionCatalog.Register(ExpensesPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(ExpenseDocumentTypes.Expense, ExpensesPermissions.ExpenseRead), new NumberedDocumentType(ExpenseDocumentTypes.Replenishment, ExpensesPermissions.FundRead));
        CustomFieldHosts.Register(new CustomFieldHost(ExpenseDocumentTypes.Expense, "app.exp_expenses", "custom_fields"));
        services.AddModuleDbContext<ExpensesDbContext>();
        services.AddScoped<CategoryService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<FundService>();
        services.AddScoped<QuickExpenseService>();
        services.AddScoped<IWorkflowSubjectProvider, ExpenseWorkflowSubject>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission("expense", ExpensesPermissions.ExpenseRead));
        services.AddSingleton(new RecordReadPermission("petty_cash_fund", ExpensesPermissions.FundRead));
        services.AddSingleton(new RecordReadPermission("fund_replenishment", ExpensesPermissions.FundRead));
        services.AddScoped<IRecordCompanies, ExpensesRecordCompanies>();
        return services;
    }
}
