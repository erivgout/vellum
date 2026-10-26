using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Vellum.Domain;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Infrastructure.Ui;

/// <summary>Small HTML building blocks shared by every page (badges, chips, avatars, dates).</summary>
public static class Ui
{
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    public static IHtmlContent Icon(string bi, string? cls = null) => new HtmlString($"<i class=\"bi {E(bi)}{(cls != null ? " " + E(cls) : "")}\" aria-hidden=\"true\"></i>");

    /// <summary>
    /// Text with each occurrence of a search term wrapped in &lt;mark&gt;. It matches the raw text and encodes each piece,
    /// so a term never lands inside an HTML entity ("amp" in R&amp;D).
    /// </summary>
    public static IHtmlContent Highlight(string? text, string? term)
    {
        if (string.IsNullOrEmpty(text)) return HtmlString.Empty;
        term = term?.Trim();
        if (term is not { Length: >= 2 }) return new HtmlString(E(text));
        var sb = new System.Text.StringBuilder();
        var at = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(term), System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            sb.Append(E(text[at..m.Index])).Append("<mark>").Append(E(m.Value)).Append("</mark>");
            at = m.Index + m.Length;
        }
        return new HtmlString(sb.Append(E(text[at..])).ToString());
    }

    public static IHtmlContent Avatar(UserInfo? u, string size = "sm", bool showName = false)
    {
        if (u == null) return HtmlString.Empty;
        var title = E(u.DisplayName + (u.JobTitle != null ? " · " + u.JobTitle : ""));
        var warn = u.Status == UserStatus.Disabled ? " vl-avatar-disabled" : "";
        var av = $"<span class=\"vl-avatar vl-avatar-{size}{warn}\" style=\"--h:{u.Hue}\" title=\"{title}\" aria-label=\"{E(u.DisplayName)}\">{E(u.Initials)}</span>";
        return new HtmlString(showName ? $"<span class=\"vl-person\">{av}<span class=\"vl-person-name\">{E(u.DisplayName)}</span></span>" : av);
    }

    public static IHtmlContent Avatars(IEnumerable<UserInfo?> users, int max = 3, string size = "sm")
    {
        var list = users.OfType<UserInfo>().DistinctBy(u => u.Id).ToList();
        if (list.Count == 0) return new HtmlString("<span class=\"text-body-tertiary\">–</span>");
        if (list.Count == 1) return Avatar(list[0], size, showName: true);
        var html = string.Concat(list.Take(max).Select(u => Avatar(u, size).ToString()));
        var more = list.Count > max ? $"<span class=\"vl-avatar vl-avatar-{size} vl-avatar-more\" title=\"{E(string.Join(", ", list.Skip(max).Select(u => u.DisplayName)))}\">+{list.Count - max}</span>" : "";
        return new HtmlString($"<span class=\"vl-avatar-stack\" title=\"{E(string.Join(", ", list.Select(u => u.DisplayName)))}\">{html}{more}</span>");
    }

    /// <summary>Phase chip. Normal phases are tinted by due status (PLT-302); terminal phases by type.</summary>
    public static IHtmlContent Phase(string? name, PhaseType type, DateOnly? due = null, bool archived = false, bool effective = false)
    {
        if (string.IsNullOrEmpty(name)) return HtmlString.Empty;
        var (cls, icon) = effective ? ("vl-chip-effective", "bi-patch-check-fill")
            : archived ? ("vl-chip-archived", "bi-archive")
            : type switch
            {
                PhaseType.Completed => ("vl-chip-done", "bi-check-circle-fill"),
                PhaseType.Voided => ("vl-chip-void", "bi-slash-circle"),
                PhaseType.Obsolete => ("vl-chip-archived", "bi-archive"),
                PhaseType.Archived => ("vl-chip-archived", "bi-archive"),
                _ => (DueClass(due), "bi-circle-fill vl-dot"),
            };
        return new HtmlString($"<span class=\"vl-chip {cls}\"><i class=\"bi {icon}\" aria-hidden=\"true\"></i>{E(name)}</span>");
    }

    private static string DueClass(DateOnly? due)
    {
        if (due is not { } d) return "vl-chip-open";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return d < today ? "vl-chip-overdue" : d == today ? "vl-chip-today" : "vl-chip-open";
    }

    /// <summary>Due-date chip: "Overdue 3d", "Due today", "Due in 4d" or a date.</summary>
    public static IHtmlContent Due(DateOnly? due, bool open = true, bool compact = false)
    {
        if (due is not { } d) return new HtmlString("<span class=\"text-body-tertiary\">–</span>");
        var date = d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        if (!open) return new HtmlString($"<span class=\"text-body-secondary\">{date}</span>");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = d.DayNumber - today.DayNumber;
        var (cls, text, icon) = days switch
        {
            < 0 => ("vl-due-overdue", $"Overdue {-days}d", "bi-exclamation-octagon-fill"),
            0 => ("vl-due-today", "Due today", "bi-hourglass-split"),
            <= 7 => ("vl-due-soon", compact ? $"{days}d" : $"Due in {days}d", "bi-clock"),
            _ => ("vl-due-later", date, "bi-calendar3"),
        };
        return new HtmlString($"<span class=\"vl-due {cls}\" title=\"Due {date}\"><i class=\"bi {icon}\" aria-hidden=\"true\"></i>{E(text)}</span>");
    }

    public static IHtmlContent Keyword(Keyword? k)
    {
        if (k == null) return HtmlString.Empty;
        return KeywordBadge(k.Label, k.Color);
    }

    public static IHtmlContent KeywordBadge(string label, string? color)
    {
        if (string.IsNullOrEmpty(color)) return new HtmlString($"<span class=\"vl-kw\">{E(label)}</span>");
        if (color.StartsWith('#')) return new HtmlString($"<span class=\"vl-kw vl-kw-hex\" style=\"--kw:{E(color)}\">{E(label)}</span>");
        return new HtmlString($"<span class=\"vl-kw vl-kw-{E(color)}\">{E(label)}</span>");
    }

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 7 ? $"{(int)span.TotalDays} d ago"
            : utc.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static IHtmlContent Time(DateTime utc, TimeZoneInfo tz, bool relative = true)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var abs = local.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " " + Abbrev(tz, local);
        return new HtmlString($"<time datetime=\"{utc:O}\" title=\"{E(abs)} · {utc:yyyy-MM-dd HH:mm} UTC\">{E(relative ? Ago(utc) : abs)}</time>");
    }

    public static string Abbrev(TimeZoneInfo tz, DateTime local)
    {
        if (tz == TimeZoneInfo.Utc) return "UTC";
        var offset = tz.GetUtcOffset(local);
        return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
    }

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static string FileIcon(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "bi-file-earmark-pdf",
        ".doc" or ".docx" => "bi-file-earmark-word",
        ".xls" or ".xlsx" or ".csv" => "bi-file-earmark-excel",
        ".ppt" or ".pptx" => "bi-file-earmark-ppt",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "bi-file-earmark-image",
        ".zip" => "bi-file-earmark-zip",
        ".txt" or ".md" => "bi-file-earmark-text",
        _ => "bi-file-earmark",
    };

    public static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
