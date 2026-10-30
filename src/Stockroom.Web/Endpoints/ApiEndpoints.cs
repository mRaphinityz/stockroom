using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Stockroom.Web.Data;
using Stockroom.Web.Domain;
using Stockroom.Web.Services;

namespace Stockroom.Web.Endpoints;

public static class ApiEndpoints
{
    public static void MapStockroomApi(this WebApplication app)
    {
        app.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery, IConfiguration config) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new
            {
                authenticated = context.User.Identity?.IsAuthenticated == true,
                email = context.User.Identity?.Name,
                role = context.User.FindFirstValue(ClaimTypes.Role),
                csrfToken = tokens.RequestToken,
                demo = config.GetValue<bool>("Demo:Enabled")
            });
        });

        app.MapPost("/api/auth/login", async (LoginRequest request, SignInManager<IdentityUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
                string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
                return Results.Problem(statusCode: 400, title: "Enter your email and password");
            var result = await signIn.PasswordSignInAsync(request.Email.Trim(), request.Password, false, lockoutOnFailure: true);
            return result.Succeeded ? Results.Ok(new { message = "Signed in" }) :
                Results.Problem(statusCode: 401, title: "Unable to sign in", detail: "Check your credentials or wait a few minutes if you have made several attempts.");
        }).RequireRateLimiting("login");

        app.MapPost("/api/auth/logout", async (SignInManager<IdentityUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/dashboard", async (StockroomDbContext db, CancellationToken ct) =>
        {
            var products = await db.Products.AsNoTracking().ToListAsync(ct);
            var orders = await db.Orders.AsNoTracking().Include(order => order.Lines).ToListAsync(ct);
            return Results.Ok(new
            {
                totalProducts = products.Count,
                availableUnits = products.Sum(product => product.Available),
                reservedUnits = products.Sum(product => product.Reserved),
                inventoryValueCents = products.Sum(product => product.OnHand * product.PriceCents),
                lowStockCount = products.Count(product => product.Available <= product.ReorderPoint),
                openOrders = orders.Count(order => order.Status == OrderStatus.Reserved),
                fulfilledValueCents = orders.Where(order => order.Status == OrderStatus.Fulfilled).Sum(order => order.TotalCents),
                orderCounts = new
                {
                    reserved = orders.Count(order => order.Status == OrderStatus.Reserved),
                    fulfilled = orders.Count(order => order.Status == OrderStatus.Fulfilled),
                    cancelled = orders.Count(order => order.Status == OrderStatus.Cancelled)
                }
            });
        });

        api.MapGet("/products", async (StockroomDbContext db, string? search, int? page, int? pageSize, CancellationToken ct) =>
        {
            var current = Math.Clamp(page ?? 1, 1, 100_000);
            var size = Math.Clamp(pageSize ?? 100, 1, 100);
            var query = db.Products.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToUpperInvariant();
                query = query.Where(product => product.Sku.Contains(term) || product.Name.ToUpper().Contains(term));
            }
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(product => product.Sku).Skip((current - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new { items = items.Select(ProductView.From), total, page = current, pageSize = size });
        });

        api.MapPost("/products", async (CreateProductRequest request, InventoryService inventory, HttpContext context, CancellationToken ct) =>
        {
            var product = await inventory.CreateProduct(request, Actor(context), ct);
            return Results.Created($"/api/products/{product.Id}", ProductView.From(product));
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Manager));

        api.MapGet("/products/{id:guid}", async (Guid id, StockroomDbContext db, CancellationToken ct) =>
        {
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(product => product.Id == id, ct);
            return product is null ? Results.NotFound() : Results.Ok(ProductView.From(product));
        });

        api.MapPost("/products/{id:guid}/adjustments", async (Guid id, AdjustStockRequest request, InventoryService inventory, HttpContext context, CancellationToken ct) =>
            Results.Ok(ProductView.From(await inventory.AdjustStock(id, request, Actor(context), ct))))
            .RequireAuthorization(policy => policy.RequireRole(Roles.Manager));

        api.MapGet("/orders", async (StockroomDbContext db, OrderStatus? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            var current = Math.Clamp(page ?? 1, 1, 100_000);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);
            var query = db.Orders.AsNoTracking();
            if (status.HasValue) query = query.Where(order => order.Status == status);
            var total = await query.CountAsync(ct);
            var orders = await query.Include(order => order.Lines).OrderByDescending(order => order.CreatedAt)
                .ThenBy(order => order.Id).Skip((current - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new { items = orders.Select(OrderView.From), total, page = current, pageSize = size });
        });

        api.MapGet("/orders/{id:guid}", async (Guid id, StockroomDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().Include(order => order.Lines).SingleOrDefaultAsync(order => order.Id == id, ct);
            return order is null ? Results.NotFound() : Results.Ok(OrderView.From(order));
        });

        api.MapPost("/orders", async (CreateOrderRequest request, InventoryService inventory, HttpContext context, CancellationToken ct) =>
        {
            var (order, replayed) = await inventory.Reserve(request, context.Request.Headers["Idempotency-Key"].ToString(), Actor(context), ct);
            context.Response.Headers["Idempotent-Replayed"] = replayed.ToString().ToLowerInvariant();
            return replayed ? Results.Ok(OrderView.From(order)) : Results.Created($"/api/orders/{order.Id}", OrderView.From(order));
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Manager));

        api.MapPost("/orders/{id:guid}/fulfill", async (Guid id, InventoryService inventory, HttpContext context, CancellationToken ct) =>
            Results.Ok(OrderView.From(await inventory.Transition(id, OrderStatus.Fulfilled, Actor(context), ct))))
            .RequireAuthorization(policy => policy.RequireRole(Roles.Manager));

        api.MapPost("/orders/{id:guid}/cancel", async (Guid id, InventoryService inventory, HttpContext context, CancellationToken ct) =>
            Results.Ok(OrderView.From(await inventory.Transition(id, OrderStatus.Cancelled, Actor(context), ct))))
            .RequireAuthorization(policy => policy.RequireRole(Roles.Manager));

        api.MapGet("/activity", async (StockroomDbContext db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var current = Math.Clamp(page ?? 1, 1, 100_000);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);
            var total = await db.Movements.CountAsync(ct);
            var items = await db.Movements.AsNoTracking().OrderByDescending(movement => movement.Id)
                .Skip((current - 1) * size).Take(size).Select(movement => new
                {
                    movement.Id,
                    movement.Kind,
                    movement.ProductId,
                    productName = movement.Product.Name,
                    movement.Product.Sku,
                    movement.OrderId,
                    movement.OnHandDelta,
                    movement.ReservedDelta,
                    movement.Reason,
                    movement.Actor,
                    movement.CreatedAt
                }).ToListAsync(ct);
            return Results.Ok(new { items, total, page = current, pageSize = size });
        });
    }

    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? "unknown";
}
