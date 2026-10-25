using Microsoft.AspNetCore.Authentication;
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

// Modules: behaviors, packaged apps, jobs, home widgets, nav contributors and [ScopedService] classes.
builder.Services.AddVellumModules(typeof(Program).Assembly);

builder.Services.AddAuthentication(SessionDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionDefaults.Scheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("admin"));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AuthorizeAreaFolder("Admin", "/", "Admin");
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
app.UseAuthorization();
app.UseMustChangePassword();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapVellumApi();
app.MapExports();

app.Run();

public partial class Program;
