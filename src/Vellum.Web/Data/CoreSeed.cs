using Microsoft.EntityFrameworkCore;
using Vellum.Domain;
using Vellum.Engine.Security;
using Vellum.Packaging;

namespace Vellum.Data;

/// <summary>Tenant baseline: settings, locations, the system identities, demo people, groups and the business calendar.</summary>
public static class CoreSeed
{
    public const string SystemUsername = "system";
    /// <summary>Intake identity for anonymous reports (EHS near misses). It can't sign in or sign.</summary>
    public const string AnonymousUsername = "anonymous";

    /// <summary>Runs on every start so databases created before an identity existed get it too.</summary>
    public static async Task EnsureSystemIdentitiesAsync(VellumDbContext db)
    {
        if (await db.Users.AnyAsync(u => u.Username == AnonymousUsername)) return;
        db.Users.Add(new User
        {
            Username = AnonymousUsername, DisplayName = "Anonymous reporter", FirstName = "Anonymous", LastName = "reporter",
            Email = "anonymous@vellum.invalid", IsSystemIdentity = true, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow, TimeZone = "UTC",
        });
        await db.SaveChangesAsync();
    }

    public sealed record DemoUser(string Username, string First, string Last, string Title, string Department, string Location, string? ReportsTo, bool Admin = false);

    public static readonly DemoUser[] People =
    [
        new("admin", "Morgan", "Hale", "System Administrator", "IT", "KZO", null, Admin: true),
        new("dokafor", "Dana", "Okafor", "VP Quality & Regulatory", "Quality", "BOS", null),
        new("sortiz", "Sam", "Ortiz", "Quality Manager", "Quality", "KZO", "dokafor"),
        new("jlee", "Jordan", "Lee", "Senior Quality Engineer", "Quality", "KZO", "sortiz"),
        new("pshah", "Priya", "Shah", "Document Control Specialist", "Quality Systems", "KZO", "sortiz"),
        new("tbrooks", "Taylor", "Brooks", "Lead Auditor", "Quality Assurance", "BOS", "sortiz"),
        new("rnovak", "Riley", "Novak", "Regulatory Affairs Manager", "Regulatory", "BOS", "dokafor"),
        new("cwu", "Casey", "Wu", "Supplier Quality Engineer", "Supply Chain", "KZO", "sortiz"),
        new("akim", "Alex", "Kim", "Manufacturing Engineer", "Operations", "KZO", "lgarcia"),
        new("lgarcia", "Luis", "Garcia", "Production Manager", "Operations", "KZO", "dokafor"),
        new("mchen", "Mei", "Chen", "EHS Coordinator", "EHS", "KZO", "lgarcia"),
        new("hmoreau", "Hannah", "Moreau", "QC Laboratory Analyst", "QC Laboratory", "GAL", "sortiz"),
        new("kpatel", "Kiran", "Patel", "Training Coordinator", "Human Resources", "KZO", "dokafor"),
        new("ebaker", "Emerson", "Baker", "Maintenance & Calibration Technician", "Facilities", "KZO", "lgarcia"),
        new("nflores", "Noel", "Flores", "Production Operator", "Operations", "KZO", "lgarcia"),
    ];

    public static readonly (string Key, string Name, string Description, string[] Members)[] GroupsSeed =
    [
        ("all-employees", "All Employees", "Everyone in the organization.", People.Where(p => p.Username != "admin").Select(p => p.Username).ToArray()),
        ("quality", "Quality", "Quality department.", ["dokafor", "sortiz", "jlee", "pshah", "tbrooks", "cwu", "hmoreau"]),
        ("document-control", "Document Controllers", "Check format and release controlled documents (DOC-032).", ["pshah"]),
        ("qa-approvers", "Quality Approvers", "Quality approval authority.", ["sortiz", "dokafor"]),
        ("regulatory", "Regulatory Affairs", "Regulatory reporting and filings.", ["rnovak"]),
        ("ehs", "EHS Team", "Environment, health and safety.", ["mchen"]),
        ("auditors", "Auditors", "Internal auditors.", ["tbrooks", "jlee"]),
        ("operations", "Operations", "Manufacturing operations.", ["lgarcia", "akim", "nflores", "ebaker"]),
        ("engineering", "Engineering", "Manufacturing and design engineering.", ["akim"]),
        ("supplier-quality", "Supplier Quality", "Supplier quality engineering.", ["cwu"]),
        ("qc-lab", "QC Laboratory", "Quality control laboratory.", ["hmoreau"]),
        ("training-coordinators", "Training Coordinators", "Manage courses and assignments.", ["kpatel"]),
        ("mrb", "Material Review Board", "Disposition of nonconforming material.", ["jlee", "sortiz", "akim", "cwu"]),
        ("change-board", "Change Control Board", "Approves changes by approval type.", ["sortiz", "akim", "rnovak", "mchen"]),
        ("maintenance", "Maintenance & Calibration", "Asset maintenance and calibration.", ["ebaker"]),
    ];

    public static async Task SeedAsync(VellumDbContext db)
    {
        var now = DateTime.UtcNow;
        db.Settings.Add(new SystemSettings
        {
            EnvironmentName = "Validation", EnvironmentType = EnvironmentType.Validation, RegulatedMode = true,
            BannerText = "Not for production use", UpdatedAt = now,
            LoginMessage = "Authorized use only. Activity is recorded in the audit trail.",
        });

        var global = new Location { Name = "Global", Code = "GLB", Path = "Global", TimeZone = "UTC" };
        var na = new Location { Name = "North America", Code = "NA", Path = "Global : North America", TimeZone = "America/New_York", Parent = global };
        var usa = new Location { Name = "USA", Code = "USA", Path = "Global : North America : USA", TimeZone = "America/New_York", Parent = na };
        var kzo = new Location { Name = "Kalamazoo", Code = "KZO", Path = "Global : North America : USA : Kalamazoo", TimeZone = "America/Detroit", Parent = usa };
        var bos = new Location { Name = "Boston", Code = "BOS", Path = "Global : North America : USA : Boston", TimeZone = "America/New_York", Parent = usa };
        var eu = new Location { Name = "Europe", Code = "EU", Path = "Global : Europe", TimeZone = "Europe/Dublin", Parent = global };
        var gal = new Location { Name = "Galway", Code = "GAL", Path = "Global : Europe : Ireland : Galway", TimeZone = "Europe/Dublin", Parent = eu };
        var apac = new Location { Name = "Asia Pacific", Code = "APAC", Path = "Global : Asia Pacific", TimeZone = "Asia/Singapore", Parent = global };
        var sin = new Location { Name = "Singapore", Code = "SIN", Path = "Global : Asia Pacific : Singapore", TimeZone = "Asia/Singapore", Parent = apac };
        db.Locations.AddRange(global, na, usa, kzo, bos, eu, gal, apac, sin);
        await db.SaveChangesAsync();
        var byCode = db.Locations.Local.ToDictionary(l => l.Code);

        db.Users.Add(new User
        {
            Username = SystemUsername, DisplayName = "System", FirstName = "System", LastName = "", Email = "system@vellum.invalid",
            IsSystemIdentity = true, Status = UserStatus.Active, CreatedAt = now, TimeZone = "UTC",
        });

        var users = new Dictionary<string, User>();
        foreach (var p in People)
        {
            var hash = PasswordHasher.Hash(DemoContext.Password);
            var u = new User
            {
                Username = p.Username, FirstName = p.First, LastName = p.Last, DisplayName = $"{p.First} {p.Last}",
                Email = $"{p.Username}@acme-medical.example", JobTitle = p.Title, Department = p.Department,
                PasswordHash = hash, PasswordChangedAt = now, IsAdministrator = p.Admin, CreatedAt = now,
                PrimaryLocationId = byCode[p.Location].Id, TimeZone = byCode[p.Location].TimeZone,
            };
            users[p.Username] = u;
            db.Users.Add(u);
        }
        await db.SaveChangesAsync();
        foreach (var u in users.Values) db.PasswordHistory.Add(new PasswordHistory { UserId = u.Id, Hash = u.PasswordHash!, CreatedAt = now });
        foreach (var p in People.Where(p => p.ReportsTo != null)) users[p.Username].ReportsToId = users[p.ReportsTo!].Id;

        foreach (var (key, name, desc, members) in GroupsSeed)
        {
            var g = new Group { Key = key, Name = name, Description = desc };
            foreach (var m in members) g.Members.Add(new GroupMember { User = users[m] });
            db.Groups.Add(g);
        }

        foreach (var (date, name) in Holidays())
            db.CalendarDays.Add(new CalendarDay { Date = date, Name = name, IsWorking = false });
        await db.SaveChangesAsync();
    }

    private static IEnumerable<(DateOnly, string)> Holidays()
    {
        foreach (var y in new[] { 2026, 2027 })
        {
            yield return (new DateOnly(y, 1, 1), "New Year's Day");
            yield return (Nth(y, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
            yield return (Last(y, 5, DayOfWeek.Monday), "Memorial Day");
            yield return (new DateOnly(y, 7, 3).DayOfWeek == DayOfWeek.Friday && y == 2026 ? new DateOnly(y, 7, 3) : new DateOnly(y, 7, 5), "Independence Day (observed)");
            yield return (Nth(y, 9, DayOfWeek.Monday, 1), "Labor Day");
            yield return (Nth(y, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day");
            yield return (Nth(y, 11, DayOfWeek.Thursday, 4).AddDays(1), "Day after Thanksgiving");
            yield return (new DateOnly(y, 12, 25), "Christmas Day");
        }
    }

    private static DateOnly Nth(int y, int m, DayOfWeek d, int n)
    {
        var first = new DateOnly(y, m, 1);
        var offset = ((int)d - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateOnly Last(int y, int m, DayOfWeek d)
    {
        var last = new DateOnly(y, m, DateTime.DaysInMonth(y, m));
        return last.AddDays(-(((int)last.DayOfWeek - (int)d + 7) % 7));
    }
}
