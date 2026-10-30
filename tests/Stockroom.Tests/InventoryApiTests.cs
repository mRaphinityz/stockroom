using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockroom.Web.Data;
using Stockroom.Web.Domain;

namespace Stockroom.Tests;

public sealed class InventoryApiTests(StockroomFactory factory) : IClassFixture<StockroomFactory>
{
    [Fact]
    public async Task Anonymous_requests_cannot_read_inventory()
    {
        using var client = factory.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_cannot_write()
    {
        using var client = await factory.SignIn(viewer: true);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/products", ProductRequest())).StatusCode);
    }

    [Fact]
    public async Task Cookie_authenticated_writes_require_a_csrf_token()
    {
        using var client = await factory.SignIn();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/products", ProductRequest())).StatusCode);
    }

    [Fact]
    public async Task Manager_can_add_a_product_and_duplicate_skus_are_rejected()
    {
        using var client = await factory.SignIn();
        var request = ProductRequest();
        var response = await client.PostAsJsonAsync("/api/products", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<ProductView>())!;
        Assert.Equal(request.OnHand, product.Available);
        Assert.Equal(request.PriceCents, product.PriceCents);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/products", request with { Sku = request.Sku.ToLowerInvariant() })).StatusCode);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(100000001, 10)]
    [InlineData(100, -1)]
    [InlineData(100, 1000001)]
    public async Task Invalid_price_and_stock_are_rejected(long price, int stock)
    {
        using var client = await factory.SignIn();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/products", ProductRequest() with { PriceCents = price, OnHand = stock })).StatusCode);
    }

    [Fact]
    public async Task Reservation_then_fulfillment_updates_physical_and_reserved_stock()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var response = await Reserve(client, product.Id, 4);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await ReadOrder(response);
        Assert.Equal(4 * product.PriceCents, order.TotalCents);
        AssertStock(await GetProduct(client, product.Id), 10, 4, 6);
        var fulfilled = await client.PostAsync($"/api/orders/{order.Id}/fulfill", null);
        Assert.Equal(OrderStatus.Fulfilled, (await ReadOrder(fulfilled)).Status);
        AssertStock(await GetProduct(client, product.Id), 6, 0, 6);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/orders/{order.Id}/fulfill", null)).StatusCode);
        AssertStock(await GetProduct(client, product.Id), 6, 0, 6);
    }

    [Fact]
    public async Task Cancellation_releases_stock_and_cannot_be_fulfilled_afterwards()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var order = await ReadOrder(await Reserve(client, product.Id, 3));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/orders/{order.Id}/cancel", null)).StatusCode);
        AssertStock(await GetProduct(client, product.Id), 10, 0, 10);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/orders/{order.Id}/fulfill", null)).StatusCode);
    }

    [Fact]
    public async Task Repeated_order_request_returns_same_order_without_reserving_twice()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var key = Guid.NewGuid().ToString();
        var first = await ReadOrder(await Reserve(client, product.Id, 3, key));
        var replay = await Reserve(client, product.Id, 3, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Id, (await ReadOrder(replay)).Id);
        AssertStock(await GetProduct(client, product.Id), 10, 3, 7);
        Assert.Equal(HttpStatusCode.Conflict, (await Reserve(client, product.Id, 2, key)).StatusCode);
    }

    [Fact]
    public async Task Failed_multi_product_reservation_rolls_back_stock_and_audit_events()
    {
        using var client = await factory.SignIn();
        var products = new[] { await CreateProduct(client, 10), await CreateProduct(client, 10) }.OrderBy(product => product.Id).ToArray();
        var request = new CreateOrderRequest("Atomic order", [new(products[0].Id, 3), new(products[1].Id, 11)]);
        var response = await SendOrder(client, request, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        foreach (var product in products) AssertStock(await GetProduct(client, product.Id), 10, 0, 10);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockroomDbContext>();
        var ids = products.Select(product => product.Id).ToArray();
        Assert.False(await db.Movements.AnyAsync(movement => ids.Contains(movement.ProductId) && movement.Kind == "Reserved"));
    }

    [Fact]
    public async Task Simultaneous_orders_cannot_oversell_the_last_unit()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Reserve(client, product.Id, 1)));
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, results.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        AssertStock(await GetProduct(client, product.Id), 1, 1, 0);
    }

    [Fact]
    public async Task Concurrent_retries_create_one_order_and_one_reservation()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Reserve(client, product.Id, 2, key)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode));
        var orders = await Task.WhenAll(responses.Select(ReadOrder));
        Assert.Single(orders.Select(order => order.Id).Distinct());
        AssertStock(await GetProduct(client, product.Id), 10, 2, 8);
    }

    [Fact]
    public async Task Stock_adjustment_rejects_stale_version_and_protects_reservations()
    {
        using var client = await factory.SignIn();
        var original = await CreateProduct(client, 10);
        await Reserve(client, original.Id, 8);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/products/{original.Id}/adjustments", new AdjustStockRequest(5, "Supplier delivery", original.Version))).StatusCode);
        var current = await GetProduct(client, original.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/products/{original.Id}/adjustments", new AdjustStockRequest(-3, "Stock correction", current.Version))).StatusCode);
        var success = await client.PostAsJsonAsync($"/api/products/{original.Id}/adjustments", new AdjustStockRequest(5, "Supplier delivery", current.Version));
        success.EnsureSuccessStatusCode();
        AssertStock(await GetProduct(client, original.Id), 15, 8, 7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task Invalid_order_quantities_are_rejected(int quantity)
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reserve(client, product.Id, quantity)).StatusCode);
        AssertStock(await GetProduct(client, product.Id), 10, 0, 10);
    }

    [Fact]
    public async Task Duplicate_order_lines_and_missing_keys_are_rejected()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var request = new CreateOrderRequest("Test Studio", [new(product.Id, 1), new(product.Id, 2)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendOrder(client, request, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", new CreateOrderRequest("Test Studio", [new(product.Id, 1)]))).StatusCode);
    }

    [Fact]
    public async Task Activity_records_actor_and_changes_in_the_order_transaction()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var order = await ReadOrder(await Reserve(client, product.Id, 2));
        await client.PostAsync($"/api/orders/{order.Id}/cancel", null);
        using var scope = factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<StockroomDbContext>().Movements
            .Where(movement => movement.OrderId == order.Id).OrderBy(movement => movement.Id).ToListAsync();
        Assert.Equal(new[] { "Reserved", "Cancelled" }, events.Select(movement => movement.Kind));
        Assert.Equal(new[] { 2, -2 }, events.Select(movement => movement.ReservedDelta));
        Assert.All(events, movement => Assert.Equal(StockroomFactory.ManagerEmail, movement.Actor));
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_results_are_paginated()
    {
        using var client = await factory.SignIn();
        var product = await CreateProduct(client, 10);
        var result = await client.GetFromJsonAsync<JsonElement>($"/api/products?search={product.Sku.ToLowerInvariant()}&pageSize=1");
        Assert.Equal(1, result.GetProperty("total").GetInt32());
        Assert.Equal(product.Id, result.GetProperty("items")[0].GetProperty("id").GetGuid());
        var next = await client.GetFromJsonAsync<JsonElement>($"/api/products?search={product.Sku}&pageSize=1&page=2");
        Assert.Equal(0, next.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Health_and_authenticated_openapi_document_are_available()
    {
        using var client = await factory.SignIn();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/orders", out _));
    }

    private static CreateProductRequest ProductRequest(int stock = 10) => new($"TEST-{Guid.NewGuid():N}"[..30], "Test keyboard", 12999, stock, 3);

    private static async Task<ProductView> CreateProduct(HttpClient client, int stock)
    {
        var response = await client.PostAsJsonAsync("/api/products", ProductRequest(stock));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductView>())!;
    }

    private static async Task<ProductView> GetProduct(HttpClient client, Guid id) => (await client.GetFromJsonAsync<ProductView>($"/api/products/{id}"))!;
    private static Task<HttpResponseMessage> Reserve(HttpClient client, Guid id, int quantity, string? key = null) =>
        SendOrder(client, new CreateOrderRequest("Test Studio", [new(id, quantity)]), key ?? Guid.NewGuid().ToString());
    private static Task<HttpResponseMessage> SendOrder(HttpClient client, CreateOrderRequest request, string key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(request) };
        message.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(message);
    }
    private static async Task<OrderView> ReadOrder(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderView>(StockroomFactory.Json))!;
    }
    private static void AssertStock(ProductView product, int onHand, int reserved, int available)
    {
        Assert.Equal(onHand, product.OnHand);
        Assert.Equal(reserved, product.Reserved);
        Assert.Equal(available, product.Available);
    }
}
