using Gateway.Api.Clients;
using HotChocolate.Subscriptions;
using Manufacturing.Catalog.Client;
using Manufacturing.Core.Models;

namespace Gateway.Api.GraphQL;

public sealed class Query
{
    public async Task<IReadOnlyList<MaterialGql>> GetMaterialsAsync(
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        var materials = await catalogClient.GetAllMaterialsAsync(ct);
        return materials.Select(MaterialGql.FromClient).ToList();
    }

    public async Task<MaterialGql?> GetMaterialByIdAsync(
        Guid id,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        try
        {
            var material = await catalogClient.GetMaterialByIdAsync(id, ct);
            return MaterialGql.FromClient(material);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProductGql>> GetProductsAsync(
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        var products = await catalogClient.GetAllProductsAsync(ct);
        return products.Select(ProductGql.FromClient).ToList();
    }

    public async Task<ProductGql?> GetProductByIdAsync(
        Guid id,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        try
        {
            var product = await catalogClient.GetProductByIdAsync(id, ct);
            return ProductGql.FromClient(product);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task<BillOfMaterialGql?> GetBillOfMaterialAsync(
        Guid productId,
        int revision,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        try
        {
            var bom = await catalogClient.GetBillOfMaterialByProductIdAndRevisionAsync(productId, revision, ct);
            return BillOfMaterialGql.FromClient(bom);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OrderGql>> GetOrdersAsync(
        [Service] IOrdersClient ordersClient,
        CancellationToken ct)
    {
        var orders = await ordersClient.GetAllOrdersAsync(ct);
        return orders.Select(OrderGql.FromCore).ToList();
    }

    public async Task<OrderGql?> GetOrderByIdAsync(
        Guid id,
        [Service] IOrdersClient ordersClient,
        CancellationToken ct)
    {
        var order = await ordersClient.GetOrderByIdAsync(id, ct);
        return order is null ? null : OrderGql.FromCore(order);
    }
}

public sealed record CreateMaterialInput(string Name, string Sku, MeasurementUnit MeasurementUnit);

public sealed record CreateProductInput(string Sku, string Name);

public sealed record BillOfMaterialItemInput(Guid MaterialId, int Quantity);

public sealed record UpsertBillOfMaterialInput(Guid ProductId, int Revision, List<BillOfMaterialItemInput> Materials);

public sealed record OrderItemInput(Guid ProductId, int Revision, int Quantity);

public sealed record CreateOrderInput(List<OrderItemInput> Items);

public sealed class Mutation
{
    public async Task<MaterialGql> CreateMaterialAsync(
        CreateMaterialInput input,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        var created = await catalogClient.CreateMaterialAsync(new CreateMaterialRequest
        {
            Name = input.Name,
            Sku = input.Sku,
            MeasurementUnit = (int)input.MeasurementUnit,
        }, ct);

        return MaterialGql.FromClient(created);
    }

    public async Task<ProductGql> CreateProductAsync(
        CreateProductInput input,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        var created = await catalogClient.CreateProductAsync(new CreateProductRequest
        {
            Sku = input.Sku,
            Name = input.Name,
        }, ct);

        return ProductGql.FromClient(created);
    }

    public async Task<BillOfMaterialGql> CreateBillOfMaterialAsync(
        UpsertBillOfMaterialInput input,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        var created = await catalogClient.CreateBillOfMaterialAsync(new Manufacturing.Catalog.Client.BillOfMaterial
        {
            ProductId = input.ProductId,
            Revision = input.Revision,
            Materials = input.Materials.Select(m => new Record
            {
                MaterialId = m.MaterialId,
                Quantity = m.Quantity,
            }).ToList(),
        }, ct);

        return BillOfMaterialGql.FromClient(created);
    }

    public async Task<OrderGql> CreateOrderAsync(
        CreateOrderInput input,
        [Service] IOrdersClient ordersClient,
        [Service] ITopicEventSender eventSender,
        CancellationToken ct)
    {
        var created = await ordersClient.CreateOrderAsync(
            new CreateOrderDto(
                input.Items.Select(i => new CreateOrderItemDto(i.ProductId, (uint)i.Revision, i.Quantity)).ToList()),
            ct);

        var gqlOrder = OrderGql.FromCore(created);
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
