using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Domain;
using Vellum.Engine;
using Vellum.Engine.Records;
using Vellum.Engine.Security;

namespace Vellum.Infrastructure.Ui;

public sealed record NavApp(AppDefinition App, int OpenAssignments);
public sealed record NavSuite(string Name, string Icon, List<NavApp> Apps);

/// <summary>Per-request data for the layout: user, environment, navigation, counts.</summary>
public sealed class UiContext(VellumDbContext db, CurrentUser me, AccessService access, ConfigCache cfg, UserDirectory dir, IServiceProvider services)
{
    private SystemSettings? _settings;
    private List<NavSuite>? _suites;
    private int? _unread;
    private List<Favorite>? _favorites;
    private List<NavItem>? _extra;

    public static readonly (string Name, string Icon)[] SuiteOrder =
    [
        ("Documents", "bi-journal-text"), ("Quality", "bi-patch-check"), ("Supplier", "bi-truck"),
        ("Risk", "bi-exclamation-diamond"), ("EHS", "bi-cone-striped"), ("Operations", "bi-gear-wide-connected"),
    ];

    public CurrentUser Me => me;
    public UserInfo? User => me.Info;
    public AccessService Access => access;
    public ConfigModel Config => cfg.Current;
    public UserDirectory Directory => dir;
    public TimeZoneInfo TimeZone => RecordService.Tz(me.Info?.TimeZone);

    public SystemSettings Settings => _settings ??= db.Settings.AsNoTracking().First();
    public bool IsProduction => Settings.EnvironmentType == EnvironmentType.Production;

    public string Theme => me.IsAuthenticated ? db.Users.Where(u => u.Id == me.Id).Select(u => u.Theme).FirstOrDefault() ?? "auto" : "auto";

    public List<NavSuite> Suites
    {
        get
        {
            if (_suites != null) return _suites;
            var counts = me.IsAuthenticated
                ? db.Assignments.Where(a => a.UserId == me.Id && a.Status == AssignmentStatus.Active && a.Record.PhaseType == PhaseType.Normal)
                    .GroupBy(a => a.Record.AppId).Select(g => new { g.Key, Count = g.Count() }).ToDictionary(x => x.Key, x => x.Count)
                : [];
            var apps = Config.Apps.Where(a => !a.IsSystem && access.CanOpenApp(a.Id)).ToList();
            _suites = SuiteOrder
                .Select(s => new NavSuite(s.Name, s.Icon, apps.Where(a => a.Suite == s.Name).Select(a => new NavApp(a, counts.GetValueOrDefault(a.Id))).ToList()))
                .Concat(apps.Where(a => SuiteOrder.All(s => s.Name != a.Suite)).GroupBy(a => a.Suite)
                    .Select(g => new NavSuite(g.Key, "bi-grid", g.Select(a => new NavApp(a, counts.GetValueOrDefault(a.Id))).ToList())))
                .Where(s => s.Apps.Count > 0)
                .ToList();
            return _suites;
        }
    }

    public List<NavItem> ExtraNav => _extra ??= services.GetServices<INavContributor>().SelectMany(c => c.Items()).OrderBy(i => i.Order).ToList();

    public int UnreadNotifications => _unread ??= me.IsAuthenticated ? db.Notifications.Count(n => n.UserId == me.Id && n.ReadAt == null) : 0;

    public int OpenSafetyAlerts => me.IsAuthenticated ? db.Notifications.Count(n => n.UserId == me.Id && n.Kind == NotificationKind.SafetyCritical && n.AcknowledgedAt == null) : 0;

    public int MyOpenAssignments => Suites.SelectMany(s => s.Apps).Sum(a => a.OpenAssignments);

    public List<Favorite> Favorites => _favorites ??= me.IsAuthenticated
        ? db.Favorites.AsNoTracking().Where(f => f.UserId == me.Id).OrderBy(f => f.Kind).ThenBy(f => f.Label).ToList()
        : [];

    public bool IsFavorite(string kind, string key) => Favorites.Any(f => f.Kind == kind && f.RefKey == key);

    /// <summary>Forms the user can create, grouped by app (for the global "New" menu).</summary>
    public IEnumerable<(AppDefinition App, List<FormDefinition> Forms)> Creatable() =>
        Suites.SelectMany(s => s.Apps).Select(a => (a.App, a.App.Forms.Where(f => !f.IsActivity && !f.HiddenFromNew && !f.IsNonWorkflow && access.CanCreate(f)).ToList()))
            .Where(x => x.Item2.Count > 0);

    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
}
