namespace Quicker.Reporting.Contracts;

/// <summary>
/// The home page's numbers for one company on one day (founder programme item 1): what was sold, what is owed and
/// due, the money in the bank and the cash boxes, and the documents waiting for the next step. A section is null when
/// the member may not read what it counts. Amounts are in the company's functional currency and come straight from
/// the modules' tables (posted invoices, open items, bank transactions), so they agree with the screens they open.
/// </summary>
public sealed record HomeDashboard(
    Guid CompanyId,
    string FunctionalCurrency,
    DateOnly AsOf,
    HomeSales? Sales,
    HomeReceivables? Receivables,
    HomePayables? Payables,
    HomeCash? Cash,
    HomePipeline? Pipeline);

/// <summary>Net sales (invoices less credit notes, before tax) of the day, the month so far and the same days of the month before, with a day-by-day series for the last thirty days.</summary>
public sealed record HomeSales(decimal Today, decimal MonthToDate, decimal PreviousMonthToDate, int InvoicesToday, int DraftInvoices, IReadOnlyList<HomeDay> Days);

public sealed record HomeDay(DateOnly Day, decimal Amount);

/// <summary>What customers owe as the open items stand now: past due at the date, due within the seven days after it, and in all.</summary>
public sealed record HomeReceivables(decimal Overdue, int OverdueCustomers, decimal DueSoon, decimal Total, int OpenItems);

/// <summary>What is owed to suppliers as the open items stand now: past due at the date, due within the seven days after it, and in all.</summary>
public sealed record HomePayables(decimal Overdue, int OverdueSuppliers, decimal DueSoon, decimal Total, int OpenItems);

/// <summary>The balance of every active bank account and cash box at the date, and what came in and went out this month.</summary>
public sealed record HomeCash(decimal Total, decimal ReceivedMonthToDate, decimal PaidMonthToDate, IReadOnlyList<HomeCashAccount> Accounts);

public sealed record HomeCashAccount(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Kind, string Currency, decimal BalanceTc, decimal BalanceFc);

/// <summary>Documents waiting for their next step; a count is null when the member may not read that document type.</summary>
public sealed record HomePipeline(int? OpenQuotations, int? OrdersToShip, int? DraftShipments, int? OpenReturns);
