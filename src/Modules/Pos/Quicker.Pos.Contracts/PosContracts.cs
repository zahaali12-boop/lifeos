namespace Quicker.Pos.Contracts;

/// <summary>The document types of the point of sale: an order synced from a till (a sale or a refund) and a cashier's session on a register.</summary>
public static class PosDocumentTypes
{
    public const string Order = "pos_order";
    public const string Session = "pos_session";
}

/// <summary>What a till's order is: a sale, or a refund of one.</summary>
public static class PosOrderKinds
{
    public const string Sale = "sale";
    public const string Refund = "refund";

    public static readonly IReadOnlyList<string> All = [Sale, Refund];
}

/// <summary>How a till's order was paid: cash into the register's cash box, a card into the acquirer's bank account, or on the customer's account.</summary>
public static class PosPaymentMethods
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string Credit = "credit";

    public static readonly IReadOnlyList<string> All = [Cash, Card, Credit];
}
