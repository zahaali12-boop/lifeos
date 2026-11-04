namespace Quicker.Accounting.Application;

public sealed record SaveChartRequest(string Code, IReadOnlyDictionary<string, string> Name, string AccountCodeFormat = "", Guid? CompanyId = null, bool IsActive = true);

public sealed record ChartSummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string? TemplateCode,
    string AccountCodeFormat,
    Guid? CompanyId,
    bool IsActive,
    int AccountCount,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AccountSummary>? Accounts = null);

public sealed record FromTemplateRequest(string TemplateCode, string Code, IReadOnlyDictionary<string, string>? Name = null, Guid? CompanyId = null, bool Shared = true);

public sealed record TemplateSummary(string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, string> Description, int Accounts, IReadOnlyList<string> Roles, string? StatutoryChartCode);

public sealed record SaveAccountRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Type,
    string? ParentCode = null,
    Guid? ParentId = null,
    string Subtype = "",
    string? CategoryCode = null,
    bool IsHeader = false,
    bool IsControl = false,
    string? SubledgerType = null,
    string? CurrencyRestriction = null,
    bool AllowManualPosting = true,
    bool RevalueFx = false,
    string? CashFlowCategory = null,
    string? DefaultRole = null,
    Guid? CompanyId = null,
    bool IsActive = true);

public sealed record AccountSummary(
    Guid Id,
    Guid ChartId,
    Guid? ParentId,
    string? ParentCode,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Type,
    string Subtype,
    string? CategoryCode,
    bool IsHeader,
    bool IsControl,
    string? SubledgerType,
    string? CurrencyRestriction,
    bool AllowManualPosting,
    bool RevalueFx,
    string? CashFlowCategory,
    string? DefaultRole,
    Guid? CompanyId,
    bool IsActive,
    int Level,
    string Path,
    DateTimeOffset UpdatedAt);

public sealed record DimensionRuleRequest(string DimensionCode, string Rule, Guid? DefaultValueId = null);

public sealed record DimensionRuleSummary(Guid DimensionId, string DimensionCode, string Rule, Guid? DefaultValueId);

public sealed record SaveCategoryRequest(IReadOnlyDictionary<string, string> Name, string Statement, int SortOrder = 0);

public sealed record CategorySummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Statement, int SortOrder, bool IsSystem);

public sealed record ImportRequest(IReadOnlyList<SaveAccountRequest> Accounts);

public sealed record ImportResult(int Created, int Updated, int Unchanged, int Total);

public sealed record MappingRequest(string AccountCode, string StatutoryCode);

public sealed record MappingSummary(Guid AccountId, string AccountCode, IReadOnlyDictionary<string, string> AccountName, string? StatutoryCode, IReadOnlyDictionary<string, string>? StatutoryName);

public sealed record StatutoryAccount(string Code, int Level, IReadOnlyDictionary<string, string> Name);

public sealed record StatutoryChartSummary(string Code, IReadOnlyDictionary<string, string> Name, string Notes, IReadOnlyList<StatutoryAccount> Accounts);

public sealed record AssignChartRequest(Guid? ChartId);

public sealed record CompanyAccountingSettings(Guid CompanyId, string CompanyCode, Guid? ChartId, string? ChartCode, Guid? PostingProfileId);

public sealed record CheckLineRequest(Guid CompanyId, IReadOnlyDictionary<string, Guid>? Dimensions = null, string? Currency = null, bool Manual = false);

public sealed record CheckLineResult(Guid AccountId, string AccountCode, IReadOnlyDictionary<string, Guid> Dimensions);
