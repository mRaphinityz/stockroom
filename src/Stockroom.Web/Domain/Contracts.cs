namespace Stockroom.Web.Domain;

public sealed record CreateProductRequest(string Sku, string Name, long PriceCents, int OnHand, int ReorderPoint);
public sealed record AdjustStockRequest(int Delta, string Reason, int ExpectedVersion);
public sealed record RequestedLine(Guid ProductId, int Quantity);
public sealed record CreateOrderRequest(string Customer, List<RequestedLine> Lines);
public sealed record LoginRequest(string Email, string Password);
public sealed record ProductView(Guid Id, string Sku, string Name, long PriceCents, int OnHand, int Reserved, int Available, int ReorderPoint, int Version)
{
    public static ProductView From(Product product) => new(product.Id, product.Sku, product.Name,
        product.PriceCents, product.OnHand, product.Reserved, product.Available, product.ReorderPoint, product.Version);
}

public sealed record OrderLineView(Guid ProductId, string Sku, string Name, int Quantity, long UnitPriceCents);
public sealed record OrderView(Guid Id, string Customer, OrderStatus Status, DateTime CreatedAt, DateTime UpdatedAt, long TotalCents, List<OrderLineView> Lines)
{
    public static OrderView From(SalesOrder order) => new(order.Id, order.Customer, order.Status, order.CreatedAt,
        order.UpdatedAt, order.TotalCents, order.Lines.Select(line => new OrderLineView(line.ProductId, line.Sku,
            line.ProductName, line.Quantity, line.UnitPriceCents)).ToList());
}
