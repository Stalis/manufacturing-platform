using System.Net;
using System.Net.Http.Json;
using Catalog.Contracts;
using Gateway.Api;
using Gateway.Api.GraphQL;
using HotChocolate.Subscriptions;
using Orders.Contracts;
using Producer.Contracts.Models;

namespace Inventory.Worker.Tests;

public class GatewayTests
{
    [Fact]
    public async Task HttpNotFoundReturnsNullOn404AndRethrowsOtherErrors()
    {
        Assert.Equal("ok", await Task.FromResult("ok").OrNotFound());
        Assert.Null(await Task.FromException<string>(
            new HttpRequestException("missing", null, HttpStatusCode.NotFound)).OrNotFound());
        await Assert.ThrowsAsync<HttpRequestException>(() => Task.FromException<string>(
            new HttpRequestException("boom", null, HttpStatusCode.BadGateway)).OrNotFound());
    }

    [Fact]
    public async Task GraphQlQueriesMutationsSubscriptionsAndFieldResolversWork()
    {
        var materialId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var reservationId = Guid.CreateVersion7();

        var material = new Material(materialId, "MAT-1", "Copper", MeasurementUnit.Meter);
        var product = new Product(productId, "PROD-1", "Controller");
        var bom = new BillOfMaterial(productId, 1, [new(materialId, 4)]);
        var order = new Order(orderId, OrderStatus.Reserved, null, 1, 0, 0, 0,
            [new OrderItem(productId, 1, reservationId, 1, 0, [new OrderTask(taskId, OrderTaskStatus.Pending, null)])]);

        using var catalogHttp = new HttpClient(new StubHandler(req => req.Method.Method switch
        {
            "GET" when req.RequestUri!.AbsolutePath == "/api/catalog/materials" => Reply(new[] { material }, HttpStatusCode.OK),
            "GET" when req.RequestUri!.AbsolutePath == $"/api/catalog/materials/{materialId}" => Reply(material, HttpStatusCode.OK),
            "POST" when req.RequestUri!.AbsolutePath == "/api/catalog/materials" => Reply(material, HttpStatusCode.Created),
            "GET" when req.RequestUri!.AbsolutePath == "/api/catalog/products" => Reply(new[] { product }, HttpStatusCode.OK),
            "GET" when req.RequestUri!.AbsolutePath == $"/api/catalog/products/{productId}" => Reply(product, HttpStatusCode.OK),
            "POST" when req.RequestUri!.AbsolutePath == "/api/catalog/products" => Reply(product, HttpStatusCode.Created),
            "GET" when req.RequestUri!.AbsolutePath == $"/api/catalog/bill-of-material/{productId}/revisions/1" => Reply(bom, HttpStatusCode.OK),
            "POST" when req.RequestUri!.AbsolutePath == "/api/catalog/bill-of-material" => Reply(bom, HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://catalog/") };

        using var ordersHttp = new HttpClient(new StubHandler(req => req.Method.Method switch
        {
            "GET" when req.RequestUri!.AbsolutePath == "/api/orders" => Reply(new[] { order }, HttpStatusCode.OK),
            "GET" when req.RequestUri!.AbsolutePath == $"/api/orders/{orderId}" => Reply(order, HttpStatusCode.OK),
            "POST" when req.RequestUri!.AbsolutePath == "/api/orders" => Reply(order, HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://orders/") };

        var catalogClient = new CatalogClient(catalogHttp);
        var ordersClient = new OrdersClient(ordersHttp);
        var query = new Query();
        var mutation = new Mutation();
        var subscription = new Subscription();
        var sender = new RecordingTopicEventSender();

        // Queries: found & 404 cases
        Assert.Single(await query.GetMaterialsAsync(catalogClient, default));
        Assert.Equal("MAT-1", (await query.GetMaterialByIdAsync(materialId, catalogClient, default))?.Sku);
        Assert.Null(await query.GetMaterialByIdAsync(Guid.CreateVersion7(), catalogClient, default));

        Assert.Single(await query.GetProductsAsync(catalogClient, default));
        var productGql = await query.GetProductByIdAsync(productId, catalogClient, default);
        Assert.NotNull(productGql);
        Assert.Null(await query.GetProductByIdAsync(Guid.CreateVersion7(), catalogClient, default));

        var bomGql = await query.GetBillOfMaterialAsync(productId, 1, catalogClient, default);
        Assert.NotNull(bomGql);
        Assert.Null(await query.GetBillOfMaterialAsync(productId, 99, catalogClient, default));

        Assert.Single(await query.GetOrdersAsync(ordersClient, default));
        var orderGql = await query.GetOrderByIdAsync(orderId, ordersClient, default);
        Assert.NotNull(orderGql);
        Assert.Null(await query.GetOrderByIdAsync(Guid.CreateVersion7(), ordersClient, default));

        // Nested GraphQL type resolvers: found & 404 cases
        Assert.NotNull(await productGql.GetBillOfMaterialAsync(1, catalogClient, default));
        Assert.Null(await productGql.GetBillOfMaterialAsync(99, catalogClient, default));

        Assert.Equal("MAT-1", (await bomGql.Materials[0].GetMaterialAsync(catalogClient, default))?.Sku);
        Assert.Null(await new BillOfMaterialItemGql(Guid.CreateVersion7(), 1).GetMaterialAsync(catalogClient, default));

        Assert.Equal("PROD-1", (await orderGql.Items[0].GetProductAsync(catalogClient, default))?.Sku);
        Assert.Null(await (orderGql.Items[0] with { ProductId = Guid.CreateVersion7() }).GetProductAsync(catalogClient, default));

        // Mutations & Subscriptions
        Assert.Equal("MAT-1", (await mutation.CreateMaterialAsync(
            new CreateMaterialInput("Copper", "MAT-1", MeasurementUnit.Meter), catalogClient, default)).Sku);
        Assert.Equal("PROD-1", (await mutation.CreateProductAsync(
            new CreateProductInput("PROD-1", "Controller"), catalogClient, default)).Sku);
        Assert.Equal(1u, (await mutation.CreateBillOfMaterialAsync(
            new UpsertBillOfMaterialInput(productId, 1, [new(materialId, 4)]), catalogClient, default)).Revision);

        var createdOrder = await mutation.CreateOrderAsync(
            new CreateOrderInput([new(productId, 1, 1)]), ordersClient, sender, default);
        Assert.Equal(orderId, createdOrder.Id);
        Assert.Equal((Subscription.OrderUpdatedTopic, createdOrder), Assert.Single(sender.Sent));

        Assert.Same(createdOrder, subscription.OnOrderUpdated(createdOrder));
        var producerEvt = new ProducerEventGql(taskId, ProducerEventType.TaskStarted, "Started", createdOrder);
        Assert.Same(producerEvt, subscription.OnProducerEvent(producerEvt));
    }

    private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status) =>
        new(status) { Content = JsonContent.Create(value) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }

    private sealed class RecordingTopicEventSender : ITopicEventSender
    {
        public List<(string Topic, object? Message)> Sent { get; } = [];

        public ValueTask SendAsync<TMessage>(string topicName, TMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add((topicName, message));
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(string topicName) => ValueTask.CompletedTask;
    }
}
