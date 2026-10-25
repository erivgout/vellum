using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MDColor = MigraDoc.DocumentObjectModel.Color;

namespace Vellum.Modules.Pdf;

/// <summary>A label/value pair in a <see cref="PdfReport"/> field grid. Wide fields span the whole row.</summary>
public sealed record PdfField(string Label, string? Value, bool Wide = false);

/// <summary>Tone of a <see cref="PdfReport"/> banner, matching the UI's vl-banner tones.</summary>
public enum PdfTone { Info, Ok, Warn, Bad, Violet, Neutral }

/// <summary>
/// A generic, browser-free document layout for printable records and official forms (PLT-809, INT-409): a header band,
/// a title block, then headings, paragraphs, label/value grids, tables and banners, with page numbers and an optional
/// diagonal marking (DRAFT, SUPERSEDED…). Render it with <see cref="IPdfService.RenderReport"/>.
/// </summary>
/// <example>
/// <code>
/// var report = new PdfReport { Title = "MedWatch 3500A", Reference = "MDR-00012", Context = "Regulatory reporting" }
///     .Heading("A. Patient information")
///     .Fields([new("Identifier", "J.D."), new("Age", "64"), new("Sex", "Female")], columns: 3)
///     .Table(["Product", "Lot", "Expiry"], [["VX-200", "L2291", "2027-04"]]);
/// byte[] pdf = pdfService.RenderReport(report);
/// </code>
/// </example>
public sealed class PdfReport
{
    public required string Title { get; init; }
    /// <summary>Small line above the title, e.g. "SOP-00012 · Revision B".</summary>
    public string? Reference { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>Left of the header band (defaults to the organization name).</summary>
    public string? Organization { get; init; }
    /// <summary>Right of the header band, e.g. "Document Control · Controlled Document".</summary>
    public string? Context { get; init; }
    /// <summary>Status chip under the title, e.g. "Effective revision".</summary>
    public string? Status { get; init; }
    public PdfTone StatusTone { get; init; } = PdfTone.Neutral;
    /// <summary>Diagonal marking stamped on every page (SAF-043), e.g. "DRAFT".</summary>
    public string? Marking { get; init; }
    /// <summary>Footer text left of the page numbers (who printed it, when, content hash…).</summary>
    public string? Footer { get; init; }
    /// <summary>Second footer line, e.g. "Printed copies are uncontrolled."</summary>
    public string? FooterNote { get; init; }
    public bool Landscape { get; init; }
    public string? Author { get; init; }

    internal List<Action<Section>> Blocks { get; } = [];

    public PdfReport Heading(string text, string? sub = null)
    {
        Blocks.Add(s =>
        {
            var p = s.AddParagraph(text, "Heading2");
            p.Format.KeepWithNext = true;
            if (sub != null) { var q = s.AddParagraph(sub, "Muted"); q.Format.KeepWithNext = true; q.Format.SpaceAfter = Unit.FromPoint(4); }
        });
        return this;
    }

    public PdfReport Text(string text, bool muted = false)
    {
        Blocks.Add(s =>
        {
            foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
                s.AddParagraph(para.Length == 0 ? " " : para, muted ? "Muted" : "Normal");
        });
        return this;
    }

    public PdfReport Fields(IEnumerable<PdfField> fields, int columns = 2)
    {
        var list = fields.ToList();
        if (list.Count == 0) return this;
        columns = Math.Clamp(columns, 1, 4);
        Blocks.Add(s => PdfLayout.FieldGrid(s, list, columns, Landscape));
        return this;
    }

    public PdfReport Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows, IReadOnlyList<double>? relativeWidths = null, string? empty = null)
    {
        var data = rows.ToList();
        Blocks.Add(s =>
        {
            if (data.Count == 0) { s.AddParagraph(empty ?? "None.", "Muted"); return; }
            PdfLayout.DataTable(s, headers, data, relativeWidths, Landscape);
        });
        return this;
    }

    public PdfReport Banner(string title, string? text = null, PdfTone tone = PdfTone.Info)
    {
        Blocks.Add(s => PdfLayout.Banner(s, title, text, tone, Landscape));
        return this;
    }

    public PdfReport Spacer(double points = 8)
    {
        Blocks.Add(s => s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(points));
        return this;
    }

    public PdfReport PageBreak()
    {
        Blocks.Add(s => s.AddPageBreak());
        return this;
    }
}

/// <summary>MigraDoc styles and building blocks shared by every generated PDF, matching the Vellum design system.</summary>
public static class PdfLayout
{
    public static readonly MDColor Ink = new(28, 25, 23);
    public static readonly MDColor Ink2 = new(87, 83, 78);
    public static readonly MDColor Ink3 = new(120, 113, 108);
    public static readonly MDColor Line = new(231, 229, 228);
    public static readonly MDColor Surface2 = new(245, 245, 244);
    public static readonly MDColor Brand = new(37, 99, 235);

    public static (MDColor Fg, MDColor Bg) Tone(PdfTone t) => t switch
    {
        PdfTone.Ok => (new MDColor(21, 128, 61), new MDColor(240, 253, 244)),
        PdfTone.Warn => (new MDColor(180, 83, 9), new MDColor(255, 251, 235)),
        PdfTone.Bad => (new MDColor(185, 28, 28), new MDColor(254, 242, 242)),
        PdfTone.Violet => (new MDColor(109, 40, 217), new MDColor(245, 243, 255)),
        PdfTone.Info => (new MDColor(29, 78, 216), new MDColor(239, 246, 255)),
        _ => (Ink2, Surface2),
    };

    private const double PortraitWidth = 612 - 2 * 54;
    private const double LandscapeWidth = 792 - 2 * 54;
    public static double ContentWidth(bool landscape) => landscape ? LandscapeWidth : PortraitWidth;

    public static Document NewDocument(string title, string? author = null)
    {
        PdfFonts.Ensure();
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Author = author ?? "Vellum";
        doc.Info.Subject = title;

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.Family;
        normal.Font.Size = 9.5;
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);
        normal.ParagraphFormat.LineSpacingRule = MigraDoc.DocumentObjectModel.LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.15;

        var h1 = doc.Styles[StyleNames.Heading1]!;
        h1.Font.Name = PdfFonts.Family;
        h1.Font.Size = 17;
        h1.Font.Bold = true;
        h1.Font.Color = Ink;
        h1.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);
        h1.ParagraphFormat.LineSpacingRule = MigraDoc.DocumentObjectModel.LineSpacingRule.Single;

        var h2 = doc.Styles[StyleNames.Heading2]!;
        h2.Font.Name = PdfFonts.SemiBold;
        h2.Font.Size = 11.5;
        h2.Font.Bold = false;
        h2.Font.Color = Ink;
        h2.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        h2.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        h2.ParagraphFormat.Borders.Bottom.Color = Line;
        h2.ParagraphFormat.Borders.Bottom.Width = 0.75;
        h2.ParagraphFormat.Borders.Distance = Unit.FromPoint(3);

        var muted = doc.Styles.AddStyle("Muted", StyleNames.Normal);
        muted.Font.Color = Ink2;
        muted.Font.Size = 8.5;

        var label = doc.Styles.AddStyle("Label", StyleNames.Normal);
        label.Font.Name = PdfFonts.SemiBold;
        label.Font.Size = 7;
        label.Font.Color = Ink3;
        label.ParagraphFormat.SpaceAfter = Unit.FromPoint(1);

        var value = doc.Styles.AddStyle("Value", StyleNames.Normal);
        value.Font.Size = 9.5;
        value.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var cell = doc.Styles.AddStyle("Cell", StyleNames.Normal);
        cell.Font.Size = 8.5;
        cell.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);
        cell.ParagraphFormat.LineSpacing = 1.1;

        var th = doc.Styles.AddStyle("CellHead", "Cell");
        th.Font.Name = PdfFonts.SemiBold;
        th.Font.Size = 7.5;
        th.Font.Color = Ink2;

        var mono = doc.Styles.AddStyle("Mono", StyleNames.Normal);
        mono.Font.Size = 8.5;
        mono.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);
        mono.ParagraphFormat.LineSpacing = 1.05;

        var footer = doc.Styles[StyleNames.Footer]!;
        footer.Font.Name = PdfFonts.Family;
        footer.Font.Size = 7;
        footer.Font.Color = Ink3;
        footer.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var header = doc.Styles[StyleNames.Header]!;
        header.Font.Name = PdfFonts.Family;
        header.Font.Size = 7.5;
        header.Font.Color = Ink2;
        return doc;
    }

    public static void PageSetup(Section s, Document doc, bool landscape = false)
    {
        var ps = doc.DefaultPageSetup.Clone();
        ps.PageFormat = PageFormat.Letter;
        ps.Orientation = landscape ? Orientation.Landscape : Orientation.Portrait;
        ps.TopMargin = Unit.FromPoint(72);
        ps.BottomMargin = Unit.FromPoint(64);
        ps.LeftMargin = Unit.FromPoint(54);
        ps.RightMargin = Unit.FromPoint(54);
        ps.HeaderDistance = Unit.FromPoint(28);
        ps.FooterDistance = Unit.FromPoint(26);
        s.PageSetup = ps;
    }

    /// <summary>Render a report: header band, title block, blocks, footer with page numbers, optional diagonal marking.</summary>
    public static byte[] Render(PdfReport r, string? defaultOrganization, string markingColor = "#b91c1c", int markingOpacity = 14)
    {
        var doc = NewDocument(r.Title, r.Author);
        var s = doc.AddSection();
        PageSetup(s, doc, r.Landscape);
        var width = ContentWidth(r.Landscape);

        // Header band
        var ht = s.Headers.Primary.AddTable();
        ht.Borders.Bottom.Width = 1.25;
        ht.Borders.Bottom.Color = Ink;
        ht.AddColumn(Unit.FromPoint(width * 0.5));
        ht.AddColumn(Unit.FromPoint(width * 0.5));
        var hr = ht.AddRow();
        hr.BottomPadding = Unit.FromPoint(5);
        var org = hr.Cells[0].AddParagraph();
        org.AddFormattedText(r.Organization ?? defaultOrganization ?? "Vellum", new Font(PdfFonts.SemiBold, 9) { Color = Ink });
        var ctx = hr.Cells[1].AddParagraph(r.Context ?? "");
        ctx.Format.Alignment = ParagraphAlignment.Right;

        // Footer: text left, page x of y right
        var ft = s.Footers.Primary.AddTable();
        ft.Borders.Top.Width = 0.5;
        ft.Borders.Top.Color = Line;
        ft.AddColumn(Unit.FromPoint(width * 0.8));
        ft.AddColumn(Unit.FromPoint(width * 0.2));
        var fr = ft.AddRow();
        fr.TopPadding = Unit.FromPoint(4);
        var left = fr.Cells[0].AddParagraph(r.Footer ?? "");
        left.Style = StyleNames.Footer;
        if (r.FooterNote != null) fr.Cells[0].AddParagraph(r.FooterNote).Style = StyleNames.Footer;
        var pn = fr.Cells[1].AddParagraph();
        pn.Style = StyleNames.Footer;
        pn.Format.Alignment = ParagraphAlignment.Right;
        pn.AddText("Page ");
        pn.AddPageField();
        pn.AddText(" of ");
        pn.AddNumPagesField();

        // Title block
        if (r.Reference != null) s.AddParagraph(r.Reference, "Muted").Format.SpaceAfter = Unit.FromPoint(2);
        s.AddParagraph(r.Title, StyleNames.Heading1);
        if (r.Subtitle != null) s.AddParagraph(r.Subtitle, "Muted");
        if (r.Status != null)
        {
            var (fg, bg) = Tone(r.StatusTone);
            var chip = s.AddParagraph();
            chip.Format.SpaceBefore = Unit.FromPoint(4);
            chip.Format.SpaceAfter = Unit.FromPoint(6);
            var t = chip.AddFormattedText(" " + r.Status + " ", new Font(PdfFonts.SemiBold, 8) { Color = fg });
            chip.Format.Shading.Color = bg;
            _ = t;
        }

        foreach (var block in r.Blocks) block(s);

        var pdf = PdfKit.Render(doc);
        return r.Marking is { Length: > 0 } m ? PdfKit.Stamp(pdf, new WatermarkSpec(m, WatermarkPosition.Diagonal, markingColor, markingOpacity)) : pdf;
    }

    internal static void FieldGrid(Section s, List<PdfField> fields, int columns, bool landscape)
    {
        var width = ContentWidth(landscape);
        var t = s.AddTable();
        t.Borders.Visible = false;
        for (var i = 0; i < columns; i++) t.AddColumn(Unit.FromPoint(width / columns));
        Row? row = null;
        var col = columns;
        foreach (var f in fields)
        {
            if (f.Wide || col >= columns)
            {
                row = t.AddRow();
                row.TopPadding = Unit.FromPoint(3);
                row.BottomPadding = Unit.FromPoint(5);
                col = 0;
            }
            var cell = row!.Cells[col];
            if (f.Wide && columns > 1) cell.MergeRight = columns - 1;
            cell.P(f.Label.ToUpperInvariant(), "Label");
            var v = string.IsNullOrWhiteSpace(f.Value) ? "–" : f.Value!;
            foreach (var line in v.Replace("\r\n", "\n").Split('\n'))
                cell.P(line.Length == 0 ? " " : line, "Value");
            col = f.Wide ? columns : col + 1;
        }
    }

    internal static void DataTable(Section s, IReadOnlyList<string> headers, List<IReadOnlyList<string?>> rows, IReadOnlyList<double>? rel, bool landscape)
    {
        var width = ContentWidth(landscape);
        var weights = rel != null && rel.Count == headers.Count ? rel : headers.Select(_ => 1.0).ToList();
        var total = weights.Sum();
        var t = s.AddTable();
        t.Borders.Width = 0;
        t.LeftPadding = Unit.FromPoint(5);
        t.RightPadding = Unit.FromPoint(5);
        foreach (var w in weights) t.AddColumn(Unit.FromPoint(width * w / total));
        var head = t.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Surface2;
        head.TopPadding = Unit.FromPoint(4);
        head.BottomPadding = Unit.FromPoint(4);
        for (var i = 0; i < headers.Count; i++) head.Cells[i].P(headers[i].ToUpperInvariant(), "CellHead");
        foreach (var r in rows)
        {
            var row = t.AddRow();
            row.TopPadding = Unit.FromPoint(4);
            row.BottomPadding = Unit.FromPoint(4);
            row.Borders.Bottom.Width = 0.5;
            row.Borders.Bottom.Color = Line;
            for (var i = 0; i < headers.Count; i++)
            {
                var text = i < r.Count ? r[i] : null;
                foreach (var line in (string.IsNullOrEmpty(text) ? "–" : text).Replace("\r\n", "\n").Split('\n'))
                    row.Cells[i].P(line, "Cell");
            }
        }
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);
    }

    internal static void Banner(Section s, string title, string? text, PdfTone tone, bool landscape)
    {
        var (fg, bg) = Tone(tone);
        var t = s.AddTable();
        t.AddColumn(Unit.FromPoint(ContentWidth(landscape)));
        var row = t.AddRow();
        row.Shading.Color = bg;
        row.TopPadding = Unit.FromPoint(6);
        row.BottomPadding = Unit.FromPoint(6);
        t.LeftPadding = Unit.FromPoint(9);
        row.Cells[0].Borders.Left.Width = 2.5;
        row.Cells[0].Borders.Left.Color = fg;
        var p = row.Cells[0].AddParagraph();
        p.AddFormattedText(title, new Font(PdfFonts.SemiBold, 9) { Color = fg });
        if (text != null) row.Cells[0].P(text, "Muted");
        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);
    }
}

internal static class MigraDocExtensions
{
    public static Paragraph P(this Cell cell, string text, string style)
    {
        var p = cell.AddParagraph(text);
        p.Style = style;
        return p;
    }
}
