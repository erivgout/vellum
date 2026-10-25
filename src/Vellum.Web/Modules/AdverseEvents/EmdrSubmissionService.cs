using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Security;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;

namespace Vellum.Modules.AdverseEvents;

/// <summary>
/// AER-005 and AER-008..010: assigns the report number and generates, sends and tracks the eMDR submission of a MedWatch.
/// Acknowledgements become linked eMDR Acknowledgement records. Rejections, failures, overdue acknowledgements and deadlines passed
/// with a submission pending raise safety-critical alerts and an urgent action item for regulatory affairs (SAF-020).
/// </summary>
[ScopedService]
public sealed class EmdrSubmissionService(VellumDbContext db, ConfigCache cfg, RecordService records, AuditService audit, NotificationService notify,
    UserDirectory dir, CurrentUser me, EmdrGateways gateways, EmdrProfileService profiles, AerSetup setup, ReportNumberService numbers, AerWriteGuard guard)
{
    private ConfigModel Cfg => cfg.Current;

    public Task<EmdrSubmission?> CurrentAsync(Guid medWatchId) =>
        db.Set<EmdrSubmission>().Where(s => s.MedWatchId == medWatchId && !s.Superseded).OrderByDescending(s => s.Attempt).FirstOrDefaultAsync();

    public Task<List<EmdrSubmission>> HistoryAsync(Guid medWatchId) =>
        db.Set<EmdrSubmission>().AsNoTracking().Where(s => s.MedWatchId == medWatchId).OrderByDescending(s => s.Attempt).ToListAsync();

    public static (string Code, string Year, string Seq) NumberFields(string reporterType) => reporterType == AerKeys.Importer
        ? ("importer_code", "importer_year", "importer_sequence") : ("mfr_code", "mfr_year", "mfr_sequence");

    // ------------------------------------------------------------------ AER-005 report numbers

    /// <summary>
    /// On entry to Submitted to FDA: with mapping on, fill in the code (basic or by rule) and year, and draw the next sequential number;
    /// with mapping off, or a manual sequence, use what was entered. A number, once issued, stays with the report (resubmissions keep it).
    /// </summary>
    public async Task<string> AssignNumberAsync(RecordSnapshot s)
    {
        var id = s.Record.Id;
        var row = await db.Set<MedWatchReport>().FirstOrDefaultAsync(x => x.MedWatchId == id)
                  ?? throw new WorkflowException($"{s.Record.Number} has no regulatory index entry.");
        var reporter = s.KeywordKey("reporter_type") ?? AerKeys.Manufacturer;
        var (codeKey, yearKey, seqKey) = NumberFields(reporter);
        if (row is { ReportCode: not null, ReportYear: not null, ReportSequence: not null })
            return ReportNumberService.Format(row.ReportCode, row.ReportYear.Value, row.ReportSequence.Value);

        var profile = await setup.ProfileAsync() ?? throw new WorkflowException("Set up the Adverse Event Reporting application profile first (AER-002).");
        var supplemental = s.KeywordKey("report_type") == AerKeys.Supplemental;
        string code;
        int year, seq;
        var generated = false;
        if (profile.MappingEnabled && !supplemental)
        {
            var location = s.KeywordKey("location");
            var mapping = await setup.MappingForAsync(location)
                          ?? throw new WorkflowException($"There's no MFR/importer mapping for {s.Keyword("location")?.Label ?? "the report's location"}.");
            code = mapping.CodeFor(reporter, s.Text("product_code")?.Trim().ToUpperInvariant()) is { } c && EmdrValidator.IsValidCode(c)
                ? c : throw new WorkflowException($"The mapping for {s.Keyword("location")?.Label} has no valid code.");
            year = BusinessCalendar.Today.Year;
            if (mapping.Sequential(reporter))
            {
                seq = await numbers.NextAsync(reporter, code, year, mapping.StartFor(reporter, year));
                generated = true;
            }
            else seq = s.Number(seqKey) is { } n ? (int)n : throw new WorkflowException("Enter the report sequence number (the mapping uses manual sequences).");
            using (guard.Allow())
            {
                var upd = await records.UpdateAsync(id, new() { [codeKey] = code, [yearKey] = (decimal)year, [seqKey] = (decimal)seq },
                    "Report number assigned on submission (AER-005)");
                if (!upd.Ok) throw new WorkflowException("Couldn't record the report number: " + string.Join("; ", upd.Errors.Select(e => e.Message)));
            }
        }
        else
        {
            code = s.Text(codeKey)?.Trim() ?? throw new WorkflowException("The report number code is missing.");
            year = s.Number(yearKey) is { } y ? (int)y : throw new WorkflowException("The report number year is missing.");
            seq = s.Number(seqKey) is { } q ? (int)q : throw new WorkflowException("The report number sequence is missing.");
        }

        var number = ReportNumberService.Format(code, year, seq);
        if (row.IsInitial && await db.Set<MedWatchReport>().AnyAsync(x => x.MedWatchId != id && x.IsInitial && x.ReporterType == reporter &&
                                                                         x.ReportCode == code && x.ReportYear == year && x.ReportSequence == seq))
            throw new WorkflowException($"Report number {number} has already been issued to another report. Report numbers are never reused (INV-023).");
        row.ReportCode = code;
        row.ReportYear = year;
        row.ReportSequence = seq;
        row.SequenceGenerated = generated;
        row.NumberAssignedAt = DateTime.UtcNow;
        audit.Record(id, "Report number assigned", $"{(reporter == AerKeys.Importer ? "Importer" : "MFR")} report number {number}" +
            (generated ? $" (next in the {year} sequence for code {code})" : supplemental ? " (carried from the original report)" : " (entered by hand)"),
            phaseName: s.Phase?.Name);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (Unique(ex))
        {
            throw new WorkflowException($"Report number {number} has already been issued to another report (INV-023).");
        }
        return number;
    }

    // ------------------------------------------------------------------ AER-008 generate and send

    public async Task SubmitAsync(RecordSnapshot s)
    {
        var id = s.Record.Id;
        var settings = await profiles.SettingsAsync();
        var profile = await profiles.CurrentAsync() ?? throw new WorkflowException("There's no eMDR profile for this environment. An administrator sets it up in Administration › eMDR gateway.");
        var gateway = gateways.Get(profile.Gateway) ?? throw new WorkflowException($"Unknown eMDR gateway '{profile.Gateway}'.");
        if (gateway.UnavailableReason(profile, settings.EnvironmentType) is { } why) throw new WorkflowException(why);

        var number = await AssignNumberAsync(s);
        var fresh = await records.LoadRequiredAsync(id);
        var now = DateTime.UtcNow;
        foreach (var old in await db.Set<EmdrSubmission>().Where(x => x.MedWatchId == id && !x.Superseded).ToListAsync()) old.Superseded = true;
        var attempt = (await db.Set<EmdrSubmission>().Where(x => x.MedWatchId == id).MaxAsync(x => (int?)x.Attempt) ?? 0) + 1;
        var defaults = EmdrProfileService.Defaults(profile.AccountType);
        var messageId = $"VLM-{Guid.NewGuid():N}";
        var envelope = new EmdrEnvelope(messageId, now, profile.SenderRoutingId, profile.FdaRoutingId ?? defaults.RoutingId, profile.AccountType == "production",
            profile.SenderOrganization, profile.SenderEmail, profile.SubmissionCenter ?? defaults.Center);
        var bytes = EmdrXmlGenerator.Generate(EmdrReport.From(fresh), envelope);
        var fileName = $"eMDR_{number}_A{attempt}_{now:yyyyMMddHHmmss}.xml";
        var att = await records.AddAttachmentAsync(fresh.Record, "submission_file", null, fileName, "application/xml", new MemoryStream(bytes));
        await db.SaveChangesAsync();

        var sub = new EmdrSubmission
        {
            MedWatchId = id, Attempt = attempt, Gateway = gateway.Key, MessageId = messageId, ReportNumber = number, FileName = fileName,
            FileSha256 = att.Sha256, AttachmentId = att.Id, FormVersion = EmdrSchema.FormVersion, SchemaVersion = EmdrSchema.SchemaVersion,
            RuleSetVersion = EmdrSchema.RuleSetVersion, AccountType = profile.AccountType, CreatedAt = now, CreatedById = me.Id,
            SimulatedOutcome = gateway.Key == "simulated" ? profile.SimulatedOutcome : null, Status = EmdrStatus.AwaitingUpload,
        };
        db.Set<EmdrSubmission>().Add(sub);
        audit.Record(id, "eMDR file generated", $"{fileName} ({bytes.Length:N0} bytes, SHA-256 {att.Sha256[..12]}…) · {EmdrSchema.SchemaVersion} · {EmdrSchema.RuleSetVersion}",
            phaseName: fresh.Phase?.Name);
        await db.SaveChangesAsync();

        GatewaySendResult result;
        try { result = await gateway.SendAsync(new EmdrOutbound(sub, bytes, profile)); }
        catch (Exception ex) { result = new GatewaySendResult(EmdrStatus.Failed, null, "The gateway couldn't send the file.", ex.Message); }
        sub.Status = result.Status;
        sub.SentAt = result.SentAt;
        sub.LastError = result.Error;
        await SetStatusAsync(id, result.Status switch { EmdrStatus.AwaitingUpload => "awaiting_upload", EmdrStatus.Sent => "awaiting_ack", _ => "rejected" },
            new() { ["gateway"] = gateway.Key, ["submitted_at"] = result.SentAt, ["ack1_at"] = null, ["ack2_at"] = null, ["ack3_at"] = null, ["confirm_final_ack"] = null },
            $"eMDR submission {attempt}");
        audit.Record(id, result.Status == EmdrStatus.Failed ? "eMDR submission failed" : "Sent to FDA gateway",
            $"{gateway.Name}, {profile.AccountType} account, message {messageId}: {result.Message}", phaseName: fresh.Phase?.Name);
        await db.SaveChangesAsync();
        if (result.Status == EmdrStatus.Failed) await RaiseFailureAsync(sub, result.Error ?? result.Message);
    }

    /// <summary>Manual gateway: the user uploaded the file with ESG WebTrader.</summary>
    public async Task<EmdrResult> RecordUploadAsync(Guid medWatchId, DateTime uploadedUtc, string? esgReference)
    {
        var sub = await CurrentAsync(medWatchId);
        if (sub == null || sub.Status != EmdrStatus.AwaitingUpload) return EmdrResult.Fail("There's no submission waiting to be uploaded.");
        if (uploadedUtc > DateTime.UtcNow.AddMinutes(5)) return EmdrResult.Fail("The upload time can't be in the future.");
        if (uploadedUtc < sub.CreatedAt.AddMinutes(-1)) return EmdrResult.Fail("The upload can't be before the file was generated.");
        return await Tx.RunAsync(db, async () =>
        {
            sub.Status = EmdrStatus.Sent;
            sub.SentAt = uploadedUtc;
            await SetStatusAsync(medWatchId, "awaiting_ack", new() { ["submitted_at"] = uploadedUtc }, "Uploaded with ESG WebTrader");
            audit.Record(medWatchId, "Uploaded to ESG WebTrader", $"{sub.FileName} uploaded {uploadedUtc:dd MMM yyyy HH:mm} UTC" +
                (string.IsNullOrWhiteSpace(esgReference) ? "" : $" · ESG reference {esgReference.Trim()}"));
            await db.SaveChangesAsync();
            return EmdrResult.Success("Upload recorded. Record ACK1, ACK2 and ACK3 as the FDA sends them.");
        }, r => r.Ok);
    }

    // ------------------------------------------------------------------ acknowledgements

    public async Task<EmdrResult> RecordAckAsync(Guid medWatchId, GatewayAck ack, string source)
    {
        var sub = await CurrentAsync(medWatchId);
        if (sub == null) return EmdrResult.Fail("This report hasn't been submitted.");
        if (sub.Status == EmdrStatus.AwaitingUpload) return EmdrResult.Fail("Record the ESG WebTrader upload first.");
        if (!EmdrStatus.IsPending(sub.Status)) return EmdrResult.Fail($"This submission is already {(sub.Status == EmdrStatus.Accepted ? "accepted" : "closed by an error")}.");
        var (have, prior) = ack.Type switch
        {
            "ack1" => (sub.Ack1At, (DateTime?)sub.SentAt),
            "ack2" => (sub.Ack2At, sub.Ack1At),
            "ack3" => (sub.Ack3At, sub.Ack2At),
            _ => throw new ArgumentException("Unknown acknowledgement type."),
        };
        var label = ack.Type.ToUpperInvariant();
        if (have != null) return EmdrResult.Fail($"{label} is already recorded.");
        if (prior == null) return EmdrResult.Fail($"{label} follows {(ack.Type == "ack1" ? "the upload" : "ACK" + (ack.Type[3] - '1'))}: record that first.");
        if (ack.ReceivedAt < prior.Value.AddMinutes(-1)) return EmdrResult.Fail($"{label} can't be earlier than the step before it.");
        if (ack.ReceivedAt > DateTime.UtcNow.AddMinutes(5)) return EmdrResult.Fail("The received time can't be in the future.");
        var snap = await records.LoadRequiredAsync(medWatchId);

        return await Tx.RunAsync(db, async () =>
        {
            var res = await records.CreateAsync(new CreateRequest
            {
                FormKey = AerKeys.Ack, ParentId = medWatchId, SkipAccessCheck = true,
                CreateComment = $"{label} received for message {sub.MessageId}",
                Values = new()
                {
                    ["ack_type"] = ack.Type, ["ack_status"] = ack.Accepted ? "accepted" : "rejected", ["ack_received_at"] = DateTime.SpecifyKind(ack.ReceivedAt, DateTimeKind.Utc),
                    ["ack_report_number"] = sub.ReportNumber, ["ack_reporter_type"] = snap.KeywordKey("reporter_type") ?? AerKeys.Manufacturer,
                    ["ack_message_id"] = sub.MessageId, ["ack_fda_reference"] = ack.FdaReference, ["ack_source"] = source, ["ack_detail"] = ack.Detail,
                },
            });
            if (!res.Ok) return EmdrResult.Fail(res.Errors.Select(e => e.Message).ToArray());
            switch (ack.Type)
            {
                case "ack1": sub.Ack1At = ack.ReceivedAt; break;
                case "ack2": sub.Ack2At = ack.ReceivedAt; break;
                default: sub.Ack3At = ack.ReceivedAt; break;
            }
            if (!ack.Accepted)
            {
                sub.Status = EmdrStatus.Rejected;
                sub.LastError = ack.Detail;
            }
            else if (ack.Type == "ack3") sub.Status = EmdrStatus.Accepted;
            var status = !ack.Accepted ? "rejected" : ack.Type == "ack3" ? "acknowledged" : sub.OverdueAlertedAt != null ? "overdue" : "awaiting_ack";
            await SetStatusAsync(medWatchId, status, new() { [ack.Type + "_at"] = DateTime.SpecifyKind(ack.ReceivedAt, DateTimeKind.Utc) }, $"{label} received");
            audit.Record(medWatchId, $"{label} received", $"{(ack.Accepted ? "Accepted" : "REJECTED")} · {res.Record!.Number} · {source}" +
                (ack.Detail != null ? $": {Trim(ack.Detail, 300)}" : ""));
            await db.SaveChangesAsync();

            if (!ack.Accepted) await RaiseFailureAsync(sub, ack.Detail ?? $"{label} reported an error.");
            else if (ack.Type == "ack3")
            {
                var owner = snap.UserId("owner") ?? snap.Record.CreatedById;
                notify.Notify(owner, NotificationKind.Info, $"{snap.Record.Number} accepted by the FDA",
                    $"ACK3 accepted report {sub.ReportNumber}. Confirm the final acknowledgement and complete the MedWatch (INV-048).", medWatchId);
                await StopComplaintClockAsync(medWatchId, sub);
                await db.SaveChangesAsync();
            }
            return EmdrResult.Success($"{label} recorded.");
        }, r => r.Ok);
    }

    /// <summary>
    /// ACK3 accepted an initial report: the complaint's FDA clock stops at the official receipt date (ACK2), and the report number is
    /// recorded on the complaint (CMP-009).
    /// </summary>
    private async Task StopComplaintClockAsync(Guid medWatchId, EmdrSubmission sub)
    {
        var row = await db.Set<MedWatchReport>().AsNoTracking().FirstOrDefaultAsync(x => x.MedWatchId == medWatchId);
        if (row is not { IsInitial: true, ComplaintId: { } complaintId } || !Cfg.FormsByKey.ContainsKey("complaint")) return;
        var complaint = await records.LoadAsync(complaintId);
        if (complaint == null) return;
        var reference = $"MFR {sub.ReportNumber}";
        var values = new Dictionary<string, object?>();
        if (complaint.Date("fda_submitted_on") == null) values["fda_submitted_on"] = DateOnly.FromDateTime(sub.Ack2At ?? sub.Ack3At ?? DateTime.UtcNow);
        var refs = complaint.Text("report_reference");
        if (refs == null || !refs.Contains(sub.ReportNumber)) values["report_reference"] = string.Join("; ", new[] { refs, reference }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (values.Count == 0) return;
        var upd = await records.UpdateAsync(complaintId, values, $"eMDR report {sub.ReportNumber} accepted by the FDA");
        if (upd.Ok) audit.Record(complaintId, "Regulatory report submitted", $"FDA eMDR {sub.ReportNumber} accepted (ACK3); FDA receipt {sub.Ack2At:dd MMM yyyy}");
    }

    /// <summary>INV-048 / AER-009: what still stands between this MedWatch and Completed.</summary>
    public async Task<List<string>> CompletionProblemsAsync(RecordSnapshot s)
    {
        var problems = new List<string>();
        var sub = await CurrentAsync(s.Record.Id);
        if (sub == null) { problems.Add("The report hasn't been submitted to the FDA."); return problems; }
        if (sub.Ack1At == null) problems.Add("ACK1 (ESG receipt) hasn't been recorded.");
        if (sub.Ack2At == null) problems.Add("ACK2 (CDRH receipt) hasn't been recorded.");
        if (sub.Ack3At == null) problems.Add("ACK3 (load and validation) hasn't been recorded.");
        if (sub.Status == EmdrStatus.Rejected || sub.Status == EmdrStatus.Failed)
            problems.Add($"The FDA rejected this submission: {Trim(sub.LastError ?? "see the acknowledgements", 200)} Correct the report and resubmit.");
        if (s.Bool("confirm_final_ack") != true) problems.Add("Set \"Confirm final acknowledgement\" to Yes.");
        return problems;
    }

    // ------------------------------------------------------------------ AER-010 monitoring

    public sealed record MonitorResult(int Acks, int Alerts, List<string> Log);

    /// <summary>Poll gateways for acknowledgements and raise alerts for failures, overdue acknowledgements and passed deadlines.</summary>
    public async Task<MonitorResult> MonitorAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var log = new List<string>();
        var acks = 0;
        var alerts = 0;
        if (!Cfg.FormsByKey.TryGetValue(AerKeys.MedWatch, out var form)) return new(0, 0, log);
        var profile = await setup.ProfileAsync();
        var delay = TimeSpan.FromMinutes(profile?.ReceiptDelayMinutes ?? AerProfileInfo.DefaultReceiptDelayMinutes);
        var submittedPhase = form.Workflows.SelectMany(w => w.Phases).Where(p => p.Key == "submitted").Select(p => p.Id).ToList();

        var subs = await db.Set<EmdrSubmission>().AsNoTracking().Where(x => !x.Superseded &&
            (x.Status == EmdrStatus.AwaitingUpload || x.Status == EmdrStatus.Sent || (x.FailureAlertedAt == null && (x.Status == EmdrStatus.Rejected || x.Status == EmdrStatus.Failed))))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var subId in subs)
        {
            db.ChangeTracker.Clear();
            var sub = await db.Set<EmdrSubmission>().FirstAsync(x => x.Id == subId, ct);
            var rec = await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == sub.MedWatchId, ct);
            if (rec?.PhaseId is not { } phaseId || !submittedPhase.Contains(phaseId)) continue;
            var gateway = gateways.Get(sub.Gateway);
            if (gateway != null && sub.Status == EmdrStatus.Sent)
                foreach (var a in (await gateway.PollAsync(sub, nowUtc, ct)).OrderBy(a => a.Type))
                {
                    var r = await RecordAckAsync(sub.MedWatchId, a, $"{gateway.Name} (automatic)");
                    if (r.Ok) { acks++; log.Add($"{rec.Number}: {a.Type.ToUpperInvariant()} {(a.Accepted ? "accepted" : "rejected")}"); }
                    else log.Add($"{rec.Number}: couldn't record {a.Type}: {string.Join("; ", r.Errors)}");
                    sub = await db.Set<EmdrSubmission>().FirstAsync(x => x.Id == subId, ct);
                }
            if ((sub.Status is EmdrStatus.Rejected or EmdrStatus.Failed) && sub.FailureAlertedAt == null)
            {
                await RaiseFailureAsync(sub, sub.LastError ?? "The FDA reported an error.");
                alerts++;
            }
            if (EmdrStatus.IsPending(sub.Status) && sub.OverdueAlertedAt == null && (sub.SentAt ?? sub.CreatedAt) + delay < nowUtc)
            {
                await RaiseOverdueAsync(sub, delay);
                alerts++;
                log.Add($"{rec.Number}: acknowledgements overdue");
            }
        }

        // A report must never sit silently past its deadline (AER-010, CMP-009).
        var today = DateOnly.FromDateTime(nowUtc);
        var openIds = await db.Records.AsNoTracking().Where(r => r.FormId == form.Id && r.PhaseType == PhaseType.Normal).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in openIds)
        {
            db.ChangeTracker.Clear();
            var s = await records.LoadRequiredAsync(id);
            if (s.Date("report_due") is not { } due || due >= today) continue;
            var sub = await CurrentAsync(id);
            if (sub?.Status == EmdrStatus.Accepted) continue;
            var title = $"MISSED eMDR deadline: {s.Record.Number} was due {due:dd MMM yyyy}";
            if (await db.Notifications.AnyAsync(n => n.RecordId == id && n.Title == title, ct)) continue;
            var state = sub == null ? $"still in {s.Phase?.Name}" : sub.Status switch
            {
                EmdrStatus.AwaitingUpload => "generated but not uploaded to the FDA",
                EmdrStatus.Sent => "sent, but the FDA hasn't accepted it yet",
                _ => "rejected by the FDA and not yet resubmitted",
            };
            notify.SafetyCritical(Recipients(s), title, $"{s.Text("report_rule") ?? "The reporting deadline"} for \"{s.Record.Title}\" passed on {due:dd MMM yyyy}. The report is {state}.", id);
            audit.Record(id, "Reporting deadline passed", $"Due {due:dd MMM yyyy}; the report is {state}. Regulatory affairs alerted (AER-010).");
            if (sub != null) sub.DeadlineAlertedAt = nowUtc;
            await db.SaveChangesAsync(ct);
            alerts++;
            log.Add(title);
        }
        return new(acks, alerts, log);
    }

    private async Task RaiseFailureAsync(EmdrSubmission sub, string detail)
    {
        var s = await records.LoadRequiredAsync(sub.MedWatchId);
        var title = $"eMDR submission rejected: {s.Record.Number} ({sub.ReportNumber})";
        notify.SafetyCritical(Recipients(s), title, $"The FDA did not accept \"{s.Record.Title}\". {Trim(detail, 600)} Correct the report and resubmit before the deadline ({s.Date("report_due"):dd MMM yyyy}).", s.Record.Id);
        await UrgentTaskAsync(s, $"URGENT: fix and resubmit eMDR {sub.ReportNumber} ({s.Record.Number})",
            $"The FDA rejected submission {sub.Attempt} (message {sub.MessageId}).\n\n{detail}\n\nRoute the MedWatch back to Draft (\"Correct and resubmit\"), fix the data and submit again.");
        sub.FailureAlertedAt = DateTime.UtcNow;
        audit.Record(s.Record.Id, "Safety-critical alert raised", $"Submission {sub.Attempt} rejected; regulatory affairs alerted and an urgent action item created (AER-010).");
        await db.SaveChangesAsync();
    }

    private async Task RaiseOverdueAsync(EmdrSubmission sub, TimeSpan delay)
    {
        var s = await records.LoadRequiredAsync(sub.MedWatchId);
        var missing = string.Join(", ", new[] { sub.Ack1At == null ? "ACK1" : null, sub.Ack2At == null ? "ACK2" : null, sub.Ack3At == null ? "ACK3" : null }.OfType<string>());
        var what = sub.Status == EmdrStatus.AwaitingUpload ? "The file hasn't been uploaded to the FDA" : $"{missing} hasn't arrived";
        var hours = delay.TotalHours >= 1 ? $"{delay.TotalHours:0.#} hours" : $"{delay.TotalMinutes:0} minutes";
        notify.SafetyCritical(Recipients(s), $"eMDR acknowledgement overdue: {s.Record.Number} ({sub.ReportNumber})",
            $"{what} within the {hours} receipt delay for \"{s.Record.Title}\". Check the ESG account and the gateway; the report deadline is {s.Date("report_due"):dd MMM yyyy}.", s.Record.Id);
        await UrgentTaskAsync(s, $"URGENT: eMDR acknowledgement overdue for {sub.ReportNumber} ({s.Record.Number})",
            $"{what} within the {hours} receipt delay (message {sub.MessageId}, sent {(sub.SentAt ?? sub.CreatedAt):dd MMM yyyy HH:mm} UTC). " +
            "Check the FDA ESG account for the acknowledgements and record them, or contact the ESG help desk.");
        sub.OverdueAlertedAt = DateTime.UtcNow;
        await SetStatusAsync(s.Record.Id, "overdue", new(), "Acknowledgement overdue");
        audit.Record(s.Record.Id, "Safety-critical alert raised", $"{what} within the receipt delay; regulatory affairs alerted and an urgent action item created (AER-010).");
        await db.SaveChangesAsync();
    }

    private async Task UrgentTaskAsync(RecordSnapshot s, string subject, string description)
    {
        if (!Cfg.FormsByKey.ContainsKey("action_item")) return;
        var assignee = s.UserId("owner") is { } owner && dir.Get(owner) is { Status: UserStatus.Active } ? owner
            : dir.ActiveMembers("regulatory").Select(u => u.Id).FirstOrDefault();
        if (assignee == 0) assignee = s.Record.CreatedById;
        var res = await records.CreateAsync(new CreateRequest
        {
            FormKey = "action_item", StartPhaseKey = "open", ParentId = s.Record.Id, SkipAccessCheck = true, Assignees = [assignee], DueDate = BusinessCalendar.Today,
            CreateComment = "Raised automatically by eMDR monitoring (AER-010)",
            Values = new() { ["subject"] = Trim(subject, 200), ["description"] = description, ["assignee"] = assignee, ["due_date"] = BusinessCalendar.Today, ["verification_required"] = false },
        });
        if (!res.Ok) audit.Record(s.Record.Id, "Alert task failed", string.Join("; ", res.Errors.Select(e => e.Message)));
    }

    private List<int> Recipients(RecordSnapshot s) =>
        dir.ActiveMembers("regulatory").Select(u => u.Id).Append(s.UserId("owner") ?? s.Record.CreatedById).Distinct().ToList();

    private async Task SetStatusAsync(Guid id, string status, Dictionary<string, object?> more, string reason)
    {
        var values = new Dictionary<string, object?>(more) { ["submission_status"] = status };
        using (guard.Allow())
        {
            var res = await records.UpdateAsync(id, values, reason);
            if (!res.Ok) throw new WorkflowException("Couldn't update the submission status: " + string.Join("; ", res.Errors.Select(e => e.Message)));
        }
    }

    public static bool Unique(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
