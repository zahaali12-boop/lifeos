using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Printing.Application;
using UglyToad.PdfPig;

namespace Quicker.Printing.Tests;

/// <summary>
/// The visual regression of roadmap 5.9's acceptance: the shipped Arabic invoice rendered from its sample data must
/// lay out as it did when the baseline was taken. The PDF's words and their positions (rounded to half a point) are
/// compared with the committed baseline, so a template, stylesheet, font or Chromium change that moves the Arabic
/// text, breaks the bidi order or overflows a column fails here. The fonts are embedded with the module, so the layout
/// is the same on every machine running the same Chromium build. Set QUICKER_PRINT_UPDATE_BASELINES=1 to rewrite the
/// baselines after a deliberate change (the test then writes them and still passes).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LayoutRegressionTests(ApiHostFixture host)
{
    private sealed record Word(string Text, double X, double Y);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Theory]
    [InlineData("SALES-INVOICE", "ar", "sales_invoice.ar")]
    [InlineData("SALES-INVOICE", "bilingual", "sales_invoice.bilingual")]
    [InlineData("CUSTOMER-STATEMENT", "ar", "customer_statement.ar")]
    public async Task The_shipped_template_rendered_from_its_sample_lays_out_as_the_baseline(string code, string languageMode, string baseline)
    {
        var ws = await host.Api.SignupAsync();
        var owner = host.Api.ClientFor(ws.AccessToken);
        var templates = await owner.GetOkAsync("/api/v1/print/templates");
        var template = templates.EnumerateArray().Single(t => t.GetProperty("code").GetString() == code);
        var templateId = template.GetProperty("id").GetGuid();
        if (template.GetProperty("languageMode").GetString() != languageMode)
        {
            await owner.PutAsync($"/api/v1/print/templates/{templateId}", new { languageMode });
        }

        // The same HTML the designer previews, rendered through the worker's own renderer with the embedded fonts.
        byte[] pdf;
        await using (var scope = host.Api.Services.CreateAsyncScope())
        {
            var renderer = scope.ServiceProvider.GetRequiredService<IPdfRenderer>();
            renderer.Executable.ShouldNotBeNull("the print pipeline tests need a Chromium (QUICKER_CHROMIUM, the Playwright browsers folder or chromium on the path)");
            var html = await owner.GetTextAsync($"/api/v1/print/templates/{templateId}/preview");
            var withFonts = html.Replace("url(\"DejaVuSans", $"url(\"{renderer.FontDirectoryUrl}DejaVuSans", StringComparison.Ordinal);
            pdf = await renderer.RenderPdfAsync(withFonts, TestContext.Current.CancellationToken);
        }

        var actual = Words(pdf);
        actual.Count.ShouldBeGreaterThan(40, "the sample renders a full page of words");
        if (languageMode == "ar")
        {
            actual.Count(static w => w.Text.Any(static c => c >= '؀' && c <= 'ۿ')).ShouldBeGreaterThan(20, "the Arabic template is set in Arabic");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Baselines", baseline + ".json");
        var sourcePath = Path.Combine(FindSourceRoot(), "src", "Modules", "Printing", "Quicker.Printing.Tests", "Baselines", baseline + ".json");
        if (Environment.GetEnvironmentVariable("QUICKER_PRINT_UPDATE_BASELINES") == "1" || !File.Exists(path))
        {
            var json = JsonSerializer.Serialize(actual, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(sourcePath, json, TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
            File.Exists(path).ShouldBeTrue();
            return;
        }

        var expected = JsonSerializer.Deserialize<List<Word>>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), Json)!;
        var differences = new List<string>();
        if (expected.Count != actual.Count)
        {
            differences.Add($"word count {expected.Count} → {actual.Count}");
        }

        foreach (var (e, a) in expected.Zip(actual))
        {
            if (!string.Equals(e.Text, a.Text, StringComparison.Ordinal))
            {
                differences.Add($"'{e.Text}' → '{a.Text}' at ({a.X}, {a.Y})");
            }
            else if (Math.Abs(e.X - a.X) > 1.5 || Math.Abs(e.Y - a.Y) > 1.5)
            {
                differences.Add($"'{e.Text}' moved from ({e.X}, {e.Y}) to ({a.X}, {a.Y})");
            }

            if (differences.Count >= 12)
            {
                break;
            }
        }

        differences.ShouldBeEmpty($"the {baseline} layout changed; set QUICKER_PRINT_UPDATE_BASELINES=1 and rerun if the change is deliberate:\n  " + string.Join("\n  ", differences));
    }

    private static List<Word> Words(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var words = new List<Word>();
        foreach (var page in document.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                var text = word.Text.Trim();
                if (text.Length == 0 || text.StartsWith("Printed", StringComparison.Ordinal) || text.StartsWith("طُبع", StringComparison.Ordinal) || LooksLikeTimestamp(text))
                {
                    continue;
                }

                words.Add(new Word(text, Round(word.BoundingBox.Left), Round(page.Height - word.BoundingBox.Top)));
            }
        }

        return words;
    }

    /// <summary>The "generated at" footer carries the clock; its date and time are left out of the comparison.</summary>
    private static bool LooksLikeTimestamp(string text) =>
        text.Length >= 5 && (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && text.StartsWith("20", StringComparison.Ordinal) && text != "2026-09-22"
            || TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || text == "UTC");

    private static double Round(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quicker.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
