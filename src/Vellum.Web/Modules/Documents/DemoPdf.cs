using System.Globalization;
using System.Text;

namespace Vellum.Modules.Documents;

/// <summary>
/// Writes small, valid, multi-page PDF files for demo controlled documents (Helvetica, no dependencies), so the demo
/// primary files open in the browser's PDF viewer like real controlled content.
/// </summary>
internal static class DemoPdf
{
    private const double Left = 72, Right = 540, Top = 720, Bottom = 84;

    public sealed record Section(string Heading, string Body);

    public static byte[] Render(string number, string revision, string title, string type, string owner, IReadOnlyList<Section> sections)
    {
        var pages = new List<StringBuilder>();
        StringBuilder page = null!;
        double y = 0;

        void NewPage()
        {
            page = new StringBuilder();
            pages.Add(page);
            // Header band and identification block
            page.Append("0.145 0.388 0.922 rg 0 762 612 30 re f\n");
            Text(page, "F2", 9, Left, 773, "ACME MEDICAL DEVICES", white: true);
            Text(page, "F1", 9, 380, 773, $"{type.ToUpperInvariant()}  |  {number}  REV {revision}", white: true);
            y = Top;
            if (pages.Count == 1)
            {
                foreach (var line in Wrap(title, 16, Right - Left)) { Text(page, "F2", 16, Left, y, line); y -= 21; }
                y -= 2;
                Text(page, "F1", 9, Left, y, $"Document {number}  -  Revision {revision}  -  Owner: {owner}  -  Kalamazoo | Boston | Galway");
                y -= 12;
                page.Append(Format("0.8 0.8 0.8 RG 0.6 w {0} {1} m {2} {1} l S\n", Left, y, Right));
                y -= 22;
            }
        }

        void Ensure(double needed)
        {
            if (y - needed < Bottom) NewPage();
        }

        NewPage();
        foreach (var s in sections)
        {
            Ensure(40);
            Text(page, "F2", 11.5, Left, y, s.Heading);
            y -= 17;
            foreach (var para in s.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var bullet = para.StartsWith("- ", StringComparison.Ordinal);
                var indent = bullet ? 14 : 0;
                var lines = Wrap(bullet ? para[2..] : para, 10, Right - Left - indent);
                for (var i = 0; i < lines.Count; i++)
                {
                    Ensure(14);
                    if (bullet && i == 0) Text(page, "F1", 10, Left + 3, y, "-");
                    Text(page, "F1", 10, Left + indent, y, lines[i]);
                    y -= 13.5;
                }
                y -= 4;
            }
            y -= 8;
        }

        var total = pages.Count;
        for (var i = 0; i < total; i++)
        {
            var p = pages[i];
            p.Append(Format("0.8 0.8 0.8 RG 0.6 w {0} 66 m {1} 66 l S\n", Left, Right));
            Text(p, "F1", 8, Left, 52, "CONTROLLED DOCUMENT - uncontrolled when printed. Verify the effective revision in Vellum before use.");
            Text(p, "F1", 8, 486, 52, $"Page {i + 1} of {total}");
        }
        return Assemble(pages);
    }

    private static void Text(StringBuilder sb, string font, double size, double x, double y, string text, bool white = false)
    {
        sb.Append(white ? "1 1 1 rg\n" : "0.11 0.1 0.09 rg\n");
        sb.Append(Format("BT /{0} {1} Tf {2} {3} Td ({4}) Tj ET\n", font, size, x, y, Escape(text)));
    }

    private static List<string> Wrap(string text, double size, double width)
    {
        var max = Math.Max(20, (int)(width / (size * 0.5)));
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in Ascii(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > max) { lines.Add(line.ToString()); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    private static string Ascii(string s) => s
        .Replace('–', '-').Replace('—', '-').Replace('·', '-').Replace('’', '\'').Replace('‘', '\'')
        .Replace('“', '"').Replace('”', '"').Replace("…", "...").Replace("°", " deg").Replace("µ", "u").Replace("±", "+/-")
        .Replace("≤", "<=").Replace("≥", ">=").Replace("×", "x");

    private static string Escape(string s) => Ascii(s).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string Format(string f, params object[] args) => string.Format(CultureInfo.InvariantCulture, f, args);

    private static byte[] Assemble(List<StringBuilder> pages)
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(string body) { offsets.Add(ms.Position); Write($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        Write("%PDF-1.4\n");
        var n = pages.Count;
        // 1 catalog, 2 pages, 3-4 fonts, then (page, content) pairs
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{5 + i * 2} 0 R"));
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {n} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < n; i++)
        {
            var content = pages[i].ToString();
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + i * 2} 0 R >>");
            Obj($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        var xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}
