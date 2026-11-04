using Quicker.Numbering.Application;

namespace Quicker.Numbering.Tests;

/// <summary>Template syntax and rendering, the part of ADR-0016 that must match the database's sequence rendering exactly.</summary>
public sealed class TemplateTests
{
    [Theory]
    [InlineData("INV-{seq}")]
    [InlineData("INV-{company}-{branch}-{yy}-{seq:6}")]
    [InlineData("{fy}/{mm}/{seq:18}")]
    [InlineData("JV{yyyy}{seq:4}")]
    public void Valid_templates_pass(string template) => Templates.Validate(template).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("", "series.template_required")]
    [InlineData("INV", "series.template_sequence_required")]
    [InlineData("INV-{seq}-{seq}", "series.template_sequence_required")]
    [InlineData("INV-{warehouse}-{seq}", "series.template_token_unknown")]
    [InlineData("INV-{seq", "series.template_invalid")]
    [InlineData("INV-seq}", "series.template_invalid")]
    [InlineData("INV-{seq:0}", "series.template_invalid")]
    [InlineData("INV-{seq:19}", "series.template_invalid")]
    [InlineData("INV-{yy:2}-{seq}", "series.template_invalid")]
    public void Invalid_templates_are_rejected_with_a_stable_code(string template, string code) => Templates.Validate(template).Error!.Code.ShouldBe(code);

    [Fact]
    public void Rendering_fills_every_token_but_the_sequence_and_the_sequence_pads_without_truncating()
    {
        var context = new TemplateContext("IQCO", "BGW", new DateOnly(2026, 9, 22), "FY2026/27");
        var rendered = Templates.Render("{company}/{branch}/{yy}/{yyyy}/{mm}/{fy}/{seq:4}", context);
        rendered.IsSuccess.ShouldBeTrue();
        rendered.Value.ShouldBe("IQCO/BGW/26/2026/09/2026/27/{seq:4}");
        Templates.RenderSequence(rendered.Value, 7).ShouldBe("IQCO/BGW/26/2026/09/2026/27/0007");
        Templates.RenderSequence(rendered.Value, 123456).ShouldBe("IQCO/BGW/26/2026/09/2026/27/123456");
        Templates.RenderSequence("N-{seq}", 42).ShouldBe("N-42");
        Templates.Needs("INV-{branch}-{seq}", "branch").ShouldBeTrue();
        Templates.Needs("INV-{seq}", "fy").ShouldBeFalse();
    }

    [Fact]
    public void Rendering_reports_missing_context()
    {
        Templates.Render("INV-{branch}-{seq}", new TemplateContext("IQCO", null, new DateOnly(2026, 9, 22), "FY2026")).Error!.Code.ShouldBe("series.branch_required");
        Templates.Render("INV-{fy}-{seq}", new TemplateContext("IQCO", null, new DateOnly(2026, 9, 22), null)).Error!.Code.ShouldBe("series.fiscal_year_required");
    }
}
