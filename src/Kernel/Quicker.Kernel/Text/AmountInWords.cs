using System.Globalization;
using System.Text;
using Quicker.Kernel.Amounts;

namespace Quicker.Kernel.Text;

/// <summary>Grammatical gender of a currency unit name, needed for Arabic number agreement.</summary>
public enum Gender
{
    Masculine,
    Feminine,
}

/// <summary>Names of a currency's major and minor unit in one language, with the forms Arabic grammar needs.</summary>
public sealed record CurrencyUnitNames(
    string Singular,
    string Dual,
    string Plural,
    Gender Gender,
    string MinorSingular,
    string MinorDual,
    string MinorPlural,
    Gender MinorGender)
{
    public static CurrencyUnitNames English(string singular, string plural, string minorSingular, string minorPlural) =>
        new(singular, plural, plural, Gender.Masculine, minorSingular, minorPlural, minorPlural, Gender.Masculine);
}

/// <summary>
/// Spells amounts in words for cheques and invoices, in English and Arabic (ADR-0027). Arabic follows the standard
/// rules: units 1–2 agree with the noun's gender, 3–10 take the opposite gender, 11–99 are compound, counted nouns are
/// singular after 1, dual after 2, plural after 3–10 and singular (accusative sense) after 11–99 and round hundreds.
/// </summary>
public static class AmountInWords
{
    private static readonly Dictionary<string, (CurrencyUnitNames En, CurrencyUnitNames Ar)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IQD"] = (CurrencyUnitNames.English("Iraqi dinar", "Iraqi dinars", "fils", "fils"),
                   new CurrencyUnitNames("دينار عراقي", "ديناران عراقيان", "دنانير عراقية", Gender.Masculine, "فلس", "فلسان", "فلوس", Gender.Masculine)),
        ["USD"] = (CurrencyUnitNames.English("US dollar", "US dollars", "cent", "cents"),
                   new CurrencyUnitNames("دولار أمريكي", "دولاران أمريكيان", "دولارات أمريكية", Gender.Masculine, "سنت", "سنتان", "سنتات", Gender.Masculine)),
        ["EUR"] = (CurrencyUnitNames.English("euro", "euros", "cent", "cents"),
                   new CurrencyUnitNames("يورو", "يوروان", "يوروات", Gender.Masculine, "سنت", "سنتان", "سنتات", Gender.Masculine)),
        ["AED"] = (CurrencyUnitNames.English("UAE dirham", "UAE dirhams", "fils", "fils"),
                   new CurrencyUnitNames("درهم إماراتي", "درهمان إماراتيان", "دراهم إماراتية", Gender.Masculine, "فلس", "فلسان", "فلوس", Gender.Masculine)),
        ["SAR"] = (CurrencyUnitNames.English("Saudi riyal", "Saudi riyals", "halala", "halalas"),
                   new CurrencyUnitNames("ريال سعودي", "ريالان سعوديان", "ريالات سعودية", Gender.Masculine, "هللة", "هللتان", "هللات", Gender.Feminine)),
        ["KWD"] = (CurrencyUnitNames.English("Kuwaiti dinar", "Kuwaiti dinars", "fils", "fils"),
                   new CurrencyUnitNames("دينار كويتي", "ديناران كويتيان", "دنانير كويتية", Gender.Masculine, "فلس", "فلسان", "فلوس", Gender.Masculine)),
        ["BHD"] = (CurrencyUnitNames.English("Bahraini dinar", "Bahraini dinars", "fils", "fils"),
                   new CurrencyUnitNames("دينار بحريني", "ديناران بحرينيان", "دنانير بحرينية", Gender.Masculine, "فلس", "فلسان", "فلوس", Gender.Masculine)),
        ["OMR"] = (CurrencyUnitNames.English("Omani rial", "Omani rials", "baisa", "baisa"),
                   new CurrencyUnitNames("ريال عماني", "ريالان عمانيان", "ريالات عمانية", Gender.Masculine, "بيسة", "بيستان", "بيسات", Gender.Feminine)),
        ["GBP"] = (CurrencyUnitNames.English("pound sterling", "pounds sterling", "penny", "pence"),
                   new CurrencyUnitNames("جنيه إسترليني", "جنيهان إسترلينيان", "جنيهات إسترلينية", Gender.Masculine, "بنس", "بنسان", "بنسات", Gender.Masculine)),
        ["JPY"] = (CurrencyUnitNames.English("yen", "yen", "sen", "sen"),
                   new CurrencyUnitNames("ين ياباني", "ينان يابانيان", "ينات يابانية", Gender.Masculine, "سن", "سنان", "سنات", Gender.Masculine)),
    };

    /// <summary>Registers or replaces the unit names for a currency (tenant configuration can extend the table).</summary>
    public static void RegisterCurrency(string code, CurrencyUnitNames english, CurrencyUnitNames arabic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Units[code] = (english, arabic);
    }

    public static bool SupportsCurrency(string code) => Units.ContainsKey(code);

    public static string Spell(Money money, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!money.IsRoundedToMinorUnit)
        {
            throw new ArgumentException("Round the amount to the currency's minor unit before spelling it.", nameof(money));
        }

        if (money.IsNegative)
        {
            throw new ArgumentException("Negative amounts are not spelled; spell the absolute value and label it.", nameof(money));
        }

        if (!Units.TryGetValue(money.Currency.Code, out var names))
        {
            throw new NotSupportedException($"No unit names registered for {money.Currency.Code}.");
        }

        var major = decimal.Truncate(money.Amount);
        var minorScale = Currency.Pow10(money.Currency.MinorUnits);
        var minor = decimal.Truncate((money.Amount - major) * minorScale);

        return language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
            ? SpellArabic((long)major, (long)minor, money.Currency.MinorUnits, names.Ar)
            : SpellEnglish((long)major, (long)minor, money.Currency.MinorUnits, names.En);
    }

    // ---------------------------------------------------------------- English

    private static readonly string[] EnOnes =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] EnTens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly string[] EnScales = ["", "thousand", "million", "billion", "trillion"];

    private static string SpellEnglish(long major, long minor, int minorUnits, CurrencyUnitNames names)
    {
        var sb = new StringBuilder();
        sb.Append(EnglishNumber(major)).Append(' ').Append(major == 1 ? names.Singular : names.Plural);
        if (minorUnits > 0)
        {
            sb.Append(" and ").Append(EnglishNumber(minor)).Append(' ').Append(minor == 1 ? names.MinorSingular : names.MinorPlural);
        }

        sb.Append(" only");
        var text = sb.ToString();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string EnglishNumber(long n)
    {
        if (n == 0)
        {
            return EnOnes[0];
        }

        var parts = new List<string>();
        var scale = 0;
        while (n > 0)
        {
            var chunk = (int)(n % 1000);
            if (chunk > 0)
            {
                var text = EnglishChunk(chunk);
                parts.Insert(0, scale > 0 ? $"{text} {EnScales[scale]}" : text);
            }

            n /= 1000;
            scale++;
        }

        return string.Join(" ", parts);
    }

    private static string EnglishChunk(int n)
    {
        var sb = new StringBuilder();
        if (n >= 100)
        {
            sb.Append(EnOnes[n / 100]).Append(" hundred");
            n %= 100;
            if (n > 0)
            {
                sb.Append(" and ");
            }
        }

        if (n >= 20)
        {
            sb.Append(EnTens[n / 10]);
            if (n % 10 > 0)
            {
                sb.Append('-').Append(EnOnes[n % 10]);
            }
        }
        else if (n > 0)
        {
            sb.Append(EnOnes[n]);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- Arabic

    private static readonly string[] ArOnesM =
    [
        "صفر", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة", "عشرة",
        "أحد عشر", "اثنا عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر", "سبعة عشر", "ثمانية عشر", "تسعة عشر",
    ];

    private static readonly string[] ArOnesF =
    [
        "صفر", "واحدة", "اثنتان", "ثلاث", "أربع", "خمس", "ست", "سبع", "ثمان", "تسع", "عشر",
        "إحدى عشرة", "اثنتا عشرة", "ثلاث عشرة", "أربع عشرة", "خمس عشرة", "ست عشرة", "سبع عشرة", "ثماني عشرة", "تسع عشرة",
    ];

    private static readonly string[] ArTens = ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"];

    private static readonly string[] ArHundreds = ["", "مائة", "مائتان", "ثلاثمائة", "أربعمائة", "خمسمائة", "ستمائة", "سبعمائة", "ثمانمائة", "تسعمائة"];

    // Scale words: singular, dual, plural (3–10), singular-after-11–99 (same as singular); scales are masculine.
    private static readonly (string One, string Two, string Plural)[] ArScales =
    [
        ("", "", ""),
        ("ألف", "ألفان", "آلاف"),
        ("مليون", "مليونان", "ملايين"),
        ("مليار", "ملياران", "مليارات"),
        ("تريليون", "تريليونان", "تريليونات"),
    ];

    private static string SpellArabic(long major, long minor, int minorUnits, CurrencyUnitNames names)
    {
        var sb = new StringBuilder();
        sb.Append(ArabicAmount(major, names.Singular, names.Dual, names.Plural, names.Gender));
        if (minorUnits > 0)
        {
            sb.Append(" و").Append(ArabicAmount(minor, names.MinorSingular, names.MinorDual, names.MinorPlural, names.MinorGender));
        }

        sb.Append(" فقط لا غير");
        return sb.ToString();
    }

    /// <summary>Number followed by the counted noun in the correct form.</summary>
    private static string ArabicAmount(long n, string singular, string dual, string plural, Gender gender)
    {
        if (n == 0)
        {
            return $"صفر {singular}";
        }

        if (n == 1)
        {
            return $"{singular} {(gender == Gender.Masculine ? "واحد" : "واحدة")}";
        }

        if (n == 2)
        {
            return dual;
        }

        var lastTwo = n % 100;
        var noun = lastTwo is >= 3 and <= 10 ? plural : singular;
        return $"{ArabicNumber(n, gender)} {noun}";
    }

    private static string ArabicNumber(long n, Gender gender)
    {
        if (n == 0)
        {
            return ArOnesM[0];
        }

        var groups = new List<string>();
        var scale = 0;
        while (n > 0)
        {
            var chunk = (int)(n % 1000);
            if (chunk > 0)
            {
                groups.Insert(0, ArabicGroup(chunk, scale, scale == 0 ? gender : Gender.Masculine));
            }

            n /= 1000;
            scale++;
        }

        return string.Join(" و", groups);
    }

    private static string ArabicGroup(int chunk, int scale, Gender gender)
    {
        if (scale == 0)
        {
            return ArabicChunk(chunk, gender);
        }

        var (one, two, plural) = ArScales[scale];
        if (chunk == 1)
        {
            return one;
        }

        if (chunk == 2)
        {
            return two;
        }

        var lastTwo = chunk % 100;
        var scaleWord = lastTwo is >= 3 and <= 10 ? plural : one;
        return $"{ArabicChunk(chunk, Gender.Masculine)} {scaleWord}";
    }

    private static string ArabicChunk(int n, Gender gender)
    {
        var parts = new List<string>();
        var hundreds = n / 100;
        var rest = n % 100;
        if (hundreds > 0)
        {
            parts.Add(ArHundreds[hundreds]);
        }

        if (rest > 0)
        {
            var ones = gender == Gender.Masculine ? ArOnesM : ArOnesF;
            if (rest < 20)
            {
                parts.Add(ones[rest]);
            }
            else
            {
                var unit = rest % 10;
                var tens = ArTens[rest / 10];
                parts.Add(unit == 0 ? tens : $"{ones[unit]} و{tens}");
            }
        }

        return string.Join(" و", parts);
    }

    /// <summary>Formats a number with the requested digit style (Western 0-9 or Eastern Arabic ٠-٩).</summary>
    public static string FormatDigits(decimal value, int decimals, bool easternArabicDigits, CultureInfo? culture = null)
    {
        var text = value.ToString($"N{decimals}", culture ?? CultureInfo.InvariantCulture);
        if (!easternArabicDigits)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c);
        }

        return sb.ToString();
    }
}
