using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Stockroom.Web.Data;
using Stockroom.Web.Domain;

namespace Stockroom.Web.Services;

public sealed partial class InventoryService(StockroomDbContext db, TimeProvider clock)
{
    public async Task<Product> CreateProduct(CreateProductRequest request, string actor, CancellationToken ct)
    {
        var sku = request.Sku?.Trim().ToUpperInvariant() ?? "";
        var name = request.Name?.Trim() ?? "";
        if (!SkuPattern().IsMatch(sku) || name.Length is < 2 or > 100 ||
            request.PriceCents is < 0 or > 100_000_000 || request.OnHand is < 0 or > 1_000_000 ||
            request.ReorderPoint is < 0 or > 1_000_000)
            throw new BusinessException(400, "Invalid product", "Use a 2–32 character SKU (letters, numbers, hyphens), a 2–100 character name, and valid nonnegative price and stock values.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Products.AnyAsync(product => product.Sku == sku, ct))
            throw new BusinessException(409, "SKU already exists", $"A product with SKU {sku} already exists.");
        var product = new Product { Sku = sku, Name = name, PriceCents = request.PriceCents, OnHand = request.OnHand, ReorderPoint = request.ReorderPoint };
        db.Products.Add(product);
        Record(product.Id, null, "Opening", request.OnHand, 0, "Opening inventory", actor);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return product;
    }

    public async Task<Product> AdjustStock(Guid id, AdjustStockRequest request, string actor, CancellationToken ct)
    {
        var reason = request.Reason?.Trim() ?? "";
        if (request.Delta == 0 || request.Delta is < -1_000_000 or > 1_000_000 || reason.Length is < 3 or > 200)
            throw new BusinessException(400, "Invalid adjustment", "Enter a nonzero quantity between -1,000,000 and 1,000,000 and a reason of 3–200 characters.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(product => product.Id == id, ct)
            ?? throw new BusinessException(404, "Product not found", "This product does not exist.");
        if (product.Version != request.ExpectedVersion)
            throw new BusinessException(409, "Inventory changed", "Refresh the inventory and try again; another operation changed this product.");
        var changed = await db.Products.Where(product => product.Id == id && product.Version == request.ExpectedVersion &&
                product.OnHand + request.Delta >= product.Reserved && product.OnHand + request.Delta <= 1_000_000)
            .ExecuteUpdateAsync(update => update.SetProperty(product => product.OnHand, product => product.OnHand + request.Delta)
                .SetProperty(product => product.Version, product => product.Version + 1), ct);
        if (changed == 0)
            throw new BusinessException(409, "Adjustment unavailable", "Stock cannot fall below reserved units or exceed 1,000,000 units.");
        Record(id, null, "Adjustment", request.Delta, 0, reason, actor);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await db.Products.AsNoTracking().SingleAsync(product => product.Id == id, ct);
    }

    public async Task<(SalesOrder Order, bool Replayed)> Reserve(CreateOrderRequest request, string key, string actor, CancellationToken ct)
    {
        var customer = request.Customer?.Trim() ?? "";
        if (customer.Length is < 2 or > 100 || request.Lines is null || request.Lines.Count is < 1 or > 25 ||
            request.Lines.Any(line => line is null || line.ProductId == Guid.Empty || line.Quantity is < 1 or > 10_000) ||
            request.Lines.Select(line => line.ProductId).Distinct().Count() != request.Lines.Count)
            throw new BusinessException(400, "Invalid order", "Enter a customer name (2–100 characters) and 1–25 distinct products with quantities of 1–10,000.");
        if (!KeyPattern().IsMatch(key))
            throw new BusinessException(400, "Invalid idempotency key", "Send an Idempotency-Key header with 8–100 letters, numbers, underscores, or hyphens.");

        var lines = request.Lines.OrderBy(line => line.ProductId).ToList();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { customer, lines }))));

        // SQLite opens an immediate write transaction: all lines and the audit trail commit together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.Orders.Include(order => order.Lines).SingleOrDefaultAsync(order => order.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
                throw new BusinessException(409, "Idempotency key reused", "This key belongs to a different order. Use a new key for a new request.");
            return (existing, true);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var order = new SalesOrder { Customer = customer, IdempotencyKey = key, RequestHash = hash, CreatedAt = now, UpdatedAt = now };
        foreach (var line in lines)
        {
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(product => product.Id == line.ProductId, ct)
                ?? throw new BusinessException(404, "Product not found", "One of the selected products no longer exists.");
            // The predicate enforces availability in the database, including concurrent requests.
            var changed = await db.Products.Where(product => product.Id == line.ProductId && product.OnHand - product.Reserved >= line.Quantity)
                .ExecuteUpdateAsync(update => update.SetProperty(product => product.Reserved, product => product.Reserved + line.Quantity)
                    .SetProperty(product => product.Version, product => product.Version + 1), ct);
            if (changed == 0)
                throw new BusinessException(409, "Insufficient stock", $"There are not enough available units of {product.Name}. Refresh inventory and try a smaller quantity.");
            order.Lines.Add(new OrderLine { ProductId = product.Id, ProductName = product.Name, Sku = product.Sku, Quantity = line.Quantity, UnitPriceCents = product.PriceCents });
            Record(product.Id, order.Id, "Reserved", 0, line.Quantity, $"Order for {customer}", actor);
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (order, false);
    }

    public async Task<SalesOrder> Transition(Guid id, OrderStatus status, string actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var order = await db.Orders.Include(order => order.Lines).SingleOrDefaultAsync(order => order.Id == id, ct)
            ?? throw new BusinessException(404, "Order not found", "This order does not exist.");
        order.TransitionTo(status);
        foreach (var line in order.Lines)
        {
            var onHandDelta = status == OrderStatus.Fulfilled ? -line.Quantity : 0;
            var changed = await db.Products.Where(product => product.Id == line.ProductId && product.Reserved >= line.Quantity)
                .ExecuteUpdateAsync(update => update.SetProperty(product => product.Reserved, product => product.Reserved - line.Quantity)
                    .SetProperty(product => product.OnHand, product => product.OnHand + onHandDelta)
                    .SetProperty(product => product.Version, product => product.Version + 1), ct);
            if (changed != 1)
                throw new BusinessException(409, "Stock conflict", "The reservation could not be updated. No changes were saved.");
            Record(line.ProductId, order.Id, status.ToString(), onHandDelta, -line.Quantity, $"Order for {order.Customer}", actor);
        }
        order.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return order;
    }

    private void Record(Guid productId, Guid? orderId, string kind, int onHand, int reserved, string reason, string actor) =>
        db.Movements.Add(new StockMovement
        {
            ProductId = productId,
            OrderId = orderId,
            Kind = kind,
            OnHandDelta = onHand,
            ReservedDelta = reserved,
            Reason = reason,
            Actor = actor,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        });

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
    [GeneratedRegex("^[a-zA-Z0-9_-]{8,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
