using Quicker.Banking.Application;

namespace Quicker.Banking.Tests;

/// <summary>
/// The four statement formats (roadmap 6.2) read from sample files as banks export them: the same three movements
/// come out of a CSV with debit and credit columns, an OFX 1.x file, an MT940 message and a CAMT.053 document, with
/// the period, the balances and the references each format carries.
/// </summary>
public sealed class StatementParserTests
{
    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    private static void ShouldBeTheOctoberMovements(ParsedStatement statement)
    {
        statement.Lines.Count.ShouldBe(3);
        statement.Lines.Select(static l => (l.BookingDate, l.Amount)).ShouldBe([(new DateOnly(2026, 10, 3), 1000m), (new DateOnly(2026, 10, 5), -600m), (new DateOnly(2026, 10, 15), -12.50m)]);
        statement.Lines[0].Description.ShouldContain("INV-1");
        statement.Lines[2].Description.ShouldContain("MONTHLY FEE");
    }

    [Fact]
    public void A_csv_with_debit_and_credit_columns_reads_through_the_mapping_and_drops_lines_without_money()
    {
        var parsed = StatementParsers.Parse(StatementFormats.Csv, Sample("rafidain.csv"), new StatementMappingSettings(DateFormat: "dd/MM/yyyy"));
        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        var statement = parsed.Value;
        ShouldBeTheOctoberMovements(statement);
        statement.Lines[0].Reference.ShouldBe("TRF-5501");
        (statement.From, statement.To).ShouldBe((new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 15)));
        statement.ClosingBalance.ShouldBeNull();

        // Columns named by position when the export has no header, and the wrong date format refused with the line.
        var noHeader = StatementParsers.Parse(StatementFormats.Csv, "03/10/2026;Transfer;1000,00\n05/10/2026;Payment;-600,00\n", new StatementMappingSettings(Delimiter: ";", HasHeader: false, DateColumn: "1", DateFormat: "dd/MM/yyyy", AmountColumn: "3", DescriptionColumn: "2", DecimalSeparator: ",", ThousandsSeparator: "."));
        noHeader.IsSuccess.ShouldBeTrue(noHeader.Error?.Message);
        noHeader.Value.Lines.Select(static l => l.Amount).ShouldBe([1000m, -600m]);
        // The mapping's format is tried first and the common ones after it, so a day-first export still reads under the default mapping; a date in words does not.
        StatementParsers.Parse(StatementFormats.Csv, Sample("rafidain.csv"), new StatementMappingSettings()).Value.Lines[0].BookingDate.ShouldBe(new DateOnly(2026, 10, 3));
        StatementParsers.Parse(StatementFormats.Csv, "Date,Amount\nthird of October,10\n", new StatementMappingSettings()).Error!.Code.ShouldBe("statement.date_unreadable");
        StatementParsers.Parse(StatementFormats.Csv, "When,What\n2026-10-03,Transfer\n", new StatementMappingSettings()).Error!.Code.ShouldBe("statement.date_column_missing");
    }

    [Fact]
    public void An_ofx_file_reads_its_transactions_balance_currency_and_account()
    {
        var parsed = StatementParsers.Parse(StatementFormats.Ofx, Sample("sample.ofx"), null);
        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        var statement = parsed.Value;
        ShouldBeTheOctoberMovements(statement);
        (statement.Currency, statement.AccountNumber, statement.ClosingBalance).ShouldBe(("USD", "0011223344", 387.50m));
        (statement.From, statement.To).ShouldBe((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        statement.Lines[1].Reference.ShouldBe("1001");
        statement.Lines[1].ExternalId.ShouldBe("2026100500002");
        statement.Lines[0].Counterparty.ShouldBe("AL-NOOR MARKETS");
    }

    [Fact]
    public void An_mt940_message_reads_its_balances_and_lines_with_their_86_details()
    {
        var parsed = StatementParsers.Parse(StatementFormats.Mt940, Sample("sample.mt940"), null);
        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        var statement = parsed.Value;
        ShouldBeTheOctoberMovements(statement);
        (statement.Currency, statement.AccountNumber, statement.OpeningBalance, statement.ClosingBalance).ShouldBe(("USD", "RAFIDAIN/0011223344", 0m, 387.50m));
        (statement.From, statement.To).ShouldBe((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        statement.Lines[0].ExternalId.ShouldBe("TRF5501");
        statement.Lines[0].Reference.ShouldBeNull();
        statement.Lines[1].Reference.ShouldBe("1001");
        statement.Lines[1].BankCode.ShouldBe("NCHK");
    }

    [Fact]
    public void A_camt053_document_reads_its_balances_entries_parties_and_references()
    {
        var parsed = StatementParsers.Parse(StatementFormats.Camt053, Sample("sample.camt053.xml"), null);
        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        var statement = parsed.Value;
        ShouldBeTheOctoberMovements(statement);
        (statement.Currency, statement.AccountNumber, statement.OpeningBalance, statement.ClosingBalance).ShouldBe(("USD", "IQ98RAFI0011223344000000", 0m, 387.50m));
        (statement.From, statement.To).ShouldBe((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        statement.Lines[0].Reference.ShouldBe("TRF-5501");
        statement.Lines[0].Counterparty.ShouldBe("Al-Noor Markets");
        statement.Lines[1].Reference.ShouldBe("1001");
        statement.Lines[2].BankCode.ShouldBe("CHRG");
    }

    [Fact]
    public void The_format_is_guessed_from_the_file_and_an_unknown_one_is_refused()
    {
        StatementFormats.Guess("october.ofx", Sample("sample.ofx")[..200]).ShouldBe("ofx");
        StatementFormats.Guess("october.txt", Sample("sample.mt940")[..60]).ShouldBe("mt940");
        StatementFormats.Guess("october.xml", Sample("sample.camt053.xml")[..300]).ShouldBe("camt053");
        StatementFormats.Guess("october.csv", "Date,Amount").ShouldBe("csv");
        StatementFormats.Guess("october.pdf", "%PDF").ShouldBeNull();
        StatementParsers.Parse("pdf", "x", null).Error!.Code.ShouldBe("statement.format_unknown");
        StatementParsers.Parse(StatementFormats.Camt053, "<not xml", null).Error!.Code.ShouldBe("statement.unreadable");
    }
}
