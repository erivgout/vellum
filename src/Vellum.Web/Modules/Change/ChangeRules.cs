namespace Vellum.Modules.Change;

/// <summary>One approval type of a change with the people who may approve for it.</summary>
public sealed record ApprovalRequirement(string TypeKey, string TypeLabel, IReadOnlyList<int> Approvers);

/// <summary>Coverage of one approval type: who approved and whether the type's requirement is met.</summary>
public sealed record ApprovalTypeStatus(string TypeKey, string TypeLabel, IReadOnlyList<int> Approvers, IReadOnlyList<int> ApprovedBy, int Required)
{
    public bool Covered => ApprovedBy.Count >= Required;
}

public sealed record ApprovalCoverage(IReadOnlyList<ApprovalTypeStatus> Types)
{
    /// <summary>INV-148: every approval type has the required number of approving signatures.</summary>
    public bool Complete => Types.Count > 0 && Types.All(t => t.Covered);
    public IEnumerable<ApprovalTypeStatus> Uncovered => Types.Where(t => !t.Covered);
    public IEnumerable<ApprovalTypeStatus> TypesOf(int userId) => Types.Where(t => t.Approvers.Contains(userId));
}

/// <summary>
/// Change Management rules as pure functions: approval coverage by approval type (CHG-021, INV-148) and the regulatory
/// completion gate (CHG-050/051, INV-161, SAF-022). Behaviors call these; tests exercise them directly.
/// </summary>
public static class ChangeRules
{
    public const int CbeWaitingDays = 30;

    /// <summary>
    /// Per-type coverage. Rows with the same type are merged. A type needs one approval, or every listed approver when
    /// <paramref name="requireAll"/> is set (template option). Someone listed for several types covers all of them.
    /// </summary>
    public static ApprovalCoverage Coverage(IEnumerable<ApprovalRequirement> rows, IReadOnlySet<int> approvedBy, bool requireAll)
    {
        var types = rows.GroupBy(r => r.TypeKey)
            .Select(g =>
            {
                var approvers = g.SelectMany(r => r.Approvers).Distinct().ToList();
                var approved = approvers.Where(approvedBy.Contains).ToList();
                return new ApprovalTypeStatus(g.Key, g.First().TypeLabel, approvers, approved, requireAll ? Math.Max(1, approvers.Count) : 1);
            })
            .ToList();
        return new ApprovalCoverage(types);
    }

    /// <summary>
    /// Approvers whose assignment is no longer needed once <paramref name="coverage"/> is reached: every type they approve for is
    /// already covered (or they aren't listed for any type). With "all approvers" nobody listed is released.
    /// </summary>
    public static bool IsReleasable(ApprovalCoverage coverage, int userId, bool requireAll)
    {
        var types = coverage.TypesOf(userId).ToList();
        if (types.Count == 0) return true;
        return !requireAll && types.All(t => t.Covered);
    }

    /// <summary>The filing a regulatory impact calls for (CHG-050). Moderate leaves the CBE-30 / CBE-0 choice to the user.</summary>
    public static IReadOnlyList<string> FilingsFor(string? impact) => impact switch
    {
        "substantial" => ["pas"],
        "moderate" => ["cbe30", "cbe0"],
        "minor" => ["annual"],
        "none" => ["none"],
        _ => [],
    };

    /// <summary>A save-time error when the chosen filing doesn't match the impact classification, otherwise null.</summary>
    public static string? FilingMismatch(string? impact, string? filing)
    {
        if (impact == null || filing == null) return null;
        var allowed = FilingsFor(impact);
        if (allowed.Count == 0 || allowed.Contains(filing)) return null;
        return impact switch
        {
            "substantial" => "A substantial change needs a Prior Approval Supplement (PAS): approval is needed before distribution.",
            "moderate" => "A moderate change is filed as CBE-30 or CBE-0.",
            "minor" => "A minor change isn't submitted; document it in the Annual report.",
            _ => "Without regulatory impact, choose \"No filing\".",
        };
    }

    /// <summary>
    /// INV-161 / CHG-051 / SAF-022: what stops the change from being Completed (made effective) right now.
    /// PAS: submission, agency approval date (not in the future) and approval evidence. CBE-30: 30 days after submission unless
    /// the agency responded. CBE-0: a submission date. Annual report and no filing: nothing.
    /// </summary>
    public static List<string> RegulatoryBlockers(string? impact, string? filing, DateOnly? submitted, DateOnly? agencyResponse,
        DateOnly? approvalDate, bool hasEvidence, DateOnly today)
    {
        var b = new List<string>();
        if (impact is "substantial" or "moderate" && filing == null)
        {
            b.Add("Choose the regulatory filing for this change (PAS, CBE-30 or CBE-0).");
            return b;
        }
        switch (filing)
        {
            case "pas":
                if (submitted == null) b.Add("Record the date the Prior Approval Supplement was submitted.");
                if (approvalDate == null) b.Add("A PAS change can't be completed until the agency's approval date is recorded (CHG-051).");
                else if (approvalDate > today) b.Add("The agency approval date can't be in the future.");
                else if (submitted is { } s && approvalDate < s) b.Add("The agency approval date can't be before the submission date.");
                if (!hasEvidence) b.Add("Attach the agency's approval letter as evidence before completing a PAS change.");
                break;
            case "cbe30":
                if (submitted == null) b.Add("Record the CBE-30 submission date.");
                else if (agencyResponse == null && today < submitted.Value.AddDays(CbeWaitingDays))
                    b.Add($"CBE-30: the change can be made effective from {submitted.Value.AddDays(CbeWaitingDays):dd MMM yyyy} (30 days after submission) unless the agency responds earlier.");
                break;
            case "cbe0":
                if (submitted == null) b.Add("Record the CBE-0 submission date: the change takes effect when the supplement is submitted.");
                break;
        }
        return b;
    }

    /// <summary>Earliest date a CBE-30 change may become effective, when known.</summary>
    public static DateOnly? CbeEffectiveFrom(DateOnly? submitted, DateOnly? agencyResponse) =>
        agencyResponse ?? submitted?.AddDays(CbeWaitingDays);
}
