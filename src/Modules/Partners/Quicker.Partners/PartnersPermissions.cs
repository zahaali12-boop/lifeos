using Quicker.Identity.Contracts;

namespace Quicker.Partners;

/// <summary>Permission keys of the partner master: the supplier side (purchaser, payables) and the customer side with its CRM (sales, receivables).</summary>
public static class PartnersPermissions
{
    public const string SupplierRead = "partners.supplier.read";
    public const string SupplierManage = "partners.supplier.manage";
    public const string SupplierRevealBankAccount = "partners.supplier.reveal_bank_account";
    public const string TermsManage = "partners.terms.manage";
    public const string CustomerRead = "partners.customer.read";
    public const string CustomerManage = "partners.customer.manage";
    public const string CreditManage = "partners.credit.manage";
    public const string SalesSetupManage = "partners.sales_setup.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(SupplierRead, "partners", "Read partners, contacts, addresses, masked bank accounts, tax registrations, supplier accounts, terms, groups and withholding codes"),
        new(SupplierManage, "partners", "Create and edit partners, their contacts, addresses, bank accounts and tax registrations, and supplier accounts with their terms, tolerances and holds"),
        new(SupplierRevealBankAccount, "partners", "Reveal a partner's full bank account number or IBAN (every reveal is audited)"),
        new(TermsManage, "partners", "Create and edit payment terms, delivery terms, supplier and customer groups and withholding tax codes"),
        new(CustomerRead, "partners", "Read partners, customer accounts, sales reps, commission plans, the pipeline, opportunities, CRM activities and the Customer 360 view"),
        new(CustomerManage, "partners", "Create and edit partners as customers, their contacts and addresses, customer accounts (not their credit), opportunities and CRM activities"),
        new(CreditManage, "partners", "Set customers' credit limits, exposure basis and overdue blocks, and put accounts on credit hold, block or release them"),
        new(SalesSetupManage, "partners", "Create and edit sales reps, commission plans and the pipeline's stages"),
    ];
}
