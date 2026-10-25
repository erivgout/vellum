using Microsoft.AspNetCore.Html;
using System.Text.Encodings.Web;

namespace Vellum.Modules.Inspections;

/// <summary>Pass / Warn / Fail / Pending chips: color plus icon plus text, never color alone.</summary>
public static class InspectionUi
{
    public static (string Css, string Icon, string Label) Style(InspectionResult r) => r switch
    {
        InspectionResult.Pass => ("pass", "bi-check-circle-fill", "Pass"),
        InspectionResult.Warn => ("warn", "bi-exclamation-triangle-fill", "Warn"),
        InspectionResult.Fail => ("fail", "bi-x-circle-fill", "Fail"),
        _ => ("pending", "bi-hourglass-split", "Pending"),
    };

    public static IHtmlContent Chip(InspectionResult r, string? label = null, bool large = false)
    {
        var (css, icon, text) = Style(r);
        var l = HtmlEncoder.Default.Encode(label ?? text);
        return new HtmlString($"<span class=\"vl-ins-chip {css}{(large ? " lg" : "")}\"><i class=\"bi {icon}\" aria-hidden=\"true\"></i>{l}</span>");
    }

    public static IHtmlContent Chip(string? key, string? label = null, bool large = false) => Chip(InspectionEvaluator.ParseResult(key), label, large);
}
