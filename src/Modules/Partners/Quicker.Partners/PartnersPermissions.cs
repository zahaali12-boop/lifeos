using Quicker.Identity.Contracts;

namespace Quicker.Partners;

/// <summary>Permission keys of the partner master (supplier side); the purchaser and payables templates grant them.</summary>
public static class PartnersPermissions
{
    public const string SupplierRead = "partners.supplier.read";
    public const string SupplierManage = "partners.supplier.manage";
    public const string SupplierRevealBankAccount = "partners.supplier.reveal_bank_account";
    public const string TermsManage = "partners.terms.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(SupplierRead, "partners", "Read partners, contacts, addresses, masked bank accounts, tax registrations, supplier accounts, terms, groups and withholding codes"),
        new(SupplierManage, "partners", "Create and edit partners, their contacts, addresses, bank accounts and tax registrations, and supplier accounts with their terms, tolerances and holds"),
        new(SupplierRevealBankAccount, "partners", "Reveal a partner's full bank account number or IBAN (every reveal is audited)"),
        new(TermsManage, "partners", "Create and edit payment terms, delivery terms, supplier groups and withholding tax codes"),
    ];
}
