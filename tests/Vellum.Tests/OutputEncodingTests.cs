using Vellum.Infrastructure;
using Vellum.Infrastructure.Ui;

namespace Vellum.Tests;

/// <summary>Encoding of text that leaves the application: CSV cells and highlighted search results.</summary>
public class OutputEncodingTests
{
    [Theory] // CSV injection: formula-looking text stays text in a spreadsheet; numbers stay numbers
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("\tindented", "'\tindented")]
    [InlineData("-4.5", "-4.5")]
    [InlineData("+12", "+12")]
    [InlineData("-", "-")]
    [InlineData("Line clearance, room 3", "\"Line clearance, room 3\"")]
    [InlineData("SOP-00012", "SOP-00012")]
    [InlineData("", "")]
    public void Csv_cells_neutralise_formulas_and_quote_when_needed(string input, string expected) =>
        Assert.Equal(expected, CsvCell.Of(input));

    [Fact]
    public void Csv_cells_can_always_be_quoted()
    {
        Assert.Equal("\"SOP-00012\"", CsvCell.Of("SOP-00012", quote: true));
        Assert.Equal("\"'=1+1\"", CsvCell.Of("=1+1", quote: true));
        Assert.Equal("", CsvCell.Of(null, quote: true));
    }

    [Fact] // a search term never matches inside the HTML encoding of the text
    public void Search_highlighting_marks_the_raw_text_not_its_html()
    {
        Assert.Equal("R&amp;D <mark>amp</mark>lifier", Ui.Highlight("R&D amplifier", "amp").ToString());
        Assert.Equal("<mark>R&amp;D</mark> bench", Ui.Highlight("R&D bench", "r&d").ToString());
        Assert.Equal("&lt;b&gt;bold&lt;/b&gt;", Ui.Highlight("<b>bold</b>", "b").ToString());
        Assert.Equal("CAPA-<mark>0001</mark>2 and <mark>0001</mark>", Ui.Highlight("CAPA-00012 and 0001", "0001").ToString());
    }
}
