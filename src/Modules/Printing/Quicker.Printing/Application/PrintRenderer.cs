using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Quicker.Printing.Application;

/// <summary>Settings of the print pipeline (section <c>Quicker:Print</c>).</summary>
public sealed class PrintOptions
{
    public const string SectionName = "Quicker:Print";

    /// <summary>The Chromium executable; found by itself when empty (QUICKER_CHROMIUM, the Playwright browsers folder, then chromium or google-chrome on the path).</summary>
    public string? ChromiumPath { get; set; }

    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>How long the evidence copy of a final document's first render is retained in the immutable store.</summary>
    public int EvidenceRetentionYears { get; set; } = 10;
}

/// <summary>Renders HTML to PDF; the one the worker runs is headless Chromium (ADR-0022).</summary>
public interface IPdfRenderer
{
    /// <summary>The executable the renderer would run, or null when none was found (renders then fail with <see cref="PdfRendererUnavailableException"/>).</summary>
    string? Executable { get; }

    /// <summary>Where the embedded fonts are on disk, as a URL a template's <c>@font-face</c> can read.</summary>
    string FontDirectoryUrl { get; }

    Task<byte[]> RenderPdfAsync(string html, CancellationToken cancellationToken = default);
}

public sealed class PdfRendererUnavailableException(string message) : Exception(message);

/// <summary>
/// Headless Chromium through its own command line: the HTML is written to a temporary folder and printed with
/// <c>--print-to-pdf</c>, with Chromium's text engine doing the Arabic shaping, bidi and fonts (ADR-0022). No driver or
/// browser protocol is needed, so the worker image only carries the browser.
/// </summary>
public sealed class ChromiumPdfRenderer : IPdfRenderer
{
    private static readonly string[] PathCandidates = ["chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "chrome"];

    private readonly PrintOptions _options;
    private readonly ILogger<ChromiumPdfRenderer> _logger;
    private readonly Lazy<string?> _executable;
    private readonly Lazy<string> _fontDirectory;

    public ChromiumPdfRenderer(PrintOptions options, ILogger<ChromiumPdfRenderer> logger)
    {
        _options = options;
        _logger = logger;
        _executable = new Lazy<string?>(Locate);
        _fontDirectory = new Lazy<string>(ExtractFonts);
    }

    public string? Executable => _executable.Value;

    public string FontDirectoryUrl => new Uri(_fontDirectory.Value + Path.DirectorySeparatorChar).AbsoluteUri;

    public async Task<byte[]> RenderPdfAsync(string html, CancellationToken cancellationToken = default)
    {
        var executable = Executable ?? throw new PdfRendererUnavailableException("No Chromium was found: set Quicker:Print:ChromiumPath or QUICKER_CHROMIUM, install the Playwright chromium, or put chromium on the path.");
        var work = Path.Combine(Path.GetTempPath(), "quicker-print", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "document.html");
            var output = Path.Combine(work, "document.pdf");
            await File.WriteAllTextAsync(input, html, cancellationToken);
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = work,
            };
            foreach (var argument in new[]
            {
                "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--disable-extensions", "--no-first-run", "--hide-scrollbars",
                "--allow-file-access-from-files", "--run-all-compositor-stages-before-draw", "--virtual-time-budget=10000", "--no-pdf-header-footer",
                "--print-to-pdf=" + output, new Uri(input).AbsoluteUri,
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Chromium did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException($"Chromium did not finish within {_options.TimeoutSeconds} s.");
            }

            await Task.WhenAll(errors, standardOutput);
            if (!File.Exists(output))
            {
                var tail = string.Join('\n', (await errors).Split('\n').Where(static l => !l.Contains("dbus", StringComparison.OrdinalIgnoreCase)).TakeLast(8));
                throw new InvalidOperationException($"Chromium exited with {process.ExitCode} and wrote no PDF. {tail}");
            }

            return await File.ReadAllBytesAsync(output, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A profile file still held open by a dying Chromium; the temp folder is swept by the OS.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private string? Locate()
    {
        foreach (var configured in new[] { _options.ChromiumPath, Environment.GetEnvironmentVariable("QUICKER_CHROMIUM") })
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured))
                {
                    return configured;
                }

                _logger.LogWarning("Configured Chromium {Path} does not exist", configured);
            }
        }

        var browserRoots = new List<string>();
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } playwright)
        {
            browserRoots.Add(playwright);
        }

        browserRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright"));
        browserRoots.Add("/opt/pw-browsers");
        foreach (var root in browserRoots.Where(Directory.Exists))
        {
            var found = Directory.EnumerateDirectories(root, "chromium-*").OrderByDescending(static d => d, StringComparer.Ordinal)
                .SelectMany(static d => Directory.EnumerateDirectories(d, "chrome-linux*"))
                .Select(static d => Path.Combine(d, "chrome"))
                .FirstOrDefault(File.Exists);
            if (found is not null)
            {
                return found;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in PathCandidates)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        _logger.LogWarning("No Chromium found for PDF rendering; HTML previews still work, PDF renders will fail until one is installed");
        return null;
    }

    private static string ExtractFonts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quicker-print", "fonts");
        Directory.CreateDirectory(directory);
        foreach (var file in ShippedTemplates.FontFiles)
        {
            var target = Path.Combine(directory, file);
            if (!File.Exists(target))
            {
                var temporary = target + "." + Guid.NewGuid().ToString("N")[..8];
                File.WriteAllBytes(temporary, ShippedTemplates.Font(file));
                File.Move(temporary, target, overwrite: true);
            }
        }

        return directory;
    }
}
