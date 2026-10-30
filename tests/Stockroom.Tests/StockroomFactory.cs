using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stockroom.Web.Data;
using Stockroom.Web.Domain;

namespace Stockroom.Tests;

public sealed class StockroomFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"stockroom-tests-{Guid.NewGuid():N}.db");
    private readonly SemaphoreSlim userLock = new(1, 1);
    public const string ManagerEmail = "test-manager@stockroom.local";
    public const string ManagerPassword = "Testing.Manager42!";
    public const string ViewerEmail = "test-viewer@stockroom.local";
    public const string ViewerPassword = "Testing.Viewer42!";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Demo:Enabled", "false");
        builder.UseSetting("Bootstrap:Email", ManagerEmail);
        builder.UseSetting("Bootstrap:Password", ManagerPassword);
        builder.UseSetting("RateLimiting:LoginPermitLimit", "1000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<StockroomDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<StockroomDbContext>>();
            services.AddDbContext<StockroomDbContext>(options => options.UseSqlite($"Data Source={databasePath};Default Timeout=15;Foreign Keys=True"));
        });
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    public async Task<HttpClient> SignIn(bool viewer = false)
    {
        var client = Anonymous();
        if (viewer)
        {
            await userLock.WaitAsync();
            try
            {
                using var scope = Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                if (await users.FindByEmailAsync(ViewerEmail) is null)
                {
                    var user = new IdentityUser { UserName = ViewerEmail, Email = ViewerEmail, EmailConfirmed = true };
                    Assert.True((await users.CreateAsync(user, ViewerPassword)).Succeeded);
                    Assert.True((await users.AddToRoleAsync(user, Roles.Viewer)).Succeeded);
                }
            }
            finally { userLock.Release(); }
        }
        await RefreshCsrf(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(viewer ? ViewerEmail : ManagerEmail, viewer ? ViewerPassword : ManagerPassword));
        response.EnsureSuccessStatusCode();
        await RefreshCsrf(client);
        return client;
    }

    public static async Task RefreshCsrf(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-shm", "-wal" }) File.Delete(databasePath + suffix);
            userLock.Dispose();
        }
    }
}
