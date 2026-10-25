using System.Globalization;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Vellum.Modules.Pdf;

/// <summary>A conversion or PDF-processing failure with a message fit to show users (PLT-807).</summary>
public sealed class PdfProcessingException(string message, bool permanent = true, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when retrying can't help (corrupt, protected, unsupported or too large).</summary>
    public bool Permanent { get; } = permanent;
}

/// <summary>
/// Font resolver for PDFsharp / MigraDoc: the embedded Inter family (SIL OFL), so generated PDFs look the same on every
/// server and need no system fonts. "Inter SemiBold" is a separate family because MigraDoc only knows bold or not.
/// </summary>
public sealed class PdfFonts : IFontResolver
{
    public const string Family = "Inter";
    public const string SemiBold = "Inter SemiBold";

    private static readonly Lazy<bool> Installed = new(() =>
    {
        GlobalFontSettings.FontResolver = new PdfFonts();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Dictionary<string, byte[]> Faces = new();
    private static readonly Lock Gate = new();

    /// <summary>Install the resolver once per process (idempotent and thread-safe).</summary>
    public static void Ensure() => _ = Installed.Value;

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        // Every family resolves to Inter so documents never fail on a missing font.
        if (string.Equals(familyName, SemiBold, StringComparison.OrdinalIgnoreCase))
            return new FontResolverInfo("Inter-SemiBold", false, italic);
        return (bold, italic) switch
        {
            (true, true) => new FontResolverInfo("Inter-Bold", false, true),
            (true, false) => new FontResolverInfo("Inter-Bold"),
            (false, true) => new FontResolverInfo("Inter-Italic"),
            _ => new FontResolverInfo("Inter-Regular"),
        };
    }

    public byte[]? GetFont(string faceName)
    {
        lock (Gate)
        {
            if (Faces.TryGetValue(faceName, out var cached)) return cached;
            using var s = typeof(PdfFonts).Assembly.GetManifestResourceStream($"VellumPdfFont.{faceName}.ttf");
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Faces[faceName] = ms.ToArray();
        }
    }
}

/// <summary>What to stamp on every page of a rendition (DOC-022, PLT-805, SAF-043).</summary>
public sealed record WatermarkSpec(string Text, WatermarkPosition Position = WatermarkPosition.Diagonal, string Color = "#b91c1c", int Opacity = 18);

public sealed record PdfFacts(int Pages, bool Encrypted, string? Producer);

/// <summary>Low-level PDF operations on byte arrays: inspect, stamp, combine and convert images and text (PDFsharp/MigraDoc, MIT).</summary>
public static class PdfKit
{
    public static readonly XSize Letter = new(612, 792);

    public static bool LooksLikePdf(ReadOnlySpan<byte> bytes)
    {
        var head = bytes[..Math.Min(bytes.Length, 1024)];
        return head.IndexOf("%PDF-"u8) >= 0;
    }

    private static PdfDocument Open(byte[] pdf, PdfDocumentOpenMode mode)
    {
        if (!LooksLikePdf(pdf)) throw new PdfProcessingException("The file isn't a PDF (no PDF header). It may be corrupt or have the wrong extension.");
        try
        {
            return PdfReader.Open(new MemoryStream(pdf, writable: false), mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfProcessingException("The PDF is password-protected or encrypted, so it can't be watermarked or combined. Upload an unprotected copy.", inner: ex);
        }
        catch (Exception ex) when (ex is not PdfProcessingException)
        {
            throw new PdfProcessingException($"The PDF couldn't be read ({ex.Message.TrimEnd('.')}). It may be corrupt; re-export it and upload it again.", inner: ex);
        }
    }

    public static PdfFacts Inspect(byte[] pdf)
    {
        using var doc = Open(pdf, PdfDocumentOpenMode.Import);
        if (doc.PageCount == 0) throw new PdfProcessingException("The PDF has no pages.");
        return new PdfFacts(doc.PageCount, doc.SecuritySettings.IsEncrypted, doc.Info.Producer);
    }

    public static int PageCount(byte[] pdf) => Inspect(pdf).Pages;

    private static XColor Color(string hex, double alpha)
    {
        var h = (hex ?? "").TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) rgb = 0xb91c1c;
        return XColor.FromArgb((int)Math.Round(Math.Clamp(alpha, 0.02, 1) * 255), (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
    }

    /// <summary>Stamp each watermark on every page (diagonal across the page, or a label in the header or footer margin).</summary>
    public static byte[] Stamp(byte[] pdf, params WatermarkSpec[] marks)
    {
        var list = marks.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToList();
        if (list.Count == 0) return pdf;
        PdfFonts.Ensure();
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        foreach (var page in doc.Pages)
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var w = page.Width.Point;
            var h = page.Height.Point;
            foreach (var m in list) Draw(gfx, w, h, m);
        }
        return Save(doc);
    }

    private static void Draw(XGraphics gfx, double w, double h, WatermarkSpec m)
    {
        var text = m.Text.Trim().Replace('\n', ' ');
        var opacity = Math.Clamp(m.Opacity, 5, 100) / 100.0;
        if (m.Position == WatermarkPosition.Diagonal)
        {
            var diag = Math.Sqrt(w * w + h * h);
            var probe = new XFont(PdfFonts.Family, 100, XFontStyleEx.Bold);
            var width = gfx.MeasureString(text, probe).Width;
            var size = Math.Clamp(100 * diag * 0.72 / Math.Max(1, width), 14, 110);
            var font = new XFont(PdfFonts.Family, size, XFontStyleEx.Bold);
            var state = gfx.Save();
            gfx.TranslateTransform(w / 2, h / 2);
            gfx.RotateTransform(-Math.Atan2(h, w) * 180 / Math.PI);
            gfx.DrawString(text, font, new XSolidBrush(Color(m.Color, opacity)), new XRect(-diag / 2, -size, diag, size * 2), XStringFormats.Center);
            gfx.Restore(state);
            return;
        }
        // Header / footer label: a small, legible box in the page margin, over whatever is printed there.
        var f = new XFont(PdfFonts.SemiBold, 7.5);
        var tw = Math.Min(gfx.MeasureString(text, f).Width, w - 48);
        var box = new XRect((w - tw) / 2 - 8, m.Position == WatermarkPosition.Header ? 9 : h - 23, tw + 16, 14);
        var labelAlpha = Math.Max(opacity, 0.85);
        gfx.DrawRoundedRectangle(new XPen(Color(m.Color, labelAlpha), 0.6), new XSolidBrush(XColor.FromArgb(225, 255, 255, 255)), box, new XSize(4, 4));
        gfx.DrawString(text, f, new XSolidBrush(Color(m.Color, labelAlpha)), box, XStringFormats.Center);
    }

    /// <summary>Concatenate PDFs (for example a rendition and its approval manifest: the controlled copy, DOC-043).</summary>
    public static byte[] Combine(params byte[][] pdfs)
    {
        using var output = new PdfDocument();
        foreach (var pdf in pdfs)
        {
            using var src = Open(pdf, PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages) output.AddPage(page);
        }
        return Save(output);
    }

    public static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Render a MigraDoc document to PDF bytes.</summary>
    public static byte[] Render(Document document)
    {
        PdfFonts.Ensure();
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        return Save(renderer.PdfDocument);
    }

    /// <summary>An image (JPEG, PNG, BMP) on its own page, scaled to fit with margins, orientation following the image.</summary>
    public static byte[] FromImage(byte[] image, string title)
    {
        PdfFonts.Ensure();
        XImage img;
        try { img = XImage.FromStream(new MemoryStream(image, writable: false)); }
        catch (Exception ex) { throw new PdfProcessingException($"The image couldn't be read ({ex.Message.TrimEnd('.')}). It may be corrupt or in an unsupported variant.", inner: ex); }
        using (img)
        using (var doc = new PdfDocument())
        {
            doc.Info.Title = title;
            doc.Info.Creator = "Vellum";
            var landscape = img.PixelWidth > img.PixelHeight;
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(landscape ? Letter.Height : Letter.Width);
            page.Height = XUnit.FromPoint(landscape ? Letter.Width : Letter.Height);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                const double margin = 36;
                var maxW = page.Width.Point - margin * 2;
                var maxH = page.Height.Point - margin * 2 - 14;
                var iw = img.PointWidth > 0 ? img.PointWidth : img.PixelWidth;
                var ih = img.PointHeight > 0 ? img.PointHeight : img.PixelHeight;
                var scale = Math.Min(maxW / iw, maxH / ih);
                var dw = iw * scale;
                var dh = ih * scale;
                gfx.DrawImage(img, (page.Width.Point - dw) / 2, margin + (maxH - dh) / 2, dw, dh);
                gfx.DrawString(title, new XFont(PdfFonts.Family, 7), new XSolidBrush(XColor.FromArgb(255, 120, 113, 108)),
                    new XRect(margin, page.Height.Point - margin + 4, maxW, 10), XStringFormats.Center);
            }
            return Save(doc);
        }
    }

    /// <summary>Plain text, line breaks kept, on Letter pages with page numbers.</summary>
    public static byte[] FromText(string text, string title)
    {
        var doc = PdfLayout.NewDocument(title);
        var section = doc.AddSection();
        PdfLayout.PageSetup(section, doc);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Style = "Footer";
        footer.AddText(title + "   ·   page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
        var head = section.AddParagraph(title, "Heading1");
        head.Format.SpaceAfter = Unit.FromPoint(10);
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var p = section.AddParagraph();
            p.Style = "Mono";
            p.AddText(line.Length == 0 ? " " : line.Replace("\t", "    "));
        }
        return Render(doc);
    }

    /// <summary>Decode plain text: UTF-8 (with or without BOM), UTF-16 with BOM, otherwise Windows-1252-ish Latin-1.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
