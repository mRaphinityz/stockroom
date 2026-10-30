using Stockroom.Web.Domain;

namespace Stockroom.Tests;

public sealed class OrderStateTests
{
    [Theory]
    [InlineData(OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Cancelled)]
    public void A_reserved_order_can_reach_a_terminal_state(OrderStatus target)
    {
        var order = Order();
        order.TransitionTo(target);
        Assert.Equal(target, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Fulfilled, OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Fulfilled, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Fulfilled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Reserved, OrderStatus.Reserved)]
    public void Terminal_orders_and_no_op_transitions_are_rejected(OrderStatus start, OrderStatus target)
    {
        var order = Order();
        order.Status = start;
        Assert.Equal(409, Assert.Throws<BusinessException>(() => order.TransitionTo(target)).StatusCode);
        Assert.Equal(start, order.Status);
    }

    [Fact]
    public void Totals_use_integer_cents_and_snapshot_prices()
    {
        var order = Order();
        order.Lines.Add(new OrderLine { ProductName = "Cable", Sku = "CB-01", UnitPriceCents = 1999, Quantity = 3 });
        order.Lines.Add(new OrderLine { ProductName = "Hub", Sku = "HB-01", UnitPriceCents = 7900, Quantity = 2 });
        Assert.Equal(21797, order.TotalCents);
    }

    private static SalesOrder Order() => new() { Customer = "Test Studio", IdempotencyKey = "unit-test-key", RequestHash = "test" };
}
