using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Quicker.Kernel.Results;

namespace Quicker.Banking.Application;

/// <summary>A statement as a file gave it: the period and balances when the format carries them, and the lines signed in the account's currency.</summary>
public sealed record ParsedStatement(DateOnly? From, DateOnly? To, decimal? OpeningBalance, decimal? ClosingBalance, string? Currency, string? AccountNumber, IReadOnlyList<ParsedLine> Lines);

public sealed record ParsedLine(DateOnly BookingDate, DateOnly? ValueDate, decimal Amount, string Description, string? Counterparty, string? Reference, string? BankCode, string? ExternalId);

/// <summary>How a bank's CSV export reads: columns by header name or 1-based position, and how dates and numbers are written.</summary>
public sealed record StatementMappingSettings(
    string Delimiter = ",",
    bool HasHeader = true,
    int SkipRows = 0,
    string? DateColumn = null,
    string DateFormat = "yyyy-MM-dd",
    string? ValueDateColumn = null,
    string? AmountColumn = null,
    string? DebitColumn = null,
    string? CreditColumn = null,
    string? DescriptionColumn = null,
    string? ReferenceColumn = null,
    string? CounterpartyColumn = null,
    string DecimalSeparator = ".",
    string? ThousandsSeparator = ",",
    bool NegateAmount = false)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static StatementMappingSettings Parse(string? json) => string.IsNullOrWhiteSpace(json) ? new StatementMappingSettings() : JsonSerializer.Deserialize<StatementMappingSettings>(json, Json) ?? new StatementMappingSettings();
}

public static class StatementFormats
{
    public const string Csv = "csv";
    public const string Ofx = "ofx";
    public const string Mt940 = "mt940";
    public const string Camt053 = "camt053";
    public static readonly IReadOnlyList<string> All = [Csv, Ofx, Mt940, Camt053];

    /// <summary>The format a file most likely is, from its name and its first bytes; null when nothing tells.</summary>
    public static string? Guess(string fileName, string head)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(head);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (head.Contains("<OFX", StringComparison.OrdinalIgnoreCase) || head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) || ext is ".ofx" or ".qfx")
        {
            return Ofx;
        }

        if (head.Contains("BkToCstmrStmt", StringComparison.Ordinal) || head.Contains("camt.053", StringComparison.OrdinalIgnoreCase))
        {
            return Camt053;
        }

        if (head.Contains(":20:", StringComparison.Ordinal) || head.Contains(":61:", StringComparison.Ordinal) || ext is ".sta" or ".mt940" or ".940")
        {
            return Mt940;
        }

        return ext is ".csv" or ".txt" ? Csv : null;
    }
}

/// <summary>Reads one statement format into lines; each parser is pure and tested on sample files.</summary>
public static class StatementParsers
{
    private static readonly string[] DateFallbacks = ["yyyy-MM-dd", "dd/MM/yyyy", "dd.MM.yyyy", "dd-MM-yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy/MM/dd", "d/M/yyyy", "dd MMM yyyy"];

    public static Result<ParsedStatement> Parse(string format, string content, StatementMappingSettings? mapping)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            return format switch
            {
                StatementFormats.Csv => ParseCsv(content, mapping ?? new StatementMappingSettings()),
                StatementFormats.Ofx => ParseOfx(content),
                StatementFormats.Mt940 => ParseMt940(content),
                StatementFormats.Camt053 => ParseCamt053(content),
                _ => Error.Validation("statement.format_unknown", "The format is csv, ofx, mt940 or camt053.").WithWhy(("format", format)),
            };
        }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException or InvalidOperationException or ArgumentException)
        {
            return Error.Validation("statement.unreadable", "The file could not be read as a statement of this format.").WithWhy(("format", format), ("detail", e.Message));
        }
    }

    // ------------------------------------------------------------------ CSV

    public static Result<ParsedStatement> ParseCsv(string content, StatementMappingSettings mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var delimiter = string.IsNullOrEmpty(mapping.Delimiter) ? ',' : mapping.Delimiter == "\\t" || mapping.Delimiter == "tab" ? '\t' : mapping.Delimiter[0];
        var rows = SplitCsv(content, delimiter).Skip(mapping.SkipRows).Where(static r => r.Any(static c => c.Length > 0)).ToList();
        if (rows.Count == 0)
        {
            return Error.Validation("statement.empty", "The file has no lines.");
        }

        var header = mapping.HasHeader ? rows[0].Select(static h => h.Trim()).ToList() : null;
        var data = mapping.HasHeader ? rows.Skip(1).ToList() : rows;
        int? Column(string? named, params string[] defaults)
        {
            if (!string.IsNullOrWhiteSpace(named))
            {
                if (int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) && position >= 1)
                {
                    return position - 1;
                }

                var byName = header?.FindIndex(h => string.Equals(h, named.Trim(), StringComparison.OrdinalIgnoreCase)) ?? -1;
                return byName >= 0 ? byName : null;
            }

            if (header is null)
            {
                return null;
            }

            foreach (var candidate in defaults)
            {
                var index = header.FindIndex(h => string.Equals(h, candidate, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    return index;
                }
            }

            return null;
        }

        var date = Column(mapping.DateColumn, "date", "booking date", "posting date", "transaction date", "value date");
        var valueDate = Column(mapping.ValueDateColumn, "value date");
        var amount = Column(mapping.AmountColumn, "amount");
        var debit = Column(mapping.DebitColumn, "debit", "withdrawal", "out");
        var credit = Column(mapping.CreditColumn, "credit", "deposit", "in");
        var description = Column(mapping.DescriptionColumn, "description", "details", "narrative", "memo", "text", "particulars");
        var reference = Column(mapping.ReferenceColumn, "reference", "ref", "transaction id", "cheque", "cheque number");
        var counterparty = Column(mapping.CounterpartyColumn, "counterparty", "name", "payee", "beneficiary");
        if (date is null)
        {
            return Error.Validation("statement.date_column_missing", "No date column: name it in the mapping.").WithWhy(("header", header));
        }

        if (amount is null && (debit is null || credit is null))
        {
            return Error.Validation("statement.amount_column_missing", "No amount column, nor a debit and a credit column: name them in the mapping.").WithWhy(("header", header));
        }

        var lines = new List<ParsedLine>();
        var lineNo = 0;
        foreach (var row in data)
        {
            lineNo++;
            string Cell(int? index) => index is { } i && i < row.Count ? row[i].Trim() : string.Empty;
            var day = ParseDate(Cell(date), mapping.DateFormat);
            if (day is null)
            {
                return Error.Validation("statement.date_unreadable", "A date could not be read with the mapping's format.").WithWhy(("line", lineNo), ("value", Cell(date)), ("format", mapping.DateFormat));
            }

            decimal value;
            if (amount is not null)
            {
                value = ParseNumber(Cell(amount), mapping);
            }
            else
            {
                var dr = ParseNumber(Cell(debit), mapping);
                var cr = ParseNumber(Cell(credit), mapping);
                value = cr - Math.Abs(dr);
            }

            if (mapping.NegateAmount)
            {
                value = -value;
            }

            if (value == 0m)
            {
                continue;
            }

            var text = Cell(description);
            var reference0 = Cell(reference);
            var party = Cell(counterparty);
            lines.Add(new ParsedLine(day.Value, ParseDate(Cell(valueDate), mapping.DateFormat), value, text, party.Length > 0 ? party : null, reference0.Length > 0 ? reference0 : null, null, null));
        }

        var dates = lines.Select(static l => l.BookingDate).ToList();
        return new ParsedStatement(dates.Count > 0 ? dates.Min() : null, dates.Count > 0 ? dates.Max() : null, null, null, null, null, lines);
    }

    private static List<List<string>> SplitCsv(string content, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static DateOnly? ParseDate(string text, string preferred)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        foreach (var format in new[] { preferred }.Concat(DateFallbacks))
        {
            if (DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return day;
            }

            if (DateTime.TryParseExact(value, format + " HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) || DateTime.TryParseExact(value, format + " HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
            {
                return DateOnly.FromDateTime(stamp);
            }
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var any) ? DateOnly.FromDateTime(any) : null;
    }

    private static decimal ParseNumber(string text, StatementMappingSettings mapping)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        var value = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        var negative = value.StartsWith('(') && value.EndsWith(')');
        value = value.Trim('(', ')');
        if (!string.IsNullOrEmpty(mapping.ThousandsSeparator))
        {
            value = value.Replace(mapping.ThousandsSeparator, string.Empty, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(mapping.DecimalSeparator) && mapping.DecimalSeparator != ".")
        {
            value = value.Replace(mapping.DecimalSeparator, ".", StringComparison.Ordinal);
        }

        value = new string(value.Where(static c => char.IsAsciiDigit(c) || c is '.' or '-' or '+').ToArray());
        if (value.Length == 0 || value == "-" || value == "+")
        {
            return 0m;
        }

        var parsed = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return negative ? -parsed : parsed;
    }

    // ------------------------------------------------------------------ OFX (SGML 1.x and XML 2.x)

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex OfxTag = new(@"<(?<tag>[A-Z0-9.]+)>(?<value>[^<\r\n]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, RegexTimeout);

    public static Result<ParsedStatement> ParseOfx(string content)
    {
        var body = content;
        var start = body.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            body = body[start..];
        }

        static Dictionary<string, string> Tags(string block)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in OfxTag.Matches(block))
            {
                var value = m.Groups["value"].Value.Trim();
                if (value.Length > 0 && !values.ContainsKey(m.Groups["tag"].Value))
                {
                    values[m.Groups["tag"].Value] = System.Net.WebUtility.HtmlDecode(value);
                }
            }

            return values;
        }

        var lines = new List<ParsedLine>();
        var transactions = OfxTransaction.Matches(body);
        foreach (Match transaction in transactions)
        {
            var tags = Tags(transaction.Groups["body"].Value);
            if (!tags.TryGetValue("DTPOSTED", out var posted) || !tags.TryGetValue("TRNAMT", out var amountText))
            {
                continue;
            }

            var amount = decimal.Parse(amountText.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            var name = tags.GetValueOrDefault("NAME");
            var memo = tags.GetValueOrDefault("MEMO");
            var description = string.Join(" ", new[] { name, memo }.Where(static s => !string.IsNullOrWhiteSpace(s)));
            var reference = tags.GetValueOrDefault("CHECKNUM") ?? tags.GetValueOrDefault("REFNUM");
            lines.Add(new ParsedLine(OfxDate(posted), tags.TryGetValue("DTUSER", out var user) ? OfxDate(user) : null, amount, description, name, reference, tags.GetValueOrDefault("TRNTYPE"), tags.GetValueOrDefault("FITID")));
        }

        var all = Tags(body);
        decimal? closing = all.TryGetValue("BALAMT", out var bal) ? decimal.Parse(bal.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : null;
        var from = all.TryGetValue("DTSTART", out var s) ? OfxDate(s) : (DateOnly?)null;
        var to = all.TryGetValue("DTEND", out var e) ? OfxDate(e) : (DateOnly?)null;
        return new ParsedStatement(from ?? (lines.Count > 0 ? lines.Min(static l => l.BookingDate) : null), to ?? (lines.Count > 0 ? lines.Max(static l => l.BookingDate) : null), null, closing, all.GetValueOrDefault("CURDEF"), all.GetValueOrDefault("ACCTID"), lines);
    }

    private static DateOnly OfxDate(string value)
    {
        var digits = new string(value.TakeWhile(char.IsAsciiDigit).ToArray());
        return DateOnly.ParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ MT940

    private static readonly Regex OfxTransaction = new(@"<STMTTRN>(?<body>.*?)(?=<STMTTRN>|</STMTTRN>|</BANKTRANLIST>|<LEDGERBAL>)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Mt940Line = new(@"^(?<valueDate>\d{6})(?<entryDate>\d{4})?(?<sign>R?[CD])(?<funds>[A-Z])?(?<amount>[\d,]+)(?<type>[A-Z][A-Z0-9]{3})(?<ref>[^/]*)(?://(?<bankRef>.*))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, RegexTimeout);
    private static readonly Regex Mt940Balance = new(@"^(?<sign>[CD])(?<date>\d{6})(?<currency>[A-Z]{3})(?<amount>[\d,]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, RegexTimeout);
    private static readonly Regex Mt940Field = new(@"^:(?<tag>\d{2}[A-Z]?):(?<value>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, RegexTimeout);

    public static Result<ParsedStatement> ParseMt940(string content)
    {
        // Fields start with :nn: at the start of a line; what follows until the next field belongs to it.
        var fields = new List<(string Tag, string Value)>();
        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line == "-")
            {
                continue;
            }

            var m = Mt940Field.Match(line);
            if (m.Success)
            {
                fields.Add((m.Groups["tag"].Value, m.Groups["value"].Value));
            }
            else if (fields.Count > 0)
            {
                var (tag, value) = fields[^1];
                fields[^1] = (tag, value + "\n" + line);
            }
        }

        string? currency = null;
        string? account = null;
        decimal? opening = null;
        decimal? closing = null;
        DateOnly? from = null;
        DateOnly? to = null;
        var lines = new List<ParsedLine>();
        for (var i = 0; i < fields.Count; i++)
        {
            var (tag, value) = fields[i];
            switch (tag)
            {
                case "25":
                    account = value.Trim();
                    break;
                case "60F":
                case "60M":
                    {
                        var b = Mt940Balance.Match(value.Trim());
                        if (b.Success)
                        {
                            currency ??= b.Groups["currency"].Value;
                            opening ??= Signed(b.Groups["sign"].Value, Mt940Amount(b.Groups["amount"].Value));
                            from ??= Mt940Date(b.Groups["date"].Value);
                        }

                        break;
                    }

                case "62F":
                case "62M":
                    {
                        var b = Mt940Balance.Match(value.Trim());
                        if (b.Success)
                        {
                            currency ??= b.Groups["currency"].Value;
                            closing = Signed(b.Groups["sign"].Value, Mt940Amount(b.Groups["amount"].Value));
                            to = Mt940Date(b.Groups["date"].Value);
                        }

                        break;
                    }

                case "61":
                    {
                        var first = value.Split('\n')[0].Trim();
                        var m = Mt940Line.Match(first);
                        if (!m.Success)
                        {
                            throw new FormatException($"Statement line not understood: {first}");
                        }

                        var valueDate = Mt940Date(m.Groups["valueDate"].Value);
                        var bookingDate = valueDate;
                        if (m.Groups["entryDate"].Success)
                        {
                            var md = m.Groups["entryDate"].Value;
                            var candidate = new DateOnly(valueDate.Year, int.Parse(md[..2], CultureInfo.InvariantCulture), int.Parse(md[2..], CultureInfo.InvariantCulture));
                            // An entry date in a later month than the value date belongs to the previous year (December value, January booking).
                            bookingDate = candidate > valueDate.AddMonths(6) ? candidate.AddYears(-1) : candidate < valueDate.AddMonths(-6) ? candidate.AddYears(1) : candidate;
                        }

                        var sign = m.Groups["sign"].Value;
                        var amount = Mt940Amount(m.Groups["amount"].Value);
                        var signed = sign is "D" or "RC" ? -amount : amount;
                        var details = i + 1 < fields.Count && fields[i + 1].Tag == "86" ? fields[i + 1].Value.Replace("\n", " ", StringComparison.Ordinal).Trim() : string.Empty;
                        var reference = m.Groups["ref"].Value.Trim();
                        lines.Add(new ParsedLine(bookingDate, valueDate, signed, details, null, reference.Length > 0 && reference != "NONREF" ? reference : null, m.Groups["type"].Value, m.Groups["bankRef"].Success ? m.Groups["bankRef"].Value.Trim() : null));
                        break;
                    }

                default:
                    break;
            }
        }

        return new ParsedStatement(from ?? (lines.Count > 0 ? lines.Min(static l => l.BookingDate) : null), to ?? (lines.Count > 0 ? lines.Max(static l => l.BookingDate) : null), opening, closing, currency, account, lines);
    }

    private static decimal Mt940Amount(string text) => decimal.Parse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static decimal Signed(string sign, decimal amount) => sign == "D" ? -amount : amount;

    private static DateOnly Mt940Date(string yymmdd) => DateOnly.ParseExact(yymmdd, "yyMMdd", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ CAMT.053

    public static Result<ParsedStatement> ParseCamt053(string content)
    {
        var document = XDocument.Parse(content);
        XElement? First(XElement? parent, params string[] path)
        {
            var current = parent;
            foreach (var name in path)
            {
                current = current?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal));
            }

            return current;
        }

        var statement = document.Descendants().FirstOrDefault(static e => e.Name.LocalName == "Stmt") ?? throw new FormatException("No Stmt element.");
        var account = First(statement, "Acct");
        var currency = First(account, "Ccy")?.Value;
        var accountNumber = First(account, "Id", "IBAN")?.Value ?? First(account, "Id", "Othr", "Id")?.Value;
        var from = First(statement, "FrToDt", "FrDtTm") is { } f ? DayOf(f.Value) : null;
        var to = First(statement, "FrToDt", "ToDtTm") is { } t ? DayOf(t.Value) : null;
        decimal? opening = null;
        decimal? closing = null;
        foreach (var bal in statement.Elements().Where(static e => e.Name.LocalName == "Bal"))
        {
            var code = First(bal, "Tp", "CdOrPrtry", "Cd")?.Value;
            var amt = First(bal, "Amt");
            if (amt is null)
            {
                continue;
            }

            currency ??= amt.Attribute("Ccy")?.Value;
            var value = decimal.Parse(amt.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            var signed = First(bal, "CdtDbtInd")?.Value == "DBIT" ? -value : value;
            if (code is "OPBD" or "PRCD")
            {
                opening = signed;
            }
            else if (code is "CLBD" or "CLAV")
            {
                closing = signed;
            }
        }

        var lines = new List<ParsedLine>();
        foreach (var entry in statement.Elements().Where(static e => e.Name.LocalName == "Ntry"))
        {
            var amt = First(entry, "Amt") ?? throw new FormatException("An entry has no amount.");
            currency ??= amt.Attribute("Ccy")?.Value;
            var value = decimal.Parse(amt.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            var credit = First(entry, "CdtDbtInd")?.Value == "CRDT";
            var booking = DateOf(First(entry, "BookgDt")) ?? DateOf(First(entry, "ValDt")) ?? throw new FormatException("An entry has no date.");
            var details = First(entry, "NtryDtls", "TxDtls");
            var remittance = string.Join(" ", First(details, "RmtInf")?.Elements().Where(static e => e.Name.LocalName == "Ustrd").Select(static e => e.Value.Trim()) ?? []);
            var additional = First(entry, "AddtlNtryInf")?.Value.Trim();
            var description = remittance.Length > 0 ? remittance : additional ?? string.Empty;
            var party = credit ? First(details, "RltdPties", "Dbtr", "Nm")?.Value ?? First(details, "RltdPties", "Dbtr", "Pty", "Nm")?.Value : First(details, "RltdPties", "Cdtr", "Nm")?.Value ?? First(details, "RltdPties", "Cdtr", "Pty", "Nm")?.Value;
            var reference = First(details, "Refs", "EndToEndId")?.Value ?? First(details, "Refs", "ChqNb")?.Value ?? First(entry, "NtryRef")?.Value;
            if (reference is "NOTPROVIDED")
            {
                reference = null;
            }

            var code = First(entry, "BkTxCd", "Domn", "Fmly", "SubFmlyCd")?.Value ?? First(entry, "BkTxCd", "Prtry", "Cd")?.Value;
            var externalId = First(entry, "AcctSvcrRef")?.Value ?? First(details, "Refs", "AcctSvcrRef")?.Value ?? First(entry, "NtryRef")?.Value;
            lines.Add(new ParsedLine(booking, DateOf(First(entry, "ValDt")), credit ? value : -value, description, party?.Trim(), reference?.Trim(), code, externalId?.Trim()));
        }

        return new ParsedStatement(from ?? (lines.Count > 0 ? lines.Min(static l => l.BookingDate) : null), to ?? (lines.Count > 0 ? lines.Max(static l => l.BookingDate) : null), opening, closing, currency, accountNumber, lines);

        static DateOnly? DateOf(XElement? dateElement)
        {
            var text = dateElement?.Elements().FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return DayOf(text);
        }

        // The bank's calendar day, as written, whatever the offset of the time stamp.
        static DateOnly? DayOf(string text)
        {
            var trimmed = text.Trim();
            return trimmed.Length >= 10 && trimmed[4] == '-' ? DateOnly.ParseExact(trimmed[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        }
    }
}
