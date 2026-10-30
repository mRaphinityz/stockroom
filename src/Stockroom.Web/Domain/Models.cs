namespace Stockroom.Web.Domain;

public static class Roles
{
    public const string Manager = "Manager";
    public const string Viewer = "Viewer";
}

public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public long PriceCents { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public int ReorderPoint { get; set; }
    public int Version { get; set; }
    public int Available => OnHand - Reserved;
}

public enum OrderStatus { Reserved, Fulfilled, Cancelled }

public sealed class SalesOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Customer { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Reserved;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public long TotalCents => Lines.Sum(line => line.UnitPriceCents * line.Quantity);

    public void TransitionTo(OrderStatus target)
    {
        if (Status != OrderStatus.Reserved || target == OrderStatus.Reserved)
            throw new BusinessException(409, "Invalid order transition", "Only a reserved order can be fulfilled or cancelled.");
        Status = target;
    }
}

public sealed class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SalesOrderId { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public required string ProductName { get; set; }
    public required string Sku { get; set; }
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}

public sealed class StockMovement
{
    public long Id { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid? OrderId { get; set; }
    public required string Kind { get; set; }
    public int OnHandDelta { get; set; }
    public int ReservedDelta { get; set; }
    public required string Reason { get; set; }
    public required string Actor { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BusinessException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}
