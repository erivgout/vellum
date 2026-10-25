using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Vellum.Engine.Records;

namespace Vellum.Modules.AdverseEvents;

/// <summary>The 3500A content of one MedWatch, independent of how it's stored. Built from a record with <see cref="From"/>.</summary>
public sealed class EmdrReport
{
    public string ReportNumber { get; init; } = "";
    /// <summary>initial | five_day | supplemental.</summary>
    public string ReportType { get; init; } = AerKeys.Initial;
    public string ReporterType { get; init; } = AerKeys.Manufacturer;
    public int? FollowUpNumber { get; init; }
    public List<string> FollowUpTypes { get; init; } = [];
    public List<string> ChangedSections { get; init; } = [];
    public string? UserFacilityNumber { get; init; }
    public string? LocalNumber { get; init; }
    public DateOnly? ReportDate { get; init; }
    public DateOnly? EventDate { get; init; }
    public DateOnly? AwarenessDate { get; init; }

    // A · patient
    public string? PatientIdentifier { get; init; }
    public decimal? PatientAge { get; init; }
    public string? PatientAgeUnit { get; init; }
    public string? PatientSex { get; init; }
    public decimal? PatientWeight { get; init; }
    public string? PatientWeightUnit { get; init; }

    // B · event
    public List<string> EventKinds { get; init; } = [];
    public List<string> Outcomes { get; init; } = [];
    public DateOnly? DeathDate { get; init; }
    public string? EventDescription { get; init; }
    public string? RelevantTests { get; init; }
    public string? OtherHistory { get; init; }

    // D · device
    public string? BrandName { get; init; }
    public string? CommonName { get; init; }
    public string? ProductCode { get; init; }
    public string? ManufacturerName { get; init; }
    public string? ManufacturerAddress { get; init; }
    public string? ModelNumber { get; init; }
    public string? CatalogNumber { get; init; }
    public string? SerialNumber { get; init; }
    public string? LotNumber { get; init; }
    public DateOnly? ExpirationDate { get; init; }
    public string? Udi { get; init; }
    public string? DeviceOperator { get; init; }
    public DateOnly? ImplantDate { get; init; }
    public DateOnly? ExplantDate { get; init; }
    public bool? Reprocessed { get; init; }
    public string? DeviceAvailable { get; init; }
    public DateOnly? ReturnedOn { get; init; }
    public string? ConcomitantProducts { get; init; }

    // E · initial reporter
    public string? ReporterName { get; init; }
    public string? ReporterAddress { get; init; }
    public string? ReporterPhone { get; init; }
    public string? ReporterEmail { get; init; }
    public string? ReporterCountry { get; init; }
    public bool? ReporterIsHcp { get; init; }
    public string? ReporterOccupation { get; init; }
    public string? ReporterSentToFda { get; init; }

    // G · all manufacturers
    public string? ContactName { get; init; }
    public string? ContactAddress { get; init; }
    public string? ContactPhone { get; init; }
    public List<string> ReportSources { get; init; } = [];
    public string? PremarketNumber { get; init; }
    public bool? CombinationProduct { get; init; }

    // H · device manufacturers
    public string? EventType { get; init; }
    public string? Evaluated { get; init; }
    public string? EvaluationSummary { get; init; }
    public DateOnly? ManufactureDate { get; init; }
    public bool? SingleUseLabeled { get; init; }
    public List<string> RemedialActions { get; init; } = [];
    public string? Usage { get; init; }
    public string? CorrectionNumber { get; init; }
    public string? ManufacturerNarrative { get; init; }
    public List<(string Annex, string Code, string? Term)> Codes { get; init; } = [];

    public static EmdrReport From(RecordSnapshot s)
    {
        var m = s.Data.Main;
        string? Kw(string k) => s.KeywordKey(k);
        List<string> Kws(string k) => s.KeywordKeys(k).ToList();
        string? Uf() => m.Text("uf_code") is { } c && m.Number("uf_year") is { } y && m.Number("uf_sequence") is { } q ? ReportNumberService.Format(c, (int)y, (int)q, true) : null;
        return new EmdrReport
        {
            ReportNumber = m.Text("report_number") ?? "", ReportType = Kw("report_type") ?? AerKeys.Initial, ReporterType = Kw("reporter_type") ?? AerKeys.Manufacturer,
            FollowUpNumber = m.Number("followup_number") is { } f ? (int)f : null, FollowUpTypes = Kws("followup_types"), ChangedSections = Kws("changed_sections"),
            UserFacilityNumber = Uf(), LocalNumber = s.Record.Number,
            ReportDate = m.Date("report_date"), EventDate = m.Date("event_date"), AwarenessDate = m.Date("awareness_date"),
            PatientIdentifier = m.Text("patient_identifier"), PatientAge = m.Number("patient_age"), PatientAgeUnit = Kw("patient_age_unit"),
            PatientSex = Kw("patient_sex"), PatientWeight = m.Number("patient_weight"), PatientWeightUnit = Kw("patient_weight_unit"),
            EventKinds = Kws("event_kind"), Outcomes = Kws("outcomes"), DeathDate = m.Date("death_date"),
            EventDescription = m.Text("event_description"), RelevantTests = m.Text("relevant_tests"), OtherHistory = m.Text("other_history"),
            BrandName = m.Text("brand_name"), CommonName = m.Text("common_name"), ProductCode = m.Text("product_code")?.Trim().ToUpperInvariant(),
            ManufacturerName = m.Text("mfr_name"), ManufacturerAddress = m.Text("mfr_address"), ModelNumber = m.Text("model_number"),
            CatalogNumber = m.Text("catalog_number"), SerialNumber = m.Text("serial_number"), LotNumber = m.Text("lot_number"),
            ExpirationDate = m.Date("expiration_date"), Udi = m.Text("udi"), DeviceOperator = Kw("device_operator"),
            ImplantDate = m.Date("implant_date"), ExplantDate = m.Date("explant_date"), Reprocessed = m.Bool("reprocessed"),
            DeviceAvailable = Kw("device_available"), ReturnedOn = m.Date("returned_on"), ConcomitantProducts = m.Text("concomitant_products"),
            ReporterName = m.Text("reporter_name"), ReporterAddress = m.Text("reporter_address"), ReporterPhone = m.Text("reporter_phone"),
            ReporterEmail = m.Text("reporter_email"), ReporterCountry = m.Text("reporter_country"), ReporterIsHcp = m.Bool("reporter_is_hcp"),
            ReporterOccupation = m.Text("reporter_occupation"), ReporterSentToFda = Kw("reporter_sent_to_fda"),
            ContactName = m.Text("contact_name"), ContactAddress = m.Text("contact_address"), ContactPhone = m.Text("contact_phone"),
            ReportSources = Kws("report_sources"), PremarketNumber = m.Text("premarket_number"), CombinationProduct = m.Bool("combination_product"),
            EventType = Kw("event_type"), Evaluated = Kw("evaluated"), EvaluationSummary = m.Text("evaluation_summary"),
            ManufactureDate = m.Date("manufacture_date"), SingleUseLabeled = m.Bool("single_use_labeled"), RemedialActions = Kws("remedial_actions"),
            Usage = Kw("usage"), CorrectionNumber = m.Text("correction_number"), ManufacturerNarrative = m.Text("mfr_narrative"),
            Codes = s.Rows("problem_codes").Select(r => (s.KeywordKey(r, "code_type") ?? "", r.Values.Text("code")?.Trim().ToUpperInvariant() ?? "", r.Values.Text("term")))
                .Where(c => c.Item1.Length > 0 && c.Item2.Length > 0).ToList(),
        };
    }
}

/// <param name="Production">The eMDR profile's account type: production messages are processed, test messages go to the FDA test system.</param>
public sealed record EmdrEnvelope(string MessageId, DateTime CreatedUtc, string SenderRoutingId, string ReceiverRoutingId, bool Production,
    string SenderOrganization, string? SenderEmail, string Center);

/// <summary>
/// Builds the FDA eMDR submission file: an HL7 v3 ICSR message (interaction PORR_IN049016UV, namespace urn:hl7-org:v3).
/// The transmission wrapper, control act and investigation event follow the ICSR structure; the 3500A content is carried as
/// coded observations grouped by form section (A, B, D, E, G, H), each tagged with its 3500A item number, and IMDRF codes as
/// coded values. Mapping covers every 3500A field Vellum captures. The FDA's own validation (ACK3) is the final authority:
/// test against the FDA ESG test account whenever the FDA publishes a new eMDR schema (AER-006).
/// </summary>
public static class EmdrXmlGenerator
{
    public static readonly XNamespace V3 = "urn:hl7-org:v3";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    public const string Interaction = "PORR_IN049016UV";
    public const string Item = "FDA 3500A";

    public static byte[] Generate(EmdrReport r, EmdrEnvelope env)
    {
        var doc = Build(r, env);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings)) doc.Save(w);
        return ms.ToArray();
    }

    public static XDocument Build(EmdrReport r, EmdrEnvelope env)
    {
        var created = Ts(env.CreatedUtc);
        var root = new XElement(V3 + Interaction,
            new XAttribute("ITSVersion", "XML_1.0"),
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XElement(V3 + "id", new XAttribute("root", Root(env.MessageId)), new XAttribute("extension", env.MessageId)),
            new XElement(V3 + "creationTime", new XAttribute("value", created)),
            new XElement(V3 + "interactionId", new XAttribute("root", "2.16.840.1.113883.1.6"), new XAttribute("extension", Interaction)),
            new XElement(V3 + "processingCode", new XAttribute("code", env.Production ? "P" : "T")),
            new XElement(V3 + "processingModeCode", new XAttribute("code", "T")),
            new XElement(V3 + "acceptAckCode", new XAttribute("code", "AL")),
            Device("receiver", "RCV", env.ReceiverRoutingId, env.Center),
            Device("sender", "SND", env.SenderRoutingId, env.SenderOrganization, env.SenderEmail),
            new XElement(V3 + "controlActProcess", new XAttribute("classCode", "CACT"), new XAttribute("moodCode", "EVN"),
                new XElement(V3 + "code", new XAttribute("code", "PORR_TE049016UV"), new XAttribute("codeSystem", "2.16.840.1.113883.1.18")),
                new XElement(V3 + "effectiveTime", new XAttribute("value", created)),
                new XElement(V3 + "subject", new XAttribute("typeCode", "SUBJ"), Investigation(r))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XElement Investigation(EmdrReport r)
    {
        var inv = new XElement(V3 + "investigationEvent", new XAttribute("classCode", "INVSTG"), new XAttribute("moodCode", "EVN"),
            new XElement(V3 + "id", new XAttribute("extension", r.ReportNumber),
                new XAttribute("assigningAuthorityName", r.ReporterType == AerKeys.Importer ? "Importer report number" : "Manufacturer report number")),
            Coded("code", r.ReportType, ReportTypeName(r.ReportType)),
            Text("text", r.EventDescription),
            new XElement(V3 + "statusCode", new XAttribute("code", "active")),
            r.EventDate is { } ev ? new XElement(V3 + "effectiveTime", new XElement(V3 + "low", new XAttribute("value", Day(ev)))) : null,
            r.ReportDate is { } rd ? new XElement(V3 + "availabilityTime", new XAttribute("value", Day(rd))) : null);

        // Report characteristics (G7, H2): initial / 5-day / follow-up and follow-up number.
        inv.Add(Characteristic("G7", "Type of report", r.ReportType, ReportTypeName(r.ReportType)));
        if (r.FollowUpNumber is { } fu) inv.Add(Characteristic("G7.FU", "Follow-up number", fu.ToString(CultureInfo.InvariantCulture), null, "INT"));
        if (r.LocalNumber != null) inv.Add(Characteristic("LOCAL", "Sender's local record number", r.LocalNumber, null, "ST"));
        if (r.UserFacilityNumber != null) inv.Add(Characteristic("F2", "User facility / importer report number", r.UserFacilityNumber, null, "ST"));

        inv.Add(Section("A", "Patient information",
            Obs("A1", "Patient identifier", St(r.PatientIdentifier)),
            Obs("A2", "Age", Pq(r.PatientAge, r.PatientAgeUnit switch { "months" => "mo", "weeks" => "wk", "days" => "d", null => null, _ => "a" })),
            Obs("A3", "Sex", Ce(r.PatientSex switch { "female" => "F", "male" => "M", "unknown" => "UN", _ => null }, r.PatientSex)),
            Obs("A4", "Weight", Pq(r.PatientWeight, r.PatientWeightUnit == "lb" ? "[lb_av]" : r.PatientWeight != null ? "kg" : null))));

        inv.Add(Section("B", "Adverse event or product problem",
            Many("B1", "Type of report", r.EventKinds),
            Many("B2", "Outcome attributed to the event", r.Outcomes),
            Obs("B2.D", "Date of death", Date(r.DeathDate)),
            Obs("B3", "Date of event", Date(r.EventDate)),
            Obs("B4", "Date of this report", Date(r.ReportDate)),
            Obs("B5", "Describe event or problem", Ed(r.EventDescription)),
            Obs("B6", "Relevant tests / laboratory data", Ed(r.RelevantTests)),
            Obs("B7", "Other relevant history", Ed(r.OtherHistory))));

        inv.Add(Section("D", "Suspect medical device",
            Obs("D1", "Brand name", St(r.BrandName)),
            Obs("D2", "Common device name", St(r.CommonName)),
            Obs("D2b", "Product code", Ce(r.ProductCode, r.CommonName, "FDA product code")),
            Obs("D3", "Manufacturer name", St(r.ManufacturerName)),
            Obs("D3.A", "Manufacturer address", St(r.ManufacturerAddress)),
            Obs("D4.M", "Model number", St(r.ModelNumber)),
            Obs("D4.C", "Catalog number", St(r.CatalogNumber)),
            Obs("D4.S", "Serial number", St(r.SerialNumber)),
            Obs("D4.L", "Lot number", St(r.LotNumber)),
            Obs("D4.E", "Expiration date", Date(r.ExpirationDate)),
            Obs("D4.UDI", "Unique device identifier", St(r.Udi)),
            Obs("D5", "Operator of device", Ce(r.DeviceOperator, r.DeviceOperator)),
            Obs("D6.I", "Implant date", Date(r.ImplantDate)),
            Obs("D6.E", "Explant date", Date(r.ExplantDate)),
            Obs("D7", "Single-use device reprocessed and reused", Bl(r.Reprocessed)),
            Obs("D9", "Device available for evaluation", Ce(r.DeviceAvailable, r.DeviceAvailable)),
            Obs("D9.R", "Returned to manufacturer on", Date(r.ReturnedOn)),
            Obs("D10", "Concomitant medical products", Ed(r.ConcomitantProducts))));

        inv.Add(Section("E", "Initial reporter",
            Obs("E1.N", "Name", St(r.ReporterName)),
            Obs("E1.A", "Address", St(r.ReporterAddress)),
            Obs("E1.P", "Phone", St(r.ReporterPhone)),
            Obs("E1.E", "Email", St(r.ReporterEmail)),
            Obs("E1.C", "Country", St(r.ReporterCountry)),
            Obs("E2", "Health professional", Bl(r.ReporterIsHcp)),
            Obs("E3", "Occupation", St(r.ReporterOccupation)),
            Obs("E4", "Initial reporter also sent report to FDA", Ce(r.ReporterSentToFda, r.ReporterSentToFda))));

        inv.Add(Section("G", "All manufacturers",
            Obs("G1.N", "Contact office name", St(r.ContactName)),
            Obs("G1.A", "Contact office address", St(r.ContactAddress)),
            Obs("G2", "Phone number", St(r.ContactPhone)),
            Many("G3", "Report source", r.ReportSources),
            Obs("G4", "Date received by manufacturer", Date(r.AwarenessDate)),
            Obs("G5", "PMA / 510(k) number", St(r.PremarketNumber)),
            Obs("G5.C", "Combination product", Bl(r.CombinationProduct)),
            Obs("G9", r.ReporterType == AerKeys.Importer ? "Importer report number" : "Manufacturer report number", St(r.ReportNumber))));

        var h = Section("H", "Device manufacturers only",
            Obs("H1", "Type of reportable event", Ce(r.EventType, r.EventType)),
            Many("H2", "If follow-up, what type", r.FollowUpTypes),
            Obs("H3", "Device evaluated by manufacturer", Ce(r.Evaluated, r.Evaluated)),
            Obs("H3.S", "Evaluation summary", Ed(r.EvaluationSummary)),
            Obs("H4", "Device manufacture date", Date(r.ManufactureDate)),
            Obs("H5", "Labeled for single use", Bl(r.SingleUseLabeled)),
            Many("H7", "Remedial action initiated", r.RemedialActions),
            Obs("H8", "Usage of device", Ce(r.Usage, r.Usage)),
            Obs("H9", "Correction / removal reporting number", St(r.CorrectionNumber)),
            Obs("H10", "Additional manufacturer narrative", Ed(r.ManufacturerNarrative)),
            Many("FU.S", "Sections changed by this follow-up", r.ChangedSections));
        foreach (var (annex, code, term) in r.Codes)
            h.Add(Component(new XElement(V3 + "observation", new XAttribute("classCode", "OBS"), new XAttribute("moodCode", "EVN"),
                new XElement(V3 + "code", new XAttribute("code", "H6"), new XAttribute("codeSystemName", Item), new XAttribute("displayName", "IMDRF " + AnnexName(annex))),
                Typed("value", "CE", new XAttribute("code", Clean(code)), new XAttribute("codeSystemName", "IMDRF " + AnnexName(annex)),
                    term != null ? new XAttribute("displayName", Clean(term)) : null))));
        inv.Add(h);
        return inv;
    }

    public static string ReportTypeName(string type) => type switch
    {
        AerKeys.FiveDay => "5-day report", AerKeys.Supplemental => "Follow-up (supplemental) report", _ => "Initial report (30-day)",
    };

    public static string AnnexName(string annex) => annex switch
    {
        "device_problem" => "Annex A", "evaluation_method" => "Annex B", "evaluation_result" => "Annex C", "evaluation_conclusion" => "Annex D",
        "health_effect" => "Annex E", "health_impact" => "Annex F", "component" => "Annex G", _ => annex,
    };

    // ------------------------------------------------------------------ element helpers

    private static XElement Device(string name, string typeCode, string routingId, string? display, string? email = null) =>
        new(V3 + name, new XAttribute("typeCode", typeCode),
            new XElement(V3 + "device", new XAttribute("classCode", "DEV"), new XAttribute("determinerCode", "INSTANCE"),
                new XElement(V3 + "id", new XAttribute("extension", Clean(routingId))),
                display != null ? new XElement(V3 + "name", Clean(display)) : null,
                email != null ? new XElement(V3 + "telecom", new XAttribute("value", "mailto:" + Clean(email))) : null));

    private static XElement Characteristic(string item, string name, string value, string? display, string type = "CE") =>
        new(V3 + "subjectOf2", new XAttribute("typeCode", "SUBJ"),
            new XElement(V3 + "investigationCharacteristic", new XAttribute("classCode", "OBS"), new XAttribute("moodCode", "EVN"),
                new XElement(V3 + "code", new XAttribute("code", item), new XAttribute("codeSystemName", Item), new XAttribute("displayName", name)),
                type == "CE"
                    ? Typed("value", "CE", new XAttribute("code", Clean(value)), display != null ? new XAttribute("displayName", Clean(display)) : null)
                    : type == "INT" ? Typed("value", "INT", new XAttribute("value", Clean(value))) : Typed("value", "ST", Clean(value))));

    private static XElement Section(string letter, string title, params object?[] observations)
    {
        var org = new XElement(V3 + "organizer", new XAttribute("classCode", "CLUSTER"), new XAttribute("moodCode", "EVN"),
            new XElement(V3 + "code", new XAttribute("code", letter), new XAttribute("codeSystemName", Item), new XAttribute("displayName", title)));
        foreach (var o in observations.SelectMany(o => o is IEnumerable<XElement> many ? many : o is XElement e ? [e] : Array.Empty<XElement>()))
            org.Add(Component(o));
        return new XElement(V3 + "component", new XAttribute("typeCode", "COMP"), org);
    }

    private static XElement Component(XElement content) => new(V3 + "component", new XAttribute("typeCode", "COMP"), content);

    private static XElement? Obs(string item, string name, XElement? value) => value == null ? null :
        new XElement(V3 + "observation", new XAttribute("classCode", "OBS"), new XAttribute("moodCode", "EVN"),
            new XElement(V3 + "code", new XAttribute("code", item), new XAttribute("codeSystemName", Item), new XAttribute("displayName", name)), value);

    private static IEnumerable<XElement> Many(string item, string name, IEnumerable<string> codes) =>
        codes.Select(c => Obs(item, name, Ce(c, c))).OfType<XElement>();

    private static XElement Typed(string name, string type, params object?[] content) =>
        new(V3 + name, new XAttribute(Xsi + "type", type), content);

    private static XElement? St(string? v) => string.IsNullOrWhiteSpace(v) ? null : Typed("value", "ST", Clean(v.Trim()));
    private static XElement? Ed(string? v) => string.IsNullOrWhiteSpace(v) ? null : Typed("value", "ED", new XAttribute("mediaType", "text/plain"), Clean(v.Trim()));
    private static XElement? Bl(bool? v) => v == null ? null : Typed("value", "BL", new XAttribute("value", v.Value ? "true" : "false"));
    private static XElement? Date(DateOnly? d) => d == null ? null : Typed("value", "TS", new XAttribute("value", Day(d.Value)));
    private static XElement? Pq(decimal? v, string? unit) => v == null ? null :
        Typed("value", "PQ", new XAttribute("value", v.Value.ToString("G29", CultureInfo.InvariantCulture)), unit != null ? new XAttribute("unit", unit) : null);
    private static XElement? Ce(string? code, string? display, string? system = null) => string.IsNullOrWhiteSpace(code) ? null :
        Typed("value", "CE", new XAttribute("code", Clean(code)), display != null ? new XAttribute("displayName", Clean(display)) : null,
            system != null ? new XAttribute("codeSystemName", system) : null);

    private static XElement Coded(string name, string code, string display) =>
        new(V3 + name, new XAttribute("code", Clean(code)), new XAttribute("codeSystemName", Item), new XAttribute("displayName", display));

    private static XElement? Text(string name, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new XElement(V3 + name, new XAttribute("mediaType", "text/plain"), Clean(text.Trim()));

    private static string Ts(DateTime utc) => utc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "+0000";
    private static string Day(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>A UUID root for the message id (HL7 accepts UUIDs as instance identifiers).</summary>
    private static string Root(string messageId) =>
        Guid.TryParse(messageId.Split('-', 2).Length == 2 ? messageId.Split('-', 2)[1] : messageId, out var g) ? g.ToString() : Guid.NewGuid().ToString();

    /// <summary>Drop characters XML 1.0 can't carry (control characters pasted from other systems).</summary>
    public static string Clean(string s)
    {
        if (s.All(XmlConvert.IsXmlChar)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch).Append(s[++i]); continue; }
            if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }
}
