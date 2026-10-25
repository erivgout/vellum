using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Engine.Security;

namespace Vellum.Tests;

/// <summary>
/// Boots the real application against a throw-away SQL Server database (one per test run), with core seed data and
/// every packaged application installed, but without demo data or the background scheduler.
/// Requires the local SQL Server from docker-compose.yml (override with VELLUM_TEST_SQL).
/// </summary>
public sealed class VellumFixture : IAsyncLifetime
{
    private static readonly string Server = Environment.GetEnvironmentVariable("VELLUM_TEST_SQL")
        ?? "Server=localhost,14333;User Id=sa;Password=Vellum_Dev_2026!;TrustServerCertificate=True";

    public string Database { get; } = $"Vellum_Test_{DateTime.UtcNow:MMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public IServiceProvider Services => Factory.Services;
    /// <summary>Root of the local-folder SharePoint stand-in for this run.</summary>
    public string SharePointRoot => Path.Combine(Path.GetTempPath(), Database, "sharepoint");

    public Task InitializeAsync()
    {
        var cs = $"{Server};Database={Database}";
        Environment.SetEnvironmentVariable("ConnectionStrings__Vellum", cs);
        Environment.SetEnvironmentVariable("Vellum__SeedDemoData", "false");
        Environment.SetEnvironmentVariable("Vellum__DisableScheduler", "true");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Vellum", cs);
            b.UseSetting("Vellum:SeedDemoData", "false");
            b.UseSetting("Vellum:DisableScheduler", "true");
            b.UseSetting("Vellum:FileRoot", Path.Combine(Path.GetTempPath(), Database, "blobs"));
            b.UseSetting("Vellum:MailPickup", Path.Combine(Path.GetTempPath(), Database, "mail"));
            b.UseSetting("Vellum:SharePoint:LocalRoot", SharePointRoot);
        });
        _ = Factory.Services; // runs startup: schema, triggers, core seed, packaged apps
        return Task.CompletedTask;
    }

    /// <summary>A DI scope acting as the given demo user (or anonymous when null).</summary>
    public IServiceScope As(string? username)
    {
        var scope = Services.CreateScope();
        if (username != null)
        {
            var dir = scope.ServiceProvider.GetRequiredService<UserDirectory>();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Impersonate(dir.Get(username)!.Id);
        }
        return scope;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection($"{Server};Database=master");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("vellum")]
public sealed class VellumCollection : ICollectionFixture<VellumFixture>;
