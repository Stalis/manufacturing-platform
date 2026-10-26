using Gateway.Api.Clients;
using Manufacturing.Catalog.Client;
using Manufacturing.Core.Models;
using Orders.Core.Models;
using Producer.Contracts.Models;
using ClientBillOfMaterial = Manufacturing.Catalog.Client.BillOfMaterial;
using ClientMaterial = Manufacturing.Catalog.Client.Material;
using ClientProduct = Manufacturing.Catalog.Client.Product;

namespace Gateway.Api.GraphQL;

public sealed record MaterialGql(
    Guid Id,
    string Sku,
    string Name,
    MeasurementUnit MeasurementUnit)
{
    public static MaterialGql FromClient(ClientMaterial m) =>
        new(m.Id, m.Sku, m.Name, (MeasurementUnit)m.MeasurementUnit);
}

public sealed record ProductGql(
    Guid Id,
    string Sku,
    string Name)
{
    public static ProductGql FromClient(ClientProduct p) =>
        new(p.Id, p.Sku, p.Name);

    public async Task<BillOfMaterialGql?> GetBillOfMaterialAsync(
        int revision,
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        try
        {
            var bom = await catalogClient.GetBillOfMaterialByProductIdAndRevisionAsync(Id, revision, ct);
            return BillOfMaterialGql.FromClient(bom);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }
}

public sealed record BillOfMaterialItemGql(
    Guid MaterialId,
    int Quantity)
{
    public async Task<MaterialGql?> GetMaterialAsync(
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        try
        {
            var material = await catalogClient.GetMaterialByIdAsync(MaterialId, ct);
            return MaterialGql.FromClient(material);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }
}

public sealed record BillOfMaterialGql(
    Guid ProductId,
    int Revision,
    IReadOnlyList<BillOfMaterialItemGql> Materials)
{
    public static BillOfMaterialGql FromClient(ClientBillOfMaterial bom) =>
        new(
            bom.ProductId,
            bom.Revision,
            bom.Materials.Select(m => new BillOfMaterialItemGql(m.MaterialId, m.Quantity)).ToList());
}

public sealed record OrderProductionTaskGql(
    Guid TaskId,
    OrderTaskStatus Status,
    string? LastMessage);

public sealed record OrderItemGql(
    Guid ProductId,
    int Revision,
    Guid ReservationId,
    int Quantity,
    int CompletedQuantity,
    IReadOnlyList<OrderProductionTaskGql> Tasks)
{
    public async Task<ProductGql?> GetProductAsync(
        [Service] ICatalogClient catalogClient,
        CancellationToken ct)
    {
        try
        {
            var product = await catalogClient.GetProductByIdAsync(ProductId, ct);
            return ProductGql.FromClient(product);
        }
        catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }
}

public sealed record OrderGql(
    Guid Id,
    OrderStatus Status,
    string? ErrorMessage,
    int TotalTasksCount,
    int CompletedTasksCount,
    int InProgressTasksCount,
    double ProgressPercentage,
    IReadOnlyList<OrderItemGql> Items)
{
    public static OrderGql FromCore(Order order) =>
        new(
            order.Id,
            order.Status,
            order.ErrorMessage,
            order.TotalTasksCount,
            order.CompletedTasksCount,
            order.InProgressTasksCount,
            order.ProgressPercentage,
            order.Items.Select(i => new OrderItemGql(
                i.ProductId,
                (int)i.Revision,
                i.BOMRevision,
                i.Quantity,
                i.CompletedQuantity,
                i.Tasks.Select(t => new OrderProductionTaskGql(t.TaskId, t.Status, t.LastMessage)).ToList()
            )).ToList());
}

public sealed record ProducerEventGql(
    Guid TaskId,
    ProducerEventType Type,
    string Message,
    OrderGql? UpdatedOrder);
