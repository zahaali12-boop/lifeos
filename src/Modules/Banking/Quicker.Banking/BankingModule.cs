using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Banking.Application;
using Quicker.Banking.Contracts;
using Quicker.Banking.Persistence;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Printing.Contracts;
namespace Quicker.Banking;

public static class BankingModule
{
    public static IServiceCollection AddBankingModule(this IServiceCollection services)
    {
        // Entries of this module belong to its documents, which keep their own subledger (A-140).
        services.AddSingleton(new PostingDocumentModule("banking"));
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(BankingPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(BankDocumentTypes.Payment, BankingPermissions.PaymentRead), new NumberedDocumentType(BankDocumentTypes.Receipt, BankingPermissions.ReceiptRead));
        CustomFieldHosts.Register(new CustomFieldHost(BankDocumentTypes.Payment, "app.bnk_payments", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost(BankDocumentTypes.Receipt, "app.bnk_payments", "custom_fields"));
        services.AddModuleDbContext<BankingDbContext>();
        services.AddScoped<BankAccountService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<CustomerReceiptService>();
        services.AddScoped<IPrintModelProvider, ReceiptPrintModel>();
        services.AddScoped<ChequeRegistry>();
        services.AddScoped<ChequeBookService>();
        services.AddScoped<ChequeService>();
        NumberedDocumentTypes.Register(new NumberedDocumentType(BankDocumentTypes.ChequeDeposit, BankingPermissions.ChequeRead));
        services.AddScoped<StatementService>();
        services.AddScoped<ReconciliationService>();
        NumberedDocumentTypes.Register(new NumberedDocumentType(BankDocumentTypes.Reconciliation, BankingPermissions.ReconciliationRead));
        services.AddScoped<IBankAccountDirectory>(static sp => sp.GetRequiredService<BankAccountService>());
        services.AddScoped<IBankTransactions, BankTransactionService>();
        services.AddScoped<IPayments, PaymentsGateway>();
        // A journal line on a bank or cash account records the movement in the box it names (A-140, A-194).
        services.AddScoped<IJournalSubledger, BankJournalSubledger>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission("bank_payment", BankingPermissions.PaymentRead));
        services.AddSingleton(new RecordReadPermission("bank_receipt", BankingPermissions.ReceiptRead));
        services.AddSingleton(new RecordReadPermission("bank_account", BankingPermissions.BankAccountRead));
        services.AddSingleton(new RecordReadPermission("bank_cheque", BankingPermissions.ChequeRead));
        services.AddSingleton(new RecordReadPermission("bank_cheque_deposit", BankingPermissions.ChequeRead));
        services.AddSingleton(new RecordReadPermission("bank_cheque_book", BankingPermissions.ChequeRead));
        services.AddSingleton(new RecordReadPermission("bank_statement", BankingPermissions.ReconciliationRead));
        services.AddSingleton(new RecordReadPermission("bank_reconciliation", BankingPermissions.ReconciliationRead));
        services.AddScoped<IRecordCompanies, BankingRecordCompanies>();
        return services;
    }
}
