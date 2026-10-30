using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Stockroom.Web.Domain;
using Stockroom.Web.Services;

namespace Stockroom.Web.Data;

public static class DatabaseSeeder
{
    public static async Task Initialize(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockroomDbContext>();
        var demo = configuration.GetValue<bool>("Demo:Enabled");
        if (demo && !environment.IsDevelopment())
            throw new InvalidOperationException("Demo accounts are permitted only in Development. Configure a Bootstrap account for other environments.");

        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { Roles.Manager, Roles.Viewer })
            if (!await roles.RoleExistsAsync(role)) Ensure(await roles.CreateAsync(new IdentityRole(role)));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        if (demo)
        {
            await CreateUser(users, "manager@stockroom.local", "Demo.Manager42!", Roles.Manager);
            await CreateUser(users, "viewer@stockroom.local", "Demo.Viewer42!", Roles.Viewer);
        }
        var bootstrapEmail = configuration["Bootstrap:Email"];
        var bootstrapPassword = configuration["Bootstrap:Password"];
        if (!string.IsNullOrWhiteSpace(bootstrapEmail) && !string.IsNullOrWhiteSpace(bootstrapPassword))
            await CreateUser(users, bootstrapEmail, bootstrapPassword, Roles.Manager);

        if (!demo || await db.Products.AnyAsync()) return;
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
        var samples = new[]
        {
            new CreateProductRequest("KB-001", "Mechanical keyboard", 12900, 48, 12),
            new CreateProductRequest("MS-002", "Wireless mouse", 5900, 72, 15),
            new CreateProductRequest("HB-003", "USB-C hub", 7900, 8, 10),
            new CreateProductRequest("MN-004", "27-inch display", 32900, 24, 8),
            new CreateProductRequest("DS-005", "Laptop stand", 4900, 36, 10),
            new CreateProductRequest("WC-006", "HD webcam", 8900, 6, 8),
            new CreateProductRequest("HP-007", "Studio headphones", 14900, 30, 10),
            new CreateProductRequest("CB-008", "Braided USB-C cable", 1900, 120, 25)
        };
        var products = new List<Product>();
        foreach (var sample in samples) products.Add(await inventory.CreateProduct(sample, "demo-seed", default));
        var first = await inventory.Reserve(new CreateOrderRequest("Northstar Studio", [new(products[0].Id, 4), new(products[1].Id, 4)]), "seed-northstar-001", "demo-seed", default);
        await inventory.Transition(first.Order.Id, OrderStatus.Fulfilled, "demo-seed", default);
        await inventory.Reserve(new CreateOrderRequest("Acme Design Co.", [new(products[3].Id, 3), new(products[4].Id, 3)]), "seed-acme-002", "demo-seed", default);
        await inventory.Reserve(new CreateOrderRequest("Juniper Labs", [new(products[6].Id, 5)]), "seed-juniper-003", "demo-seed", default);
    }

    private static async Task CreateUser(UserManager<IdentityUser> users, string email, string password, string role)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            Ensure(await users.CreateAsync(user, password));
        }
        if (!await users.IsInRoleAsync(user, role)) Ensure(await users.AddToRoleAsync(user, role));
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
    }
}
