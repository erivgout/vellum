using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace Vellum.Modules.Integrations.SharePoint;

/// <summary>Status badges for the SharePoint administration pages (icon + text, never colour alone).</summary>
public static class SharePointUi
{
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    public static IHtmlContent JobStatus(SharePointJob j) => new HtmlString(j.Status switch
    {
        SpJobStatus.Pending when j.Attempts > 0 => "<span class=\"vl-kw vl-kw-warning\"><i class=\"bi bi-arrow-repeat\"></i>Retrying</span>",
        SpJobStatus.Pending => "<span class=\"vl-kw vl-kw-info\"><i class=\"bi bi-hourglass-split\"></i>Queued</span>",
        SpJobStatus.Succeeded => "<span class=\"vl-kw vl-kw-success\"><i class=\"bi bi-check2\"></i>Succeeded</span>",
        SpJobStatus.Failed => "<span class=\"vl-kw vl-kw-danger\"><i class=\"bi bi-x-lg\"></i>Failed</span>",
        SpJobStatus.Skipped => "<span class=\"vl-kw vl-kw-secondary\"><i class=\"bi bi-skip-forward\"></i>Skipped</span>",
        _ => "<span class=\"vl-kw vl-kw-secondary\"><i class=\"bi bi-slash-circle\"></i>Cancelled</span>",
    });

    public static IHtmlContent PubStatus(SpPublicationStatus s) => new HtmlString(s switch
    {
        SpPublicationStatus.Published => "<span class=\"vl-kw vl-kw-success\"><i class=\"bi bi-cloud-check\"></i>Current</span>",
        SpPublicationStatus.Replaced => "<span class=\"vl-kw vl-kw-info\"><i class=\"bi bi-arrow-left-right\"></i>Replaced</span>",
        SpPublicationStatus.Removed => "<span class=\"vl-kw vl-kw-secondary\"><i class=\"bi bi-trash3\"></i>Removed</span>",
        _ => "<span class=\"vl-kw vl-kw-warning\"><i class=\"bi bi-archive\"></i>Marked obsolete</span>",
    });

    public static IHtmlContent Kind(SpJobKind k) => new HtmlString(k == SpJobKind.Publish
        ? "<span class=\"adm-status info\"><i class=\"bi bi-cloud-arrow-up\"></i>Publish</span>"
        : "<span class=\"adm-status warn\"><i class=\"bi bi-cloud-slash\"></i>Withdraw</span>");

    public static IHtmlContent Connection(SpConnection c) => new HtmlString(c == SpConnection.Graph
        ? "<span class=\"sp-conn\"><i class=\"bi bi-microsoft\"></i>SharePoint Online (Graph)</span>"
        : "<span class=\"sp-conn\"><i class=\"bi bi-folder2\"></i>Local folder (development)</span>");

    public static IHtmlContent Test(SharePointProfile p) => new HtmlString(p.LastTestOk switch
    {
        true => $"<span class=\"adm-status ok\" title=\"{E(p.LastTestSummary)}\"><i class=\"bi bi-check-circle-fill\"></i>Connected</span>",
        false => $"<span class=\"adm-status bad\" title=\"{E(p.LastTestSummary)}\"><i class=\"bi bi-x-circle-fill\"></i>Test failed</span>",
        _ => "<span class=\"adm-status off\"><i class=\"bi bi-dash-circle\"></i>Not tested</span>",
    });

    public static string Withdraw(SpWithdrawAction a) => a == SpWithdrawAction.Delete ? "Delete from the library" : "Mark obsolete";
}
