using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Security;
using Vellum.Infrastructure;

namespace Vellum.Modules.Admin;

public sealed record KeywordUse(AppDefinition App, FormDefinition Form, FieldDefinition Field);

/// <summary>
/// Master keyword lists (ADM-603..607). Keywords are stored on records by ID (INV-061), so they are never deleted:
/// they are disabled. Renaming a label in use needs explicit confirmation (ADM-607). Every change writes exactly one
/// configuration audit entry with the list before and after (INV-145) and refreshes the configuration cache.
/// </summary>
[ScopedService]
public sealed class KeywordAdminService(VellumDbContext db, CurrentUser me, ConfigCache cfg, AuditService audit)
{
    /// <summary>ADM-603/606: which forms and fields use each list (parent applications are derived from these).</summary>
    public Dictionary<string, List<KeywordUse>> Usage()
    {
        var map = new Dictionary<string, List<KeywordUse>>(StringComparer.Ordinal);
        foreach (var app in cfg.Current.Apps)
            foreach (var form in app.Forms)
                foreach (var f in form.Fields.Where(f => f.Type is FieldType.Keyword or FieldType.KeywordMulti && f.KeywordList != null))
                {
                    if (!map.TryGetValue(f.KeywordList!, out var list)) map[f.KeywordList!] = list = [];
                    list.Add(new KeywordUse(app, form, f));
                }
        return map;
    }

    /// <summary>How many stored values reference each keyword of the list (keyword ID → count).</summary>
    public async Task<Dictionary<int, int>> ValueCountsAsync(string listKey)
    {
        var uses = Usage().GetValueOrDefault(listKey) ?? [];
        if (uses.Count == 0 || !cfg.Current.KeywordListsByKey.TryGetValue(listKey, out var list)) return [];
        var ids = list.Keywords.Select(k => k.Id).ToList();
        var fieldKeys = uses.Select(u => u.Field.Key).Distinct().ToList();
        var formIds = uses.Select(u => u.Form.Id).Distinct().ToList();
        var valid = uses.Select(u => (u.Form.Id, u.Field.Key)).ToHashSet();
        var rows = await db.RecordValues.AsNoTracking()
            .Where(v => v.RefId != null && ids.Contains(v.RefId!.Value) && fieldKeys.Contains(v.FieldKey))
            .Join(db.Records.Where(r => formIds.Contains(r.FormId)), v => v.RecordId, r => r.Id, (v, r) => new { r.FormId, v.FieldKey, v.RefId, v.RecordId })
            .GroupBy(x => new { x.FormId, x.FieldKey, x.RefId })
            .Select(g => new { g.Key.FormId, g.Key.FieldKey, g.Key.RefId, Records = g.Select(x => x.RecordId).Distinct().Count() })
            .ToListAsync();
        return rows.Where(r => valid.Contains((r.FormId, r.FieldKey))).GroupBy(r => r.RefId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Records));
    }

    private async Task<KeywordList?> LoadAsync(string key) => await db.KeywordLists.Include(l => l.Keywords).FirstOrDefaultAsync(l => l.Key == key);

    public static string Snapshot(KeywordList l) => AdminJson.Of(new
    {
        l.Key, l.Name, l.Description,
        keywords = l.Keywords.OrderBy(k => k.Order).ThenBy(k => k.Label).Select(k => new { k.Key, k.Label, k.Color, k.Order, k.Disabled }),
    });

    private static string? NormalizeColor(string? color, out string? error)
    {
        error = null;
        color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        if (color == null) return null;
        if (AdminText.ColorVariants.Contains(color) || AdminText.HexColor.IsMatch(color)) return color;
        error = "Choose a color from the list, or a hex color like #0d9488.";
        return null;
    }

    private async Task<AdminResult> CommitAsync(KeywordList list, string before, string action, string detail, string? reason = null)
    {
        audit.Global("Keyword list changed", $"{list.Name} ({list.Key}): {detail}", reason);
        audit.Config("KeywordList", list.Key, action, before, Snapshot(list), reason);
        await db.SaveChangesAsync();
        cfg.Invalidate();
        return AdminResult.Success(detail);
    }

    public async Task<AdminResult> UpdateListAsync(string key, string? name, string? description)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var list = await LoadAsync(key);
        if (list == null) return AdminResult.Fail("That keyword list doesn't exist.");
        name = name?.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (string.IsNullOrEmpty(name)) return AdminResult.Fail("Enter a display name.");
        if (list.Name == name && list.Description == description) return AdminResult.Success("No changes to save.");
        var before = Snapshot(list);
        list.Name = name;
        list.Description = description;
        return await CommitAsync(list, before, "Updated", "Saved the list details.");
    }

    public async Task<AdminResult> AddKeywordAsync(string listKey, string? label, string? keywordKey, string? color)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var list = await LoadAsync(listKey);
        if (list == null) return AdminResult.Fail("That keyword list doesn't exist.");
        label = label?.Trim();
        if (string.IsNullOrEmpty(label)) return AdminResult.Fail("Enter the keyword's text.");
        if (label.Length > 200) return AdminResult.Fail("Keep the keyword under 200 characters.");
        if (list.Keywords.Any(k => string.Equals(k.Label, label, StringComparison.OrdinalIgnoreCase)))
            return AdminResult.Fail($"“{label}” is already in this list" + (list.Keywords.First(k => string.Equals(k.Label, label, StringComparison.OrdinalIgnoreCase)).Disabled ? " (disabled). Enable it instead." : "."));
        var key = string.IsNullOrWhiteSpace(keywordKey) ? AdminText.Slug(label, '_') : keywordKey.Trim().ToLowerInvariant();
        if (!AdminText.KeyPattern.IsMatch(key)) return AdminResult.Fail("The key uses lowercase letters, digits, dashes and underscores.");
        if (list.Keywords.Any(k => k.Key == key))
        {
            if (!string.IsNullOrWhiteSpace(keywordKey)) return AdminResult.Fail($"The key “{key}” is already used in this list.");
            var n = 2;
            while (list.Keywords.Any(k => k.Key == $"{key}_{n}")) n++;
            key = $"{key}_{n}";
        }
        var c = NormalizeColor(color, out var colorError);
        if (colorError != null) return AdminResult.Fail(colorError);
        var before = Snapshot(list);
        list.Keywords.Add(new Keyword { Key = key, Label = label, Color = c, Order = list.Keywords.Count == 0 ? 0 : list.Keywords.Max(k => k.Order) + 1 });
        return await CommitAsync(list, before, "Keyword added", $"Added “{label}”.");
    }

    /// <summary>
    /// Change a keyword's text or color. ADM-607: renaming a keyword that is in use changes the label on every record
    /// that shows it (records store the ID), so it is refused unless <paramref name="confirmRename"/> is set.
    /// </summary>
    public async Task<AdminResult> UpdateKeywordAsync(string listKey, int keywordId, string? label, string? color, bool confirmRename, string? reason = null)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var list = await LoadAsync(listKey);
        var kw = list?.Keywords.FirstOrDefault(k => k.Id == keywordId);
        if (list == null || kw == null) return AdminResult.Fail("That keyword doesn't exist.");
        label = label?.Trim();
        if (string.IsNullOrEmpty(label)) return AdminResult.Fail("Enter the keyword's text.");
        if (list.Keywords.Any(k => k.Id != kw.Id && string.Equals(k.Label, label, StringComparison.OrdinalIgnoreCase)))
            return AdminResult.Fail($"Another keyword in this list is already called “{label}”.");
        var c = NormalizeColor(color, out var colorError);
        if (colorError != null) return AdminResult.Fail(colorError);
        var renamed = kw.Label != label;
        if (!renamed && kw.Color == c) return AdminResult.Success("No changes to save.");
        if (renamed)
        {
            var used = (await ValueCountsAsync(listKey)).GetValueOrDefault(kw.Id);
            if (used > 0 && !confirmRename)
                return AdminResult.Fail($"“{kw.Label}” is used on {Infrastructure.Ui.Ui.Plural(used, "record")}. Renaming it changes the label on all of them, including historical records. " +
                                        "Confirm the rename, or disable this keyword and add a new one instead (ADM-607).");
        }
        var before = Snapshot(list);
        var old = kw.Label;
        kw.Label = label;
        kw.Color = c;
        return await CommitAsync(list, before, renamed ? "Keyword renamed" : "Keyword updated",
            renamed ? $"Renamed “{old}” to “{label}”." : $"Updated “{label}”.", reason);
    }

    public async Task<AdminResult> SetDisabledAsync(string listKey, int keywordId, bool disabled)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var list = await LoadAsync(listKey);
        var kw = list?.Keywords.FirstOrDefault(k => k.Id == keywordId);
        if (list == null || kw == null) return AdminResult.Fail("That keyword doesn't exist.");
        if (kw.Disabled == disabled) return AdminResult.Success("No changes.");
        if (disabled && list.Keywords.Count(k => !k.Disabled) <= 1) return AdminResult.Fail("A list needs at least one enabled keyword.");
        var before = Snapshot(list);
        kw.Disabled = disabled;
        return await CommitAsync(list, before, disabled ? "Keyword disabled" : "Keyword enabled",
            disabled ? $"Disabled “{kw.Label}”. Existing records still show it; it's no longer offered for new values." : $"Enabled “{kw.Label}”.");
    }

    public async Task<AdminResult> MoveAsync(string listKey, int keywordId, int delta)
    {
        if (!me.IsAdmin) return AdminResult.Fail(AdminResult.NotAdmin);
        var list = await LoadAsync(listKey);
        if (list == null) return AdminResult.Fail("That keyword list doesn't exist.");
        var ordered = list.Keywords.OrderBy(k => k.Order).ThenBy(k => k.Label).ToList();
        var i = ordered.FindIndex(k => k.Id == keywordId);
        var j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= ordered.Count) return AdminResult.Success("No changes.");
        var before = Snapshot(list);
        (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        for (var n = 0; n < ordered.Count; n++) ordered[n].Order = n;
        return await CommitAsync(list, before, "Keywords reordered", $"Moved “{ordered[j].Label}” {(delta < 0 ? "up" : "down")}.");
    }
}
