using Quicker.Identity.Contracts;
using Quicker.Kernel.Text;

namespace Quicker.Identity;

public sealed record RoleTemplate(string Code, LocalizedText Name, string Description, IReadOnlyList<string> Grants);

/// <summary>
/// Roles every new tenant starts with. Wildcard grants reference module prefixes so the templates stay valid as
/// modules add permission keys; admins refine them in the UI.
/// </summary>
public static class RoleTemplates
{
    public static readonly IReadOnlyList<RoleTemplate> All =
    [
        new("owner", LocalizedText.Bilingual("Owner", "المالك"), "Full access, including tenant settings and billing", ["*"]),
        new("admin", LocalizedText.Bilingual("Administrator", "مدير النظام"), "Full access to configuration and all modules", ["*"]),
        // Reopening a closed period is a controller's decision, kept apart from posting (default SoD rule), so the
        // template names the accounting areas instead of "accounting.*": a default role must be assignable on its own.
        new("accountant", LocalizedText.Bilingual("Accountant", "محاسب"), "General ledger, period close, finance and reporting",
            ["accounting.journal.*", "accounting.ledger.*", "accounting.chart.*", "accounting.dimension.*", "accounting.period.manage", "accounting.period.post_in_soft_closed", "finance.*", "tax.*", "reporting.*", "organization.company.read", "identity.user.read"]),
        new("ar_clerk", LocalizedText.Bilingual("Receivables clerk", "موظف الذمم المدينة"), "Customer invoices, receipts, statements and dunning",
            ["sales.invoice.*", "receivables.*", "partners.customer.read", "reporting.report.run"]),
        new("ap_clerk", LocalizedText.Bilingual("Payables clerk", "موظف الذمم الدائنة"), "Supplier invoices, matching and payments",
            ["purchasing.invoice.*", "payables.*", "partners.supplier.read", "reporting.report.run"]),
        new("warehouse_operator", LocalizedText.Bilingual("Warehouse operator", "أمين مستودع"), "Receiving, picking, transfers, counts and scanning",
            ["inventory.receipt.*", "inventory.pick.*", "inventory.transfer.*", "inventory.count.*", "inventory.item.read"]),
        new("sales_rep", LocalizedText.Bilingual("Sales representative", "مندوب مبيعات"), "Customers, quotes and orders",
            ["sales.quote.*", "sales.order.*", "partners.customer.*", "inventory.item.read", "reporting.report.run"]),
        new("purchaser", LocalizedText.Bilingual("Purchaser", "مسؤول مشتريات"), "Requisitions, RFQs, purchase orders and suppliers",
            ["purchasing.requisition.*", "purchasing.rfq.*", "purchasing.order.*", "partners.supplier.*", "inventory.item.read"]),
        new("approver", LocalizedText.Bilingual("Approver", "معتمد"), "Approves documents routed by workflow", ["workflow.request.approve", "workflow.request.read"]),
        new("auditor", LocalizedText.Bilingual("Auditor", "مدقق"), "Read-only access to books, documents and the audit log",
            ["accounting.ledger.read", "accounting.journal.read", "reporting.*", "audit.event.read", "audit.event.export", "audit.chain.verify", "identity.user.read", "identity.role.read", "identity.sod.read"]),
    ];

    public static RoleTemplate? Find(string code) => All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal));
}

/// <summary>Segregation-of-duties rules every tenant starts with (ADR-0014). Admins can add more.</summary>
public static class DefaultSodRules
{
    public sealed record Rule(string PermissionA, string PermissionB, string Severity, LocalizedText Rationale);

    public static readonly IReadOnlyList<Rule> All =
    [
        new("partners.supplier.manage", "payables.payment.post", "block", LocalizedText.Bilingual("Creating suppliers and paying them must be separated.", "يجب فصل إنشاء الموردين عن سداد مدفوعاتهم.")),
        new("purchasing.order.approve", "purchasing.invoice.post", "warn", LocalizedText.Bilingual("Approving purchase orders and posting supplier invoices should be separated.", "يفضل فصل اعتماد أوامر الشراء عن ترحيل فواتير الموردين.")),
        new("accounting.journal.post", "accounting.period.reopen", "block", LocalizedText.Bilingual("Posting journals and reopening periods must be separated.", "يجب فصل ترحيل القيود عن إعادة فتح الفترات.")),
        new("sales.invoice.post", "receivables.writeoff.post", "warn", LocalizedText.Bilingual("Invoicing customers and writing off their balances should be separated.", "يفضل فصل إصدار فواتير العملاء عن شطب أرصدتهم.")),
        new("identity.role.manage", "identity.assignment.manage", "warn", LocalizedText.Bilingual("Defining roles and assigning them should be separated.", "يفضل فصل تعريف الأدوار عن إسنادها.")),
        new("inventory.count.approve", "inventory.adjustment.post", "warn", LocalizedText.Bilingual("Approving counts and posting adjustments should be separated.", "يفضل فصل اعتماد الجرد عن ترحيل التسويات.")),
    ];
}

public static class IdentityRegistration
{
    public static void RegisterPermissions() => PermissionCatalog.Register(IdentityPermissions.All);
}
