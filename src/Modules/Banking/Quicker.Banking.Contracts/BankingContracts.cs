using Quicker.Kernel.Text;

namespace Quicker.Banking.Contracts;

public static class BankDocumentTypes
{
    public const string Payment = "bank_payment";
    public const string Transaction = "bank_transaction";
}

public static class BankAccountKinds
{
    public const string Bank = "bank";
    public const string Cash = "cash";
    public const string PettyCash = "petty_cash";

    public static readonly IReadOnlyList<string> All = [Bank, Cash, PettyCash];
}

public sealed record BankAccountInfo(Guid Id, Guid CompanyId, string Code, LocalizedText Name, string Kind, string Currency, Guid GlAccountId, string? BankName, string? IbanMasked, Guid? BranchId, bool IsActive);

public interface IBankAccountDirectory
{
    Task<BankAccountInfo?> FindAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BankAccountInfo>> ListAsync(Guid companyId, CancellationToken cancellationToken = default);
}
