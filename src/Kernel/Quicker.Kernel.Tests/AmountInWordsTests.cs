using Quicker.Kernel.Amounts;
using Quicker.Kernel.Text;

namespace Quicker.Kernel.Tests;

public class AmountInWordsTests
{
    [Theory]
    [InlineData(0, "USD", "Zero US dollars and zero cents only")]
    [InlineData(1.01, "USD", "One US dollar and one cent only")]
    [InlineData(21.5, "USD", "Twenty-one US dollars and fifty cents only")]
    [InlineData(1_234_567.89, "USD", "One million two hundred and thirty-four thousand five hundred and sixty-seven US dollars and eighty-nine cents only")]
    [InlineData(100, "EUR", "One hundred euros and zero cents only")]
    [InlineData(1250.750, "KWD", "One thousand two hundred and fifty Kuwaiti dinars and seven hundred and fifty fils only")]
    [InlineData(5000, "JPY", "Five thousand yen only")]
    public void English(double raw, string code, string expected)
    {
        var currency = code switch { "USD" => Currency.USD, "EUR" => Currency.EUR, "KWD" => Currency.KWD, "JPY" => Currency.JPY, _ => throw new InvalidOperationException() };
        AmountInWords.Spell(Money.Of((decimal)raw, currency), "en").ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, "دينار عراقي واحد وصفر فلس فقط لا غير")]
    [InlineData(2, "ديناران عراقيان وصفر فلس فقط لا غير")]
    [InlineData(3, "ثلاثة دنانير عراقية وصفر فلس فقط لا غير")]
    [InlineData(10, "عشرة دنانير عراقية وصفر فلس فقط لا غير")]
    [InlineData(11, "أحد عشر دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(25, "خمسة وعشرون دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(100, "مائة دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(200, "مائتان دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(1000, "ألف دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(2000, "ألفان دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(3000, "ثلاثة آلاف دينار عراقي وصفر فلس فقط لا غير")]
    [InlineData(1_250_000, "مليون ومائتان وخمسون ألف دينار عراقي وصفر فلس فقط لا غير")]
    public void Arabic_dinar_agreement(double raw, string expected)
    {
        AmountInWords.Spell(Money.Of((decimal)raw, Currency.IQD), "ar").ShouldBe(expected);
    }

    [Fact]
    public void Arabic_feminine_minor_unit_takes_feminine_numbers()
    {
        // 3.03 SAR: halala is feminine → "ثلاث هللات"; riyal is masculine → "ثلاثة ريالات"
        AmountInWords.Spell(Money.Of(3.03m, Currency.SAR), "ar").ShouldBe("ثلاثة ريالات سعودية وثلاث هللات فقط لا غير");
        AmountInWords.Spell(Money.Of(0.01m, Currency.SAR), "ar").ShouldBe("صفر ريال سعودي وهللة واحدة فقط لا غير");
        AmountInWords.Spell(Money.Of(0.02m, Currency.SAR), "ar").ShouldBe("صفر ريال سعودي وهللتان فقط لا غير");
    }

    [Fact]
    public void Unrounded_amounts_are_refused()
    {
        Should.Throw<ArgumentException>(() => AmountInWords.Spell(Money.Of(1.005m, Currency.USD), "en"));
    }

    [Fact]
    public void Eastern_arabic_digits_are_a_display_choice()
    {
        AmountInWords.FormatDigits(1234567.5m, 2, easternArabicDigits: true).ShouldBe("١,٢٣٤,٥٦٧.٥٠");
        AmountInWords.FormatDigits(1234567.5m, 2, easternArabicDigits: false).ShouldBe("1,234,567.50");
    }
}
