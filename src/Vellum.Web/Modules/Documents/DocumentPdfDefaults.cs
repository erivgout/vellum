using Vellum.Domain;
using Vellum.Modules.Pdf;

namespace Vellum.Modules.Documents;

/// <summary>
/// Default PDF rendition rules of the controlled document (DOC-021..023, DOC-043, DOC-081, INV-054). The primary file is
/// converted when Document Control review starts (so conversion problems surface early), again on approval (Awaiting
/// Release) and on release, stamped for each phase; a revision can't become effective without its rendition, and
/// readers of effective and obsolete revisions only get the PDF. Administrators change these under
/// Administration › PDF renditions.
/// </summary>
public sealed class DocumentPdfDefaults : IPdfDefaults
{
    public string FormKey => DocKeys.Document;

    public void Define(PdfRulesBuilder rules) => rules
        .Style("#b91c1c", 16).AutoMark().Manifest(note: "This manifest lists every electronic signature applied to this revision. Each signer re-entered their password and confirmed the meaning shown (21 CFR 11.50, 11.200).")
        .Phase(DocKeys.AwaitingRelease, "{number} rev {revision} · Approved, not yet effective", WatermarkPosition.Header)
        .Phase(DocKeys.Approved, "{number} rev {revision} · Effective {effective_date} · Uncontrolled when printed", WatermarkPosition.Footer,
            download: AppRole.Author, print: AppRole.Reader)
        .Phase(DocKeys.Obsolete, null, download: AppRole.Manager, print: AppRole.Manager)
        .Field("primary_file", DocKeys.DcReview, convert: true)
        .Field("primary_file", DocKeys.AwaitingRelease, convert: true)
        .Field("primary_file", DocKeys.Approved, convert: true, require: true, pdfOnly: true)
        .Field("primary_file", DocKeys.Obsolete, pdfOnly: true);
}
