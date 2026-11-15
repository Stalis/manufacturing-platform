using Catalog.Contracts;
using HotChocolate.Subscriptions;
using Orders.Contracts;

namespace Gateway.Api.GraphQL;

public sealed class Query
{
    public async Task<IReadOnlyList<MaterialGql>> GetMaterialsAsync(
        [Service] CatalogClient catalogClient,
        CancellationToken ct)
    {
        var materials = await catalogClient.ListMaterialsAsync(ct);
        return materials.Select(MaterialGql.FromContract).ToList();
    }

    public async Task<MaterialGql?> GetMaterialByIdAsync(
        Guid id,
        [Service] CatalogClient catalogClient,
        CancellationToken ct) =>
        await catalogClient.GetMaterialAsync(id, ct).OrNotFound() is { } material
            ? MaterialGql.FromContract(material)
            : null;

    public async Task<IReadOnlyList<ProductGql>> GetProductsAsync(
        [Service] CatalogClient catalogClient,
        CancellationToken ct)
    {
        var products = await catalogClient.ListProductsAsync(ct);
        return products.Select(ProductGql.FromContract).ToList();
    }

    public async Task<ProductGql?> GetProductByIdAsync(
        Guid id,
        [Service] CatalogClient catalogClient,
        CancellationToken ct) =>
        await catalogClient.GetProductAsync(id, ct).OrNotFound() is { } product
            ? ProductGql.FromContract(product)
            : null;

    public async Task<BillOfMaterialGql?> GetBillOfMaterialAsync(
        Guid productId,
        uint revision,
        [Service] CatalogClient catalogClient,
        CancellationToken ct) =>
        await catalogClient.GetBillOfMaterialAsync(productId, revision, ct).OrNotFound() is { } bom
            ? BillOfMaterialGql.FromContract(bom)
            : null;

    public async Task<IReadOnlyList<OrderGql>> GetOrdersAsync(
        [Service] OrdersClient ordersClient,
        CancellationToken ct)
    {
        var orders = await ordersClient.ListOrdersAsync(ct);
        return orders.Select(OrderGql.FromContract).ToList();
    }

    public async Task<OrderGql?> GetOrderByIdAsync(
        Guid id,
        [Service] OrdersClient ordersClient,
        CancellationToken ct) =>
        await ordersClient.GetOrderAsync(id, ct).OrNotFound() is { } order
            ? OrderGql.FromContract(order)
            : null;
}

public sealed record CreateMaterialInput(string Name, string Sku, MeasurementUnit MeasurementUnit);

public sealed record CreateProductInput(string Sku, string Name);

public sealed record BillOfMaterialItemInput(Guid MaterialId, int Quantity);

public sealed record UpsertBillOfMaterialInput(Guid ProductId, uint Revision, List<BillOfMaterialItemInput> Materials);

public sealed record OrderItemInput(Guid ProductId, uint Revision, int Quantity);

public sealed record CreateOrderInput(List<OrderItemInput> Items);

public sealed class Mutation
{
    public async Task<MaterialGql> CreateMaterialAsync(
        CreateMaterialInput input,
        [Service] CatalogClient catalogClient,
        CancellationToken ct)
    {
        var created = await catalogClient.CreateMaterialAsync(
            new MaterialInput(input.Sku, input.Name, input.MeasurementUnit), ct);

        return MaterialGql.FromContract(created);
    }

    public async Task<ProductGql> CreateProductAsync(
        CreateProductInput input,
        [Service] CatalogClient catalogClient,
        CancellationToken ct)
    {
        var created = await catalogClient.CreateProductAsync(new ProductInput(input.Sku, input.Name), ct);

        return ProductGql.FromContract(created);
    }

    public async Task<BillOfMaterialGql> CreateBillOfMaterialAsync(
        UpsertBillOfMaterialInput input,
        [Service] CatalogClient catalogClient,
        CancellationToken ct)
    {
        var created = await catalogClient.CreateBillOfMaterialAsync(new BillOfMaterial(
            input.ProductId,
            input.Revision,
            input.Materials.Select(m => new BillOfMaterialItem(m.MaterialId, m.Quantity)).ToArray()), ct);

        return BillOfMaterialGql.FromContract(created);
    }

    public async Task<OrderGql> CreateOrderAsync(
        CreateOrderInput input,
        [Service] OrdersClient ordersClient,
        [Service] ITopicEventSender eventSender,
        CancellationToken ct)
    {
        var created = await ordersClient.CreateOrderAsync(
            new CreateOrderRequest(
                input.Items.Select(i => new CreateOrderItem(i.ProductId, i.Revision, i.Quantity)).ToArray()),
            ct);

        var gqlOrder = OrderGql.FromContract(created);
        await eventSender.SendAsync(Subscription.OrderUpdatedTopic, gqlOrder, ct);
        return gqlOrder;
    }
}

public sealed class Subscription
{
    public const string OrderUpdatedTopic = "OrderUpdated";
    public const string ProducerEventTopic = "ProducerEventReceived";

    [Subscribe]
    [Topic(OrderUpdatedTopic)]
    public OrderGql OnOrderUpdated([EventMessage] OrderGql order) => order;

    [Subscribe]
    [Topic(ProducerEventTopic)]
    public ProducerEventGql OnProducerEvent([EventMessage] ProducerEventGql producerEvent) => producerEvent;
}
