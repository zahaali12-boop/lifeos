namespace Quicker.Migrator.Demo;

public sealed record DemoBranch(string Code, string NameEn, string NameAr, string CityEn, string CityAr);

public sealed record DemoCurrency(string Code, int DisplayDecimals, decimal CashRounding);

public sealed record DemoCompany(
    string Code,
    string LegalEn,
    string LegalAr,
    string TradeEn,
    string TradeAr,
    string Country,
    string FunctionalCurrency,
    string ReportingCurrency,
    string TimeZone,
    string TaxId,
    string Registration,
    string Street,
    string City,
    IReadOnlyList<DemoBranch> Branches,
    IReadOnlyList<DemoCurrency> Currencies);

/// <summary>A demo person: mailbox name, display name, default role, UI locale and digits; <c>CompanyScope</c> narrows the role to one company (code).</summary>
public sealed record DemoUser(string Local, string DisplayName, string Role, string Locale = "en", string DigitStyle = "western", string? CompanyScope = null)
{
    public string Email => Local + "@" + DemoData.EmailDomain;
}

/// <summary>
/// The demo tenant of M1 (roadmap 1.12, ADR-0029, ASSUMPTIONS Q7): one group with an IQD-functional trading
/// company, a USD-functional import company and an AED-functional entity, five branches, ten users covering
/// every default role, and a year of exchange rates. Later milestones extend the same tenant.
/// </summary>
public static class DemoData
{
    public const string Slug = "demo";
    public const string TenantName = "Quicker Demo Group";
    public const string EmailDomain = "quicker.example";
    public const string Password = "DemoPass2026!";
    public const string OfficialRateType = "official";
    public const string MarketRateType = "market";

    /// <summary>The rate series covers this many days ending today (in Baghdad).</summary>
    public const int RateDays = 365;

    public const string BaghdadTimeZone = "Asia/Baghdad";

    public static readonly Guid TenantId = DemoIds.For("tenant");

    public static readonly IReadOnlyList<DemoCompany> Companies =
    [
        new(
            "IQT", "Al-Rafidain Trading Company Ltd.", "شركة الرافدين للتجارة المحدودة", "Rafidain Trading", "الرافدين للتجارة",
            "IQ", "IQD", "USD", BaghdadTimeZone, "IQ-100200300", "CR-BGD-48213", "Al-Karrada, Arasat Street 14", "Baghdad",
            [
                new("BGD", "Baghdad head office", "بغداد – المقر الرئيسي", "Baghdad", "بغداد"),
                new("BSR", "Basra branch", "فرع البصرة", "Basra", "البصرة"),
                new("EBL", "Erbil branch", "فرع أربيل", "Erbil", "أربيل"),
            ],
            [new("IQD", 0, 250m), new("USD", 2, 0m)]),
        new(
            "USI", "Tigris Import LLC", "شركة دجلة للاستيراد ذ.م.م", "Tigris Import", "دجلة للاستيراد",
            "IQ", "USD", "IQD", BaghdadTimeZone, "IQ-400500600", "CR-BGD-51907", "Al-Mansour, Free Zone Road 3", "Baghdad",
            [new("BGD", "Baghdad warehouse and office", "بغداد – المخزن والمكتب", "Baghdad", "بغداد")],
            [new("USD", 2, 0m), new("IQD", 0, 250m), new("EUR", 2, 0m)]),
        new(
            "AEG", "Gulf Gate General Trading LLC", "بوابة الخليج للتجارة العامة ذ.م.م", "Gulf Gate", "بوابة الخليج",
            "AE", "AED", "USD", "Asia/Dubai", "100987654300003", "DED-1234567", "Jebel Ali Free Zone, LB 12", "Dubai",
            [new("DXB", "Dubai office", "مكتب دبي", "Dubai", "دبي")],
            [new("AED", 2, 0.25m), new("USD", 2, 0m)]),
    ];

    public static readonly IReadOnlyList<DemoUser> Users =
    [
        new("owner", "Zaid Al-Hashimi", "owner"),
        new("admin", "Sara Kareem", "admin"),
        new("accountant", "Hind Jabbar", "accountant", Locale: "ar", DigitStyle: "eastern_arabic"),
        new("ar", "Ali Hussein", "ar_clerk"),
        new("ap", "Maryam Saleh", "ap_clerk", Locale: "ar"),
        new("purchasing", "Omar Talib", "purchaser"),
        new("sales", "Noor Abbas", "sales_rep", CompanyScope: "IQT"),
        new("warehouse", "Karim Faris", "warehouse_operator", CompanyScope: "IQT"),
        new("approvals", "Layla Ahmed", "approver"),
        new("auditor", "Dana Yousif", "auditor"),
    ];

    public static DemoUser Owner => Users[0];

    public static Guid UserId(DemoUser user) => DemoIds.For("user:" + user.Local, Users.IndexOf(user));

    public static Guid MembershipId(DemoUser user) => DemoIds.For("membership:" + user.Local, Users.IndexOf(user));

    private static int IndexOf(this IReadOnlyList<DemoUser> users, DemoUser user)
    {
        for (var i = 0; i < users.Count; i++)
        {
            if (ReferenceEquals(users[i], user))
            {
                return i;
            }
        }

        throw new ArgumentException("Not a demo user.", nameof(user));
    }
}
