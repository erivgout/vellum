using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vellum.Data;
using Vellum.Engine;
using Vellum.Engine.Audit;
using Vellum.Engine.Behaviors;
using Vellum.Engine.Notifications;
using Vellum.Engine.Records;
using Vellum.Engine.Search;
using Vellum.Engine.Security;
using Vellum.Engine.Views;
using Vellum.Engine.Workflow;
using Vellum.Infrastructure;
using Vellum.Infrastructure.Api;
using Vellum.Infrastructure.Auth;
using Vellum.Infrastructure.Files;
using Vellum.Infrastructure.Jobs;
using Vellum.Infrastructure.Ui;
using Vellum.Packaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<VellumDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Vellum"), sql => sql.CommandTimeout(300))
     .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.FirstWithoutOrderByAndFilterWarning)));

// Configuration and directory caches
builder.Services.AddSingleton<ConfigCache>();
builder.Services.AddSingleton<UserDirectory>();

// Engine
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NumberingService>();
builder.Services.AddScoped<BusinessCalendar>();
builder.Services.AddScoped<AssignmentService>();
builder.Services.AddScoped<LockService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<SignatureService>();
builder.Services.AddScoped<RecordService>();
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddScoped<BehaviorRegistry>();
builder.Services.AddScoped<ViewService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<FileStore>();
builder.Services.AddScoped<PackagedAppInstaller>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<UiContext>();
builder.Services.AddSingleton<DbInitializer>();
builder.Services.AddSingleton<Scheduler>();
if (!builder.Configuration.GetValue("Vellum:DisableScheduler", false))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());
builder.Services.AddHttpContextAccessor();

// Data Protection encrypts stored integration secrets (SharePoint, eMDR) and short-lived tickets. The key ring must survive
// restarts and be shared by every instance, so it lives in a fixed folder (Vellum:DataProtection:KeysPath; put it on a
// persistent volume in containers). On Windows the keys are encrypted at rest with machine-scope DPAPI.
var keysPath = builder.Configuration["Vellum:DataProtection:KeysPath"] is { Length: > 0 } configuredKeys
    ? configuredKeys : Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Vellum").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

// Modules: behaviors, packaged apps, jobs, home widgets, nav contributors and [ScopedService] classes.
builder.Services.AddVellumModules(typeof(Program).Assembly);

builder.Services.AddAuthentication(SessionDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionDefaults.Scheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("admin"))
    // SEC-804: the supplier portal is for external users only (internal users get /forbidden there).
    .AddPolicy(SessionDefaults.PortalPolicy, p => p.RequireAuthenticatedUser().RequireClaim(SessionDefaults.ExternalClaim, "1"));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.Add(new AreaSignInConvention());
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AuthorizeAreaFolder("Admin", "/", "Admin");
    o.Conventions.AuthorizeAreaFolder("Portal", "/", SessionDefaults.PortalPolicy);
    o.Conventions.AllowAnonymousToAreaPage("Portal", "/Welcome"); // one-time set-password link from the invitation email
});
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

var app = builder.Build();

LockService.TimeoutMinutes = builder.Configuration.GetValue("Vellum:LockTimeoutMinutes", 30);
await app.Services.GetRequiredService<DbInitializer>().InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.UseSecurityHeaders();
app.UseRouting();
app.UseAuthentication();
app.UseExternalUserBoundary(); // SEC-804 / INV-088: external users reach only /portal (deny by default)
app.UseAuthorization();
app.UseMustChangePassword();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapVellumApi();
app.MapExports();

app.Run();

public partial class Program;
