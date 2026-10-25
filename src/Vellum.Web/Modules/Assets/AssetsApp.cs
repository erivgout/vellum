using Vellum.Domain;
using Vellum.Modules.Kit;
using Vellum.Packaging;

namespace Vellum.Modules.Assets;

/// <summary>
/// Assets, Calibration and Maintenance (AST §18): asset register with unique IDs (INV-022), calibration with as-found/as-left
/// readings evaluated per AST-022 (INV-064), standards that must be in calibration (INV-115), as-found failures that need a
/// CAPA or a justified bypass (INV-116), quarantine (SAF-040) and maintenance records.
/// </summary>
public sealed class AssetsApp : IPackagedApp
{
    public string Key => "assets";
    public int Order => 85;

    public void Define(AppBuilder app)
    {
        app.Name("Assets & Calibration")
            .Description("Register measuring and production equipment, calibrate it against traceable standards and keep maintenance records.")
            .Icon("bi-speedometer2").Color("#0891b2").Suite("Operations").Order(10).Version("1.0.0")
            .DefaultAccess(AppRole.Reader)
            .Grant("@maintenance", AppRole.Author)
            .Grant("@quality", AppRole.Author)
            .Grant("ebaker", AppRole.Author)
            .Grant("lgarcia", AppRole.Manager, designer: true);

        Sites.Define(app);
        app.Keywords("asset_type", "Asset type", k => k
            .Add("torque_driver", "Torque driver").Add("caliper", "Caliper").Add("micrometer", "Micrometer").Add("balance", "Balance / scale")
            .Add("pressure_gauge", "Pressure gauge").Add("dp_gauge", "Differential pressure gauge").Add("thermohygrometer", "Thermometer / thermohygrometer")
            .Add("torque_standard", "Torque transducer (standard)").Add("gauge_blocks", "Gauge block set").Add("weights", "Calibration weights")
            .Add("pressure_calibrator", "Pressure calibrator").Add("production", "Production equipment").Add("hvac", "HVAC / air handling"));
        app.Keywords("asset_status", "Asset status", k => k
            .Add("in_service", "In service", "success").Add("due", "Calibration due", "warning").Add("overdue", "Overdue", "danger")
            .Add("quarantined", "Quarantined", "dark").Add("maintenance", "Under maintenance", "info").Add("retired", "Retired", "secondary"));
        app.Keywords("cal_result", "Calibration result", k => k.Add("pass", "Pass", "success").Add("fail", "Fail", "danger"));
        app.Keywords("cal_type", "Calibration type", k => k.Add("internal", "In-house").Add("external", "External vendor"));
        app.Keywords("maintenance_type", "Maintenance type", k => k
            .Add("preventive", "Preventive", "info").Add("corrective", "Corrective", "warning").Add("predictive", "Predictive", "violet"));
        app.Keywords("task_result", "Task result", k => k
            .Add("ok", "OK", "success").Add("adjusted", "Adjusted", "info").Add("replaced", "Replaced", "info").Add("not_ok", "Not OK", "danger"));
        app.Keywords("maintenance_result", "Maintenance result", k => k
            .Add("ok", "Returned to service", "success").Add("follow_up", "Returned, follow-up needed", "warning").Add("failed", "Failed: quarantine the asset", "danger"));

        app.Form("asset", "Asset", f => f
            .Icon("bi-speedometer2")
            .Description("A measuring instrument, standard or piece of equipment with its calibration and maintenance schedule.")
            .Numbering("AST-")
            .NonWorkflow()
            .Title("{asset_id} · {description}")
            .Section("info", "Asset information", s =>
            {
                s.Text("asset_id", "Asset ID", 3).Required().Unique().MaxLength(40).Header().Placeholder("e.g. KZO-TD-014")
                    .Help("The tag number on the equipment. Unique across every site (INV-022) and fixed once the asset is registered; use Copy profile for a similar asset.");
                s.Text("description", "Description", 9).Required().MaxLength(200).Placeholder("e.g. Electronic torque driver 0.5–3.0 N·m, final assembly station 3");
                s.Keyword("asset_type", "Type", "asset_type").Required().Header();
                s.Keyword("site", "Site", "ops_site").Required().Header();
                s.Text("location", "Location / line", 4).Placeholder("Final assembly, station 3");
                s.Text("manufacturer", "Manufacturer", 4);
                s.Text("model", "Model", 4);
                s.Text("serial_number", "Serial number", 4);
                s.User("owner", "Owner").Required().Header().Help("Responsible for the asset day to day. Notified when calibration is due or the asset is quarantined.");
                s.Keyword("status", "Status", "asset_status").ReadOnly().Header()
                    .Help("Maintained by Vellum: due and overdue from the calibration schedule, quarantined after a failed calibration or by the Quarantine action (SAF-040).");
                s.TextArea("quarantine_reason", "Quarantine reason").ReadOnly().VisibleWhen("status=quarantined");
            }, icon: "bi-tag")
            .Section("calibration", "Calibration schedule", s =>
            {
                s.YesNo("calibration_required", "Calibration required?").Default("yes");
                s.Number("cal_interval_days", "Interval (days)", 2).Min(1).Max(3650).Decimals(0).VisibleWhen("calibration_required=yes");
                s.User("technician", "Responsible technician")
                    .Help("The schedule assignee. Only this person or an application manager can record calibrations and maintenance (INV-087).");
                s.Text("cal_vendor", "External calibration vendor", 4).VisibleWhen("calibration_required=yes");
                s.Date("last_cal_date", "Last calibrated").ReadOnly();
                s.Date("next_cal_date", "Next calibration due").ReadOnly().Header();
                s.Keyword("last_cal_result", "Last as-left result", "cal_result", 3).ReadOnly();
                s.YesNo("use_as_standard", "Use as a calibration standard?").Default("no")
                    .Help("Standards must themselves be in calibration and not quarantined on the date they're used (INV-115).");
                s.Text("traceability", "Traceability", 6).VisibleWhen("use_as_standard=yes").Placeholder("NIST-traceable certificate no. / accredited lab");
                s.Text("accuracy", "Accuracy class", 3).VisibleWhen("use_as_standard=yes");
            }, icon: "bi-calendar2-check")
            .Subform("characteristics", "Characteristics", s =>
            {
                s.Text("characteristic", "Characteristic", 4).Required();
                s.Text("unit", "Unit", 2);
                s.Number("nominal", "Nominal", 2).Required();
                s.Number("tol_minus", "− Tolerance", 2).Required().Min(0);
                s.Number("tol_plus", "+ Tolerance", 2).Required().Min(0);
            }, rowNoun: "characteristic", icon: "bi-rulers",
               description: "What is checked at each calibration: the nominal value and its tolerances (AST-011). Each calibration starts with one reading per characteristic.")
            .Section("maintenance", "Maintenance schedule", s =>
            {
                s.YesNo("pm_required", "Preventive maintenance?").Default("no");
                s.Number("pm_interval_days", "PM interval (days)", 3).Min(1).Max(3650).Decimals(0).VisibleWhen("pm_required=yes");
                s.Date("last_pm_date", "Last preventive maintenance").ReadOnly();
                s.Date("next_pm_date", "Next preventive maintenance").ReadOnly();
            }, icon: "bi-wrench-adjustable"));

        app.Form("calibration", "Calibration", f => f
            .Icon("bi-bullseye")
            .Description("As-found and as-left readings of an asset against its tolerances, with the standards used.")
            .Numbering("CAL-")
            .Activity("asset")
            .Title("Calibration of {asset_ref}")
            .Section("cal", "Calibration", s =>
            {
                s.Text("asset_ref", "Asset", 6).ReadOnly();
                s.Date("cal_date", "Calibration date").Required().Header().Default("$today");
                s.User("technician", "Performed by").Required().Header().Default("$me");
                s.Keyword("cal_type", "Calibration type", "cal_type").Required().Default("internal");
                s.Text("vendor", "Vendor", 4).VisibleWhen("cal_type=external");
                s.Text("certificate_no", "Certificate number", 4);
                s.Link("standards", "Standards used", "asset").Help("Every standard must be in calibration and not quarantined on the calibration date (INV-115).");
                s.Text("procedure", "Procedure / work instruction", 6).Placeholder("e.g. CP-012 Torque driver calibration rev D");
                s.Number("temperature_c", "Temperature (°C)", 3).Decimals(1);
                s.Number("humidity_pct", "Relative humidity (%)", 3).Min(0).Max(100).Decimals(0);
            }, icon: "bi-bullseye")
            .Subform("readings", "As-found and as-left readings", s =>
            {
                s.Text("characteristic", "Characteristic", 2).Required();
                s.Text("unit", "Unit", 1);
                s.Number("nominal", "Nominal", 1).Required();
                s.Number("tol_minus", "− Tol.", 1).Required().Min(0);
                s.Number("tol_plus", "+ Tol.", 1).Required().Min(0);
                s.Number("found", "As found", 1);
                s.Number("found_dev", "Deviation", 1).ReadOnly();
                s.Keyword("found_result", "Result", "cal_result", 1).ReadOnly();
                s.Number("left", "As left", 1);
                s.Number("left_dev", "Deviation", 1).ReadOnly();
                s.Keyword("left_result", "Result", "cal_result", 1).ReadOnly();
            }, rowNoun: "reading", required: true, icon: "bi-table",
               description: "Deviation and pass/fail are calculated when you save, exactly as AST-022 defines them. As-left is the reading after any adjustment: repeat the as-found value if nothing was adjusted.")
            .Section("results", "Results", s =>
            {
                s.Keyword("as_found_result", "As-found result", "cal_result").ReadOnly().Header();
                s.Keyword("as_left_result", "As-left result", "cal_result").ReadOnly().Header();
                s.TextArea("adjustments", "Adjustments made");
                s.TextArea("comments", "Comments");
            }, icon: "bi-clipboard-data")
            .Section("failure", "As-found failure: impact and corrective action", s =>
            {
                s.Prompt("failure_prompt", "An as-found failure means measurements made with this asset since its last good calibration may be wrong. Review the measurement impact, then link a CAPA or record why no corrective action is needed (INV-116, SAF-040).");
                s.TextArea("impact_assessment", "Measurement impact assessment").Help("Which products, inspections or calibrations relied on this asset, and what you concluded.");
                s.Link("capa_link", "Corrective action (CAPA)", "capa");
                s.YesNo("bypass_capa", "Bypass corrective action?").Default("no");
                s.TextArea("bypass_reason", "Reason for bypassing corrective action").VisibleWhen("bypass_capa=yes");
            }, icon: "bi-exclamation-triangle", description: "Needed only when an as-found reading fails.")
            .Workflow("calibration", "Calibration", w =>
            {
                w.Start("perform", "Perform").Purpose("Enter the standards and conditions, then the as-found and as-left readings. Results are calculated when you save.")
                    .DueDays(5).Edit("cal", "readings", "results", "failure");
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("perform", "completed", label: "Complete calibration", sign: "Calibration performed");
                w.Void("perform");
            }));

        app.Form("maintenance", "Maintenance", f => f
            .Icon("bi-wrench-adjustable")
            .Description("Preventive, predictive or corrective maintenance on an asset, with tasks and parts used.")
            .Numbering("MNT-")
            .Activity("asset")
            .Title("{maintenance_type} maintenance of {asset_ref}")
            .Section("work", "Maintenance", s =>
            {
                s.Text("asset_ref", "Asset", 6).ReadOnly();
                s.Keyword("maintenance_type", "Type", "maintenance_type").Required().Header();
                s.Date("performed_on", "Performed on").Required().Default("$today");
                s.User("technician", "Technician").Required().Default("$me").Header();
                s.TextArea("work_performed", "Work performed").RequiredIn("perform");
                s.Number("downtime_hours", "Downtime (hours)", 3).Min(0).Decimals(1);
                s.Date("returned_to_service", "Returned to service").RequiredIn("perform");
                s.Keyword("result", "Result", "maintenance_result").RequiredIn("perform").Header();
            }, icon: "bi-wrench-adjustable")
            .Subform("tasks", "Tasks", s =>
            {
                s.Text("task", "Task", 6).Required();
                s.Keyword("task_result", "Result", "task_result", 2);
                s.Text("notes", "Notes", 4);
            }, rowNoun: "task", icon: "bi-list-check")
            .Subform("parts", "Parts used", s =>
            {
                s.Text("part", "Part", 6).Required();
                s.Text("part_no", "Part number", 3);
                s.Number("qty", "Quantity", 3).Required().Min(1).Decimals(0);
            }, rowNoun: "part", icon: "bi-box-seam", description: "Optional. Spare-part stock isn't deducted in this release.")
            .Workflow("maintenance", "Maintenance", w =>
            {
                w.Start("perform", "Perform").Purpose("Carry out the work, record each task and the parts used, and when the asset went back into service.")
                    .DueDays(5).Edit("work", "tasks", "parts");
                w.Completed("completed", "Completed");
                w.Voided();
                w.Forward("perform", "completed", label: "Complete maintenance", sign: "Maintenance performed");
                w.Void("perform");
            }));

        app.View("all", "All assets", v => v.Category("1. Assets").Forms("asset").All()
            .Columns("number", "title", "asset_type", "site", "status", "next_cal_date", "owner").Sort("title", false).Default());
        app.View("due", "Calibration due", v => v.Category("1. Assets").Forms("asset").All().Filter("status", "eq", "due")
            .Columns("number", "title", "site", "next_cal_date", "technician", "owner").Sort("next_cal_date", false));
        app.View("overdue", "Calibration overdue", v => v.Category("1. Assets").Forms("asset").All().Filter("status", "eq", "overdue")
            .Columns("number", "title", "site", "next_cal_date", "technician", "owner").Sort("next_cal_date", false));
        app.View("quarantined", "Quarantined", v => v.Category("1. Assets").Forms("asset").All().Filter("status", "eq", "quarantined")
            .Columns("number", "title", "site", "quarantine_reason", "owner", "modified"));
        app.View("standards", "Calibration standards", v => v.Category("1. Assets").Forms("asset").All().Filter("use_as_standard", "eq", "yes")
            .Columns("number", "title", "status", "traceability", "next_cal_date"));
        app.View("retired", "Retired assets", v => v.Category("1. Assets").Forms("asset").All().Filter("status", "eq", "retired")
            .Columns("number", "title", "site", "modified"));
        app.View("cal_open", "Calibrations in progress", v => v.Category("2. Calibration").Forms("calibration").Open()
            .Columns("number", "title", "cal_date", "technician", "due"));
        app.View("cal_results", "Calibrations by result", v => v.Category("2. Calibration").Forms("calibration").Completed()
            .Columns("number", "title", "cal_date", "as_found_result", "as_left_result", "technician").Sort("as_found_result", false));
        app.View("cal_failures", "As-found failures", v => v.Category("2. Calibration").Forms("calibration").All().Filter("as_found_result", "eq", "fail")
            .Columns("number", "title", "cal_date", "as_left_result", "capa_link", "bypass_capa", "phase").Sort("cal_date"));
        app.View("cal_completed", "Completed calibrations", v => v.Category("2. Calibration").Forms("calibration").Completed()
            .Columns("number", "title", "cal_date", "as_found_result", "as_left_result", "certificate_no").Sort("cal_date"));
        app.View("mnt_open", "Maintenance in progress", v => v.Category("3. Maintenance").Forms("maintenance").Open()
            .Columns("number", "title", "maintenance_type", "performed_on", "technician", "due"));
        app.View("mnt_completed", "Completed maintenance", v => v.Category("3. Maintenance").Forms("maintenance").Completed()
            .Columns("number", "title", "maintenance_type", "returned_to_service", "result", "downtime_hours").Sort("returned_to_service"));
    }

    public Task SeedDemoAsync(DemoContext demo) => AssetsDemo.SeedAsync(demo);
}
