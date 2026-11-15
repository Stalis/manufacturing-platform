using Catalog.Contracts;
using Orders.Contracts;
using Producer.Contracts.Models;

namespace Gateway.Api.GraphQL;

public sealed record MaterialGql(
    Guid Id,
    string Sku,
    string Name,
    MeasurementUnit MeasurementUnit)
{
    public static MaterialGql FromContract(Material m) =>
        new(m.Id, m.Sku, m.Name, m.MeasurementUnit);
}

public sealed record ProductGql(
    Guid Id,
    string Sku,
    string Name)
{
    public static ProductGql FromContract(Product p) =>
        new(p.Id, p.Sku, p.Name);

    public async Task<BillOfMaterialGql?> GetBillOfMaterialAsync(
        uint revision,
        [Service] CatalogClient catalogClient,
        CancellationToken ct) =>
        await catalogClient.GetBillOfMaterialAsync(Id, revision, ct).OrNotFound() is { } bom
            ? BillOfMaterialGql.FromContract(bom)
            : null;
}

public sealed record BillOfMaterialItemGql(
    Guid MaterialId,
    int Quantity)
{
    public async Task<MaterialGql?> GetMaterialAsync(
        [Service] CatalogClient catalogClient,
        CancellationToken ct) =>
        await catalogClient.GetMaterialAsync(MaterialId, ct).OrNotFound() is { } material
            ? MaterialGql.FromContract(material)
            : null;
}

public sealed record BillOfMaterialGql(
    Guid ProductId,
    uint Revision,
    IReadOnlyList<BillOfMaterialItemGql> Materials)
{
    public static BillOfMaterialGql FromContract(BillOfMaterial bom) =>
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
    uint Revision,
    Guid ReservationId,
    int Quantity,
    int CompletedQuantity,
    IReadOnlyList<OrderProductionTaskGql> Tasks)
{
    public async Task<ProductGql?> GetProductAsync(
        [Service] CatalogClient catalogClient,
        CancellationToken ct) =>
        await catalogClient.GetProductAsync(ProductId, ct).OrNotFound() is { } product
            ? ProductGql.FromContract(product)
            : null;
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
    public static OrderGql FromContract(Order order) =>
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
                i.Revision,
                i.ReservationId,
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
