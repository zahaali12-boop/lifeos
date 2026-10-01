using FsCheck;
using FsCheck.Xunit;
using Quicker.Kernel.Quantities;

namespace Quicker.Kernel.Tests;

public class QuantityTests
{
    private static readonly UnitOfMeasure Piece = UnitOfMeasure.Of("PCS");
    private static readonly UnitOfMeasure Carton = UnitOfMeasure.Of("CTN");
    private static readonly UnitOfMeasure Dozen = UnitOfMeasure.Of("DZ");
    private static readonly UomConversion CartonToPiece = new(Carton, Piece, 24m, 1m);
    private static readonly UomConversion DozenToPiece = new(Dozen, Piece, 12m, 1m);

    [Fact]
    public void Carton_to_piece_to_dozen_is_exact()
    {
        var cartons = Quantity.Of(7m, Carton);
        var pieces = cartons.ConvertTo(Piece, CartonToPiece);
        pieces.Value.ShouldBe(168m);
        var dozens = pieces.ConvertTo(Dozen, DozenToPiece.Inverse());
        dozens.Value.ShouldBe(14m);
    }

    [Fact]
    public void Chained_conversions_stay_rational()
    {
        var cartonToDozen = CartonToPiece.Then(DozenToPiece.Inverse());
        cartonToDozen.Numerator.ShouldBe(24m);
        cartonToDozen.Denominator.ShouldBe(12m);
        Quantity.Of(1m, Carton).ConvertTo(Dozen, cartonToDozen).Value.ShouldBe(2m);
    }

    [Fact]
    public void Non_integer_results_are_detectable()
    {
        var pieces = Quantity.Of(5m, Piece);
        var dozens = pieces.ConvertTo(Dozen, DozenToPiece.Inverse());
        dozens.IsExactIn(Dozen).ShouldBeFalse();
        dozens.IsExactIn(UnitOfMeasure.Of("DZ", 6)).ShouldBeFalse();
        Quantity.Of(6m, Piece).ConvertTo(Dozen, DozenToPiece.Inverse()).IsExactIn(UnitOfMeasure.Of("DZ", 1)).ShouldBeTrue();
    }

    /// <summary>Hard scenario 9: thousands of random carton/dozen movements in base pieces never drift.</summary>
    [Property(MaxTest = 200)]
    public bool Thousands_of_conversions_never_drift(NonEmptyArray<(bool IsCarton, PositiveInt Qty, bool Inbound)> movements)
    {
        var onHandPieces = Quantity.Zero(Piece);
        var expectedPieces = 0L;
        foreach (var (isCarton, qty, inbound) in movements.Get)
        {
            var count = qty.Get % 500;
            var unit = isCarton ? Carton : Dozen;
            var conversion = isCarton ? CartonToPiece : DozenToPiece;
            var moved = Quantity.Of(count, unit).ConvertTo(Piece, conversion);
            var sign = inbound ? 1 : -1;
            onHandPieces = inbound ? onHandPieces + moved : onHandPieces - moved;
            expectedPieces += sign * count * (isCarton ? 24 : 12);
        }

        return onHandPieces.Value == expectedPieces && onHandPieces.IsExactIn(Piece);
    }

    [Fact]
    public void Mismatched_units_are_refused()
    {
        Should.Throw<UnitMismatchException>(() => Quantity.Of(1m, Piece) + Quantity.Of(1m, Carton));
        Should.Throw<UnitMismatchException>(() => Quantity.Of(1m, Piece).ConvertTo(Dozen, CartonToPiece));
    }
}
