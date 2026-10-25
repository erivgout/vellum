using System.Diagnostics;
using System.Text;
using Vellum.Infrastructure;

namespace Vellum.Modules.Pdf;

public enum SourceKind { Pdf, Image, Text, Office, Unsupported }

/// <summary>Configuration under <c>Vellum:Pdf</c>.</summary>
public sealed class PdfOptions
{
    /// <summary>Explicit path to soffice(.exe/.com). "none" disables Office conversion. Empty = auto-detect.</summary>
    public string? SofficePath { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxParallel { get; init; } = 2;
    public long MaxOfficeBytes { get; init; } = 100L * 1024 * 1024;
    public long MaxPdfBytes { get; init; } = 200L * 1024 * 1024;
    public long MaxImageBytes { get; init; } = 40L * 1024 * 1024;
    public long MaxTextBytes { get; init; } = 5L * 1024 * 1024;
    /// <summary>Attempts before a conversion is marked failed for good (NFR-025).</summary>
    public int MaxAttempts { get; init; } = 5;
    /// <summary>First retry delay; doubles each attempt (30 s, 1 min, 2 min, 4 min…), capped at 1 hour.</summary>
    public int BackoffSeconds { get; init; } = 30;
    /// <summary>Background worker poll interval.</summary>
    public int PollSeconds { get; init; } = 3;
    public bool Worker { get; init; } = true;

    public static PdfOptions From(IConfiguration config)
    {
        var s = config.GetSection("Vellum:Pdf");
        return new PdfOptions
        {
            SofficePath = s["SofficePath"],
            TimeoutSeconds = s.GetValue("TimeoutSeconds", 120),
            MaxParallel = Math.Clamp(s.GetValue("MaxParallel", 2), 1, 8),
            MaxOfficeBytes = s.GetValue("MaxOfficeMegabytes", 100L) * 1024 * 1024,
            MaxPdfBytes = s.GetValue("MaxPdfMegabytes", 200L) * 1024 * 1024,
            MaxImageBytes = s.GetValue("MaxImageMegabytes", 40L) * 1024 * 1024,
            MaxTextBytes = s.GetValue("MaxTextMegabytes", 5L) * 1024 * 1024,
            MaxAttempts = Math.Clamp(s.GetValue("MaxAttempts", 5), 1, 20),
            BackoffSeconds = Math.Clamp(s.GetValue("BackoffSeconds", 30), 1, 3600),
            PollSeconds = Math.Clamp(s.GetValue("PollSeconds", 3), 1, 300),
            Worker = s.GetValue("Worker", true) && !config.GetValue("Vellum:DisableScheduler", false),
        };
    }
}

public sealed record ConversionOutput(byte[] Pdf, string Converter, int Pages, int DurationMs);

/// <summary>File types and the rendition naming rule of PLT-804.</summary>
public static class SourceTypes
{
    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".ott", ".fodt", ".wpd",
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".ods", ".ots", ".fods", ".csv",
        ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odp", ".otp", ".fodp",
        ".odg", ".vsd", ".vsdx", ".pub", ".gif", ".tif", ".tiff", ".webp", ".svg",
    };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".log", ".md", ".text" };

    public static SourceKind Classify(string fileName, string? contentType, ReadOnlySpan<byte> head)
    {
        var ext = Path.GetExtension(fileName);
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase) || contentType == "application/pdf") return SourceKind.Pdf;
        if (Images.Contains(ext) || contentType is "image/jpeg" or "image/png" or "image/bmp") return SourceKind.Image;
        if (Text.Contains(ext) || contentType == "text/plain") return SourceKind.Text;
        if (Office.Contains(ext)) return SourceKind.Office;
        if (PdfKit.LooksLikePdf(head)) return SourceKind.Pdf;
        return SourceKind.Unsupported;
    }

    /// <summary>PLT-804: <c>Report.docx</c> → <c>Report_docx.pdf</c>; no extension → <c>.pdf</c> appended; <c>.pdf</c> unchanged.</summary>
    public static string RenditionName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name);
        if (ext.Length == 0) return name + ".pdf";
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) return name;
        return Path.GetFileNameWithoutExtension(name) + "_" + ext.TrimStart('.') + ".pdf";
    }

    public static string Describe(SourceKind k) => k switch
    {
        SourceKind.Pdf => "PDF",
        SourceKind.Image => "image",
        SourceKind.Text => "text file",
        SourceKind.Office => "Office document",
        _ => "file",
    };
}

/// <summary>
/// Converts an original attachment to a base PDF: PDFs pass through (validated), JPEG/PNG/BMP images and plain text are
/// laid out with PDFsharp/MigraDoc, Office and ODF documents go through LibreOffice headless. Size limits are enforced.
/// Watermarks are applied afterwards by the queue, so a base PDF can be cached by source checksum (PLT-804).
/// </summary>
[ScopedService]
public sealed class PdfConverter(IConfiguration config, ILogger<PdfConverter> log)
{
    private PdfOptions Options { get; } = PdfOptions.From(config);

    public OfficeConverter Office => new(Options, log);

    public async Task<ConversionOutput> ConvertAsync(byte[] bytes, string fileName, string? contentType, CancellationToken ct = default)
    {
        var kind = SourceTypes.Classify(fileName, contentType, bytes);
        var sw = Stopwatch.StartNew();
        void Limit(long max)
        {
            if (bytes.LongLength > max)
                throw new PdfProcessingException($"{Path.GetFileName(fileName)} is {bytes.LongLength / 1048576.0:0.#} MB; {SourceTypes.Describe(kind)}s up to {max / 1048576} MB can be converted to PDF.");
        }
        switch (kind)
        {
            case SourceKind.Pdf:
            {
                Limit(Options.MaxPdfBytes);
                var facts = PdfKit.Inspect(bytes);
                if (facts.Encrypted)
                    throw new PdfProcessingException("The PDF is password-protected or encrypted, so a controlled rendition can't be made from it. Upload an unprotected copy.");
                return new ConversionOutput(bytes, "passthrough", facts.Pages, (int)sw.ElapsedMilliseconds);
            }
            case SourceKind.Image:
            {
                Limit(Options.MaxImageBytes);
                var pdf = PdfKit.FromImage(bytes, Path.GetFileName(fileName));
                return new ConversionOutput(pdf, "image", 1, (int)sw.ElapsedMilliseconds);
            }
            case SourceKind.Text:
            {
                Limit(Options.MaxTextBytes);
                var pdf = PdfKit.FromText(PdfKit.DecodeText(bytes), Path.GetFileName(fileName));
                return new ConversionOutput(pdf, "text", PdfKit.PageCount(pdf), (int)sw.ElapsedMilliseconds);
            }
            case SourceKind.Office:
            {
                Limit(Options.MaxOfficeBytes);
                var pdf = await Office.ConvertAsync(bytes, fileName, ct);
                return new ConversionOutput(pdf, "libreoffice", PdfKit.PageCount(pdf), (int)sw.ElapsedMilliseconds);
            }
            default:
                throw new PdfProcessingException($"{Path.GetFileName(fileName)} can't be converted to PDF: {Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant() switch { "" => "files without an extension aren't", var e => e + " files aren't" }} supported. Upload a PDF, an Office or OpenDocument file, an image or plain text.");
        }
    }

    /// <summary>True when the file can be converted without LibreOffice (fast enough to run inline, NFR-005).</summary>
    public static bool IsFast(string fileName, string? contentType, ReadOnlySpan<byte> head) =>
        SourceTypes.Classify(fileName, contentType, head) is SourceKind.Pdf or SourceKind.Image or SourceKind.Text;
}

/// <summary>
/// LibreOffice headless (<c>soffice --headless --convert-to pdf</c>). Each conversion gets its own profile directory
/// (<c>-env:UserInstallation</c>) so parallel conversions never collide; the process tree is killed on timeout and every
/// temporary file is removed. When LibreOffice isn't installed the conversion fails with a clear, retryable message.
/// </summary>
public sealed class OfficeConverter(PdfOptions options, ILogger log)
{
    private static SemaphoreSlim? _gate;
    private static readonly Lock GateLock = new();

    private static readonly string[] Candidates = OperatingSystem.IsWindows()
        ? [@"C:\Program Files\LibreOffice\program\soffice.exe", @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"]
        : OperatingSystem.IsMacOS()
            ? ["/Applications/LibreOffice.app/Contents/MacOS/soffice"]
            : ["/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/opt/libreoffice/program/soffice", "/snap/bin/libreoffice"];

    /// <summary>The soffice executable to run, or null when LibreOffice isn't available (checked on every call: it may be installed later).</summary>
    public string? Locate()
    {
        var configured = options.SofficePath?.Trim();
        if (string.Equals(configured, "none", StringComparison.OrdinalIgnoreCase)) return null;
        var path = !string.IsNullOrEmpty(configured) ? configured : Candidates.FirstOrDefault(File.Exists);
        if (path == null || !File.Exists(path)) return null;
        // On Windows, soffice.com is the console launcher: it waits for the conversion and returns its exit code.
        if (OperatingSystem.IsWindows() && path.EndsWith("soffice.exe", StringComparison.OrdinalIgnoreCase))
        {
            var com = Path.ChangeExtension(path, ".com");
            if (File.Exists(com)) return com;
        }
        return path;
    }

    public bool Available => Locate() != null;

    public string MissingMessage => string.IsNullOrWhiteSpace(options.SofficePath) || string.Equals(options.SofficePath, "none", StringComparison.OrdinalIgnoreCase)
        ? "Office files can't be converted to PDF on this server: LibreOffice isn't installed. Install LibreOffice, or set Vellum:Pdf:SofficePath, then retry the conversion."
        : $"Office files can't be converted to PDF on this server: LibreOffice wasn't found at {options.SofficePath} (Vellum:Pdf:SofficePath). Fix the path, then retry the conversion.";

    private SemaphoreSlim Gate
    {
        get
        {
            lock (GateLock) return _gate ??= new SemaphoreSlim(options.MaxParallel, options.MaxParallel);
        }
    }

    public async Task<byte[]> ConvertAsync(byte[] input, string fileName, CancellationToken ct = default)
    {
        var soffice = Locate() ?? throw new PdfProcessingException(MissingMessage, permanent: false);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext.Length is < 2 or > 8 || ext.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) ext = ".bin";
        var work = Path.Combine(Path.GetTempPath(), "vellum-pdf", Guid.NewGuid().ToString("N"));
        var inDir = Path.Combine(work, "in");
        var outDir = Path.Combine(work, "out");
        var profile = Path.Combine(work, "profile");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);
        var source = Path.Combine(inDir, "source" + ext);
        await File.WriteAllBytesAsync(source, input, ct);

        await Gate.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = work,
            };
            psi.ArgumentList.Add("-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri);
            foreach (var a in new[] { "--headless", "--invisible", "--nologo", "--nodefault", "--nofirststartwizard", "--nolockcheck", "--norestore", "--convert-to", "pdf", "--outdir" })
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(source);

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            try { proc.Start(); }
            catch (Exception ex) { throw new PdfProcessingException($"LibreOffice couldn't be started ({ex.Message}).", permanent: false, ex); }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(proc);
                if (ct.IsCancellationRequested) throw;
                throw new PdfProcessingException($"The conversion timed out after {options.TimeoutSeconds} seconds and was stopped. Large or complex files may need a longer timeout (Vellum:Pdf:TimeoutSeconds).", permanent: false);
            }

            var output = Path.Combine(outDir, "source.pdf");
            if (proc.ExitCode != 0 || !File.Exists(output))
            {
                var detail = (stderr.ToString() + " " + stdout.ToString()).Trim();
                log.LogWarning("LibreOffice conversion of {File} failed with exit code {Code}: {Detail}", fileName, proc.ExitCode, detail);
                var corrupt = detail.Contains("source file could not be loaded", StringComparison.OrdinalIgnoreCase) || detail.Contains("Error: ", StringComparison.Ordinal);
                throw new PdfProcessingException(corrupt
                    ? $"LibreOffice couldn't open {Path.GetFileName(fileName)}. The file may be corrupt, password-protected or not really a {ext.TrimStart('.').ToUpperInvariant()} file."
                    : $"LibreOffice didn't produce a PDF (exit code {proc.ExitCode}).", permanent: corrupt);
            }
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            Gate.Release();
            await CleanupAsync(work);
        }
    }

    private void Kill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception ex) { log.LogWarning(ex, "Couldn't stop a timed-out LibreOffice process"); }
    }

    private static async Task CleanupAsync(string dir)
    {
        // LibreOffice can hold its profile for a moment after exiting; retry before giving up.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { await Task.Delay(200 * (i + 1)); }
            catch (UnauthorizedAccessException) { await Task.Delay(200 * (i + 1)); }
        }
    }
}
