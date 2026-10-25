using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Modules.Supplier;
using Vellum.Packaging;

namespace Vellum.Modules.Portal;

/// <summary>
/// Supplier portal demo: portal contacts for Polymera Resins (portal.polymera) and Precision Molding (portal.precision), with
/// requests of every kind in different states. Everything goes through the real actions, routes and portal service, so audit
/// trails show who did what, including the supplier's own portal entries.
/// </summary>
public static class PortalDemo
{
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task SeedAsync(DemoContext d)
    {
        var polymera = await SupplierAsync(d, "Polymera Resins Inc.");
        var precision = await SupplierAsync(d, "Precision Molding Co.");
        if (polymera == null || precision == null) return; // supplier demo data not installed

        // ----------------------------------------------------------- portal contacts (invited by the SQE, then activated)
        await ContactAsync(d, polymera.Value, "Dana Whitfield", "d.whitfield@polymera.example.com", "Quality Manager", "portal.polymera", daysAgo: 30);
        await ContactAsync(d, precision.Value, "Greg Halvorsen", "g.halvorsen@precisionmolding.example.com", "Plant Quality Lead", "portal.precision", daysAgo: 45);
        // Marco Ruiz (Polymera) has an invitation he hasn't used yet: the supplier profile shows him as Invited.
        var polySnap = await d.Services.GetRequiredService<RecordService>().LoadRequiredAsync(polymera.Value);
        var marco = polySnap.Rows("contacts").FirstOrDefault(r => r.Values.Text("email") == "m.ruiz@polymera.example.com");
        if (marco != null) await DemoActions.RunAsync(d, polymera.Value, "portal-invite", "cwu", new() { ["contact"] = marco.Id.ToString() });


        // ----------------------------------------------------------- Polymera: several open requests
        // 1. The CAPA demo's open SCAR, sent to the portal (CAPA-011).
        var scar = await FindAsync(d, "scar", "Resin lots shipped without certificates of analysis");
        if (scar != null)
            await DemoActions.RunAsync(d, scar.Value, "portal-send", "cwu", new()
            {
                ["supplier"] = polymera.Value.ToString(), ["due"] = Iso(DemoContext.Days(5)),
                ["message"] = "Dana, please send us your containment, root cause and corrective actions for the three lots shipped without CoAs. The remaining 1,200 kg stays on hold until we have them.",
            });

        // 2. A safety data sheet for the resin.
        var resin = await FindAsync(d, "material", "RES-PC-2458");
        var sds = await d.CreateAsync(PortalKinds.DocForm, new()
        {
            ["supplier"] = polymera.Value, ["doc_type"] = "sds", ["subject"] = "Safety data sheet for Makroblend M-2458 NT (current revision)",
            ["material"] = resin, ["owner"] = d.User("cwu"), ["due_date"] = DemoContext.Days(7),
            ["instructions"] = "Please upload the current SDS (GHS format, section 1–16) for the natural grade we buy. Our copy is revision 4 from 2023.",
        }, "cwu", daysAgo: 3);
        await d.RouteAsync(sds.Id, "with_supplier");

        // 3. The annual self-assessment, partly answered by Dana (saved as a draft in the portal).
        var sq = await d.CreateAsync(PortalKinds.QuestionnaireForm, new()
        {
            ["supplier"] = polymera.Value, ["subject"] = $"{DemoContext.Today.Year} annual supplier self-assessment", ["questionnaire_type"] = "annual",
            ["owner"] = d.User("cwu"), ["due_date"] = DemoContext.Days(14),
            ["instructions"] = "We ask every approved supplier to complete this once a year. Attach your current certificates.",
        }, "cwu", daysAgo: 6, rows: new() { ["questions"] = PortalSupplierBehavior.StandardQuestionRows() });
        await d.RouteAsync(sq.Id, "with_supplier");
        await RespondAsync(d, "portal.polymera", sq.Id, submit: false, rows: (r, i) => i switch
        {
            0 => "Yes",
            1 => "No",
            2 => "Changes go through our MOC procedure QP-07. Customers named in the quality agreement are notified at least 90 days before any change to formulation, site or critical sub-tier supplier.",
            _ => null,
        });

        // 4. Findings of a remote supplier audit, sent to the portal (AUD-031).
        var audit = await d.CreateAsync("audit", new()
        {
            ["subject"] = "Polymera Resins – remote audit of CoA and lot release controls", ["audit_kind"] = "supplier", ["auditee"] = "Polymera Resins Inc.",
            ["entity_kind"] = "supplier", ["lead_auditor"] = d.User("tbrooks"), ["evaluation"] = "normal",
            ["start_at"] = DateTime.UtcNow.Date.AddDays(-9).AddHours(14), ["end_at"] = DateTime.UtcNow.Date.AddDays(-9).AddHours(18),
            ["criteria"] = "Quality agreement QA-POL-02, ISO 9001:2015 §8.6", ["scope"] = "Certificate of analysis generation, lot release and shipping release after SCAR on missing CoAs.",
        }, "tbrooks", daysAgo: 14, rows: new()
        {
            ["checklist"] =
            [
                Q("Is a CoA generated and reviewed for every lot before shipment?", "QA-POL-02 §4.3", "minor_nc", 3m,
                    "Lot release checklist allows shipping 'pending CoA' when the LIMS is down; used 3 times in the last quarter."),
                Q("Are CoA results traceable to the batch test records?", "ISO 9001 §8.6", "conforming", 5m, "Sampled 5 lots; all traceable."),
                Q("Is shipping release segregated from production?", "ISO 9001 §8.6", "observation", 4m,
                    "Shift supervisors can release shipments; no second-person check of documentation."),
                Q("Are customer-specific CoA requirements documented?", "QA-POL-02 §4.1", "conforming", 5m, "Customer requirement matrix CR-12 is current."),
            ],
        });
        await d.RouteAsync(audit.Id, "scheduled", asUser: "tbrooks");
        await DemoActions.RunAsync(d, audit.Id, "portal-findings", "tbrooks", new()
        {
            ["supplier"] = polymera.Value.ToString(), ["due"] = Iso(DemoContext.Days(18)),
            ["instructions"] = "Please answer both findings. The nonconformance needs a root cause, a correction, a corrective action and a target date.",
        });

        // 5. The two latest published scorecards (SUP-036).
        await ShareScorecardsAsync(d, polymera.Value, 2);

        // ----------------------------------------------------------- Precision Molding: submitted, returned and completed requests
        // 1. Renewed ISO 13485 certificate: uploaded by Greg, waiting for review.
        var cert = await d.CreateAsync(PortalKinds.DocForm, new()
        {
            ["supplier"] = precision.Value, ["doc_type"] = "iso_cert", ["standard"] = "iso_13485", ["subject"] = "ISO 13485 certificate with the extended scope (ultrasonic welding)",
            ["owner"] = d.User("cwu"), ["due_date"] = DemoContext.Days(4),
            ["instructions"] = "Your audit report mentions the scope now covers ultrasonic welding. Please upload the reissued certificate.",
        }, "cwu", daysAgo: 12);
        await d.RouteAsync(cert.Id, "with_supplier");
        await RespondAsync(d, "portal.precision", cert.Id, submit: true, main: new()
        {
            ["doc_number"] = "MD 612044-R2", ["issuer"] = "TÜV SÜD Product Service", ["issued_on"] = Iso(DemoContext.Days(-20)), ["expires_on"] = Iso(DemoContext.Days(1075)),
            ["supplier_comments"] = "Reissued after the surveillance audit. Scope now includes ultrasonic welding of thermoplastic assemblies.",
        }, file: ("doc_file", "PMC-ISO13485-MD612044-R2.pdf", Pdf("CERTIFICATE", ["TÜV SÜD Product Service GmbH", "Certificate No. MD 612044-R2",
            "Precision Molding Co., 770 Industrial Pkwy, Portage, MI 49002, USA", "has established and applies a quality management system for medical devices.",
            "Scope: Design transfer and manufacture of injection molded, insert molded and ultrasonically welded thermoplastic components for medical devices.",
            "Standard: EN ISO 13485:2016", $"Valid until {DemoContext.Days(1075):dd MMMM yyyy}"])));

        // 2. A SCAR on short shots: Greg responded, Casey returned it asking for a deeper root cause.
        var scar2 = await d.CreateAsync("scar", new()
        {
            ["subject"] = "Short shots on VX-200 housing bottoms (lot P-24417)", ["owner"] = d.User("cwu"), ["priority"] = "high",
            ["description"] = "Incoming inspection found incomplete fill at the battery-door hinge on 31 of 200 sampled HSG-200-B housings. The hinge breaks on first use.",
            ["requirement"] = "DWG-200-115 rev B: no short shots; hinge must pass 50 open/close cycles",
            ["supplier_name"] = "Precision Molding Co.", ["supplier_code"] = "V-10388", ["supplier_contact"] = "Greg Halvorsen", ["supplier_email"] = "g.halvorsen@precisionmolding.example.com",
            ["part_number"] = "HSG-200-B", ["part_description"] = "VX-200 pump housing, bottom", ["lot_number"] = "P-24417", ["po_number"] = "PO-781455", ["quantity_affected"] = 4000m,
            ["containment_required"] = true, ["stock_quarantined"] = 3800m, ["containment_actions"] = "Hold all HSG-200-B stock at Precision and in transit; 100% hinge inspection of anything shipped.",
            ["response_due"] = DemoContext.Days(9),
        }, "cwu", daysAgo: 16);
        await d.RouteAsync(scar2.Id, "initial_approval", assignees: ["sortiz"]);
        await d.RouteAsync(scar2.Id, "assign_to_supplier");
        await DemoActions.RunAsync(d, scar2.Id, "portal-send", "cwu", new()
        {
            ["supplier"] = precision.Value.ToString(), ["due"] = Iso(DemoContext.Days(9)),
            ["message"] = "Greg, please contain all HSG-200-B stock and send us your 8D. We need the root cause for the hinge fill problem before the next build on the 14th.",
        });
        await RespondAsync(d, "portal.precision", scar2.Id, submit: true, main: new()
        {
            ["sr_containment"] = "3,800 housings on hold at Portage and 1,200 in transit recalled. 100% hinge inspection: 412 rejected.",
            ["sr_root_cause"] = "Short shots caused by low melt temperature on press 7.",
            ["sr_corrective_action"] = "Melt temperature raised by 10 °C and operators retrained.",
            ["sr_implementation_date"] = Iso(DemoContext.Days(-2)), ["sr_responder"] = "Greg Halvorsen, Plant Quality Lead",
        });
        await DemoActions.RunAsync(d, scar2.Id, "portal-return", "cwu",
            comment: "Thanks Greg. A low melt temperature is the symptom: why did it drop on press 7, and why wasn't it caught? Please add the occurrence and escape root causes (5 Whys) and how you'll stop it on the other presses.");

        // 3. A completed onboarding questionnaire.
        var sq2 = await d.CreateAsync(PortalKinds.QuestionnaireForm, new()
        {
            ["supplier"] = precision.Value, ["subject"] = "Ultrasonic welding process questionnaire", ["questionnaire_type"] = "onboarding",
            ["owner"] = d.User("cwu"), ["due_date"] = DemoContext.Days(3),
        }, "cwu", daysAgo: 40, rows: new()
        {
            ["questions"] =
            [
                new() { ["q_text"] = "Is the ultrasonic welding process validated (IQ/OQ/PQ)?", ["q_type"] = "yes_no", ["q_required"] = true },
                new() { ["q_text"] = "How do you monitor weld energy and collapse distance in production?", ["q_type"] = "text", ["q_required"] = true },
                new() { ["q_text"] = "How often are the horns and fixtures inspected for wear?", ["q_type"] = "text", ["q_required"] = true },
            ],
        });
        await d.RouteAsync(sq2.Id, "with_supplier");
        await RespondAsync(d, "portal.precision", sq2.Id, submit: true, rows: (r, i) => i switch
        {
            0 => "Yes",
            1 => "Every weld records energy, time and collapse distance; out-of-window welds are rejected automatically and logged in the MES.",
            _ => "Weekly visual inspection and a monthly dimensional check; horns are replaced at 250,000 cycles.",
        }, main: new() { ["supplier_comments"] = "Validation report VR-UW-2026-03 is available on request." });
        await d.UpdateAsync(sq2.Id, new() { ["review_result"] = "acceptable", ["review_notes"] = "Validated process with 100% monitoring. Matches what we saw at the audit." }, "cwu");
        await d.RouteAsync(sq2.Id, "completed", asUser: "cwu");

        await ShareScorecardsAsync(d, precision.Value, 1);
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, object?> Q(string question, string clause, string classification, decimal score, string comments) => new()
    {
        ["question"] = question, ["clause"] = clause, ["out_of"] = 5m, ["required"] = true, ["classification"] = classification, ["score"] = score, ["comments"] = comments,
    };

    private static async Task<Guid?> SupplierAsync(DemoContext d, string name)
    {
        var form = d.Config.FormsByKey.GetValueOrDefault("supplier");
        if (form == null) return null;
        return await d.Db.Records.AsNoTracking().Where(r => r.FormId == form.Id &&
                d.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "name" && v.Text == name))
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
    }

    private static async Task<Guid?> FindAsync(DemoContext d, string formKey, string titleStart)
    {
        if (!d.Config.FormsByKey.TryGetValue(formKey, out var form)) return null;
        return await d.Db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.Title.StartsWith(titleStart))
            .OrderByDescending(r => r.CreatedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync();
    }

    /// <summary>Invite through the real invitation flow, then accept it as the contact would (password set from the link).</summary>
    private static async Task ContactAsync(DemoContext d, Guid supplierId, string name, string email, string role, string username, int daysAgo)
    {
        d.As("cwu");
        d.Db.ChangeTracker.Clear();
        var res = await d.Services.GetRequiredService<PortalAccounts>().InviteAsync(supplierId, name, email, role, username);
        if (!res.Success) throw new InvalidOperationException("Demo portal invitation failed: " + string.Join("; ", res.Errors ?? []));
        var user = await d.Db.Users.FirstAsync(u => u.Username == username);
        var when = DateTime.UtcNow.AddDays(-daysAgo);
        var hash = PasswordHasher.Hash(DemoContext.Password);
        user.PasswordHash = hash;
        user.PasswordChangedAt = when;
        user.LastLoginAt = DateTime.UtcNow.AddHours(-20);
        user.CreatedAt = when.AddDays(-1);
        user.TimeZone = "America/Detroit";
        await d.Db.Set<AccountToken>().Where(t => t.UserId == user.Id && t.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, when));
        d.Db.PasswordHistory.Add(new PasswordHistory { UserId = user.Id, Hash = hash, CreatedAt = when });
        d.Db.SecurityEvents.Add(new SecurityEvent { At = when, Kind = "Password set (portal invitation)", Success = true, UserId = user.Id, Username = user.Username });
        await d.Db.SaveChangesAsync();
        d.Directory.Invalidate();
    }

    private static async Task ShareScorecardsAsync(DemoContext d, Guid supplierId, int count)
    {
        if (!d.Config.FormsByKey.TryGetValue("scorecard", out var form)) return;
        var published = d.Config.FormsByKey["scorecard"].Workflows.SelectMany(w => w.Phases).Where(p => p.Key == "published").Select(p => p.Id).ToList();
        var ids = await d.Db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseId != null && published.Contains(r.PhaseId.Value) &&
                d.Db.RecordValues.Any(v => v.RecordId == r.Id && v.RowId == null && v.FieldKey == "supplier" && v.RefGuid == supplierId))
            .OrderByDescending(r => r.CreatedAt).Take(count).Select(r => r.Id).ToListAsync();
        foreach (var id in ids.AsEnumerable().Reverse())
            await DemoActions.RunAsync(d, id, "portal-share", "cwu", new() { ["message"] = "Your latest supplier scorecard is available in the supplier portal." });
    }

    /// <summary>Answer a shared request in the portal as the supplier contact (the same path as the portal's form post).</summary>
    private static async Task RespondAsync(DemoContext d, string asUser, Guid recordId, bool submit, Dictionary<string, string?>? main = null,
        Func<RowBag, int, string?>? rows = null, (string Field, string Name, byte[] Bytes)? file = null)
    {
        d.As(asUser);
        d.Db.ChangeTracker.Clear();
        var share = await d.Db.Set<ExternalShare>().AsNoTracking().FirstAsync(s => s.RecordId == recordId && s.Status != ExternalShareStatus.Withdrawn);
        var snap = await d.Services.GetRequiredService<RecordService>().LoadRequiredAsync(recordId);
        var fields = new Dictionary<string, StringValues>();
        foreach (var (k, v) in main ?? []) fields[$"f.{k}"] = v;
        if (rows != null && PortalKinds.Get(share.Kind)?.Rows is { } spec)
        {
            var i = 0;
            foreach (var row in snap.Rows(spec.Section).OrderBy(r => r.Order))
                if (rows(row, i++) is { } answer) fields[$"r.{row.Id:N}.{spec.Editable[0]}"] = answer;
        }
        var files = new FormFileCollection();
        if (file is { } f)
            files.Add(new FormFile(new MemoryStream(f.Bytes), 0, f.Bytes.Length, $"file.{f.Field}", f.Name)
            {
                Headers = new HeaderDictionary(), ContentType = "application/pdf",
            });
        var res = await d.Services.GetRequiredService<PortalService>().RespondAsync(share.Id, snap.Record.Version, new FormCollection(fields, files), submit);
        if (!res.Ok) throw new InvalidOperationException($"Demo portal response to {snap.Record.Number} failed: {string.Join("; ", res.Errors)}");
    }

    /// <summary>A small one-page PDF (Helvetica) standing in for a supplier's certificate.</summary>
    private static byte[] Pdf(string heading, IReadOnlyList<string> lines)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content = new StringBuilder("BT /F2 22 Tf 72 700 Td (" + Esc(heading) + ") Tj ET\n");
        var y = 660;
        foreach (var line in lines) { content.Append($"BT /F1 11 Tf 72 {y} Td ({Esc(line)}) Tj ET\n"); y -= 22; }
        var stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
