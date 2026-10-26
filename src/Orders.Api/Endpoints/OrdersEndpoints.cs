using Inventory.Contracts.Models;
using Manufacturing.Catalog.Client;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Orders.Api.Persistence;
using Orders.Core.Models;
using Producer.Contracts.Models;
using CoreBillOfMaterial = Manufacturing.Core.Models.BillOfMaterial;

namespace Orders.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/orders")
                       .WithTags("Orders");

        group.MapGet("/", GetAll)
             .WithName("GetAllOrders")
             .Produces<List<Order>>();

        group.MapGet("/{id:guid}", GetById)
             .WithName("GetOrderById")
             .Produces<Order>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateOrder)
             .WithName("CreateOrder")
             .Accepts<CreateOrderRequest>("application/json")
             .Produces<Order>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    public static async Task<IResult> GetAll(OrdersDbContext db, CancellationToken cs)
    {
        var orders = await db.Orders.AsNoTracking().ToListAsync(cs);
        return Results.Ok(orders);
    }

    public static async Task<IResult> GetById(Guid id, OrdersDbContext db, CancellationToken cs)
    {
        var order = await db.Orders.FindAsync([id], cs);

        return order is not null ? Results.Ok(order) : Results.NotFound();
    }

    public static async Task<IResult> CreateOrder(
        CreateOrderRequest request,
        OrdersDbContext db,
        ICatalogClient catalogClient,
        INatsConnection nats,
        CancellationToken cs)
    {
        if (request.Items is null || request.Items.Count == 0 || request.Items.Any(i => i.Quantity <= 0 || i.Revision == 0))
        {
            return Results.BadRequest("Order must contain at least one item with positive revision and quantity.");
        }

        var resolvedBoms = new List<(CreateOrderItemRequest Item, CoreBillOfMaterial Bom, BomSnapshot Snapshot)>();

        foreach (var item in request.Items)
        {
            Manufacturing.Catalog.Client.BillOfMaterial clientBom;
            try
            {
                clientBom = await catalogClient.GetBillOfMaterialByProductIdAndRevisionAsync(
                    item.ProductId,
                    (int)item.Revision,
                    cs);
            }
            catch (ApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                return Results.NotFound($"BOM for product {item.ProductId} revision {item.Revision} was not found.");
            }

            var coreBom = new CoreBillOfMaterial
            {
                ProductId = clientBom.ProductId,
                Revision = (uint)clientBom.Revision,
                Materials = clientBom.Materials
                    .Select(m => new CoreBillOfMaterial.Record(m.MaterialId, m.Quantity))
                    .ToList(),
            };

            var snapshot = new BomSnapshot { BillOfMaterial = coreBom };
            resolvedBoms.Add((item, coreBom, snapshot));
        }

        // Persist distinct BOM snapshots
        var distinctSnapshots = resolvedBoms
            .Select(x => x.Snapshot)
            .GroupBy(s => s.Hash)
            .Select(g => g.First())
            .ToList();

        var hashes = distinctSnapshots.Select(s => s.Hash).ToList();
        var existingHashes = await db.BomSnapshots
            .Where(s => hashes.Contains(s.Hash))
            .Select(s => s.Hash)
            .ToHashSetAsync(cs);

        foreach (var snapshot in distinctSnapshots.Where(s => !existingHashes.Contains(s.Hash)))
        {
            db.BomSnapshots.Add(snapshot);
        }

        // 1. Create order in Created -> WaitingForReserve state
        var order = new Order
        {
            Status = OrderStatus.Created,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cs);

        order.Status = OrderStatus.WaitingForReserve;
        await db.SaveChangesAsync(cs);

        var js = nats.CreateJetStreamContext();
        var producerTasks = new List<ProducerTaskV1>();
        var orderItems = new List<Order.OrderItem>();

        foreach (var (item, bom, _) in resolvedBoms)
        {
            var reservationId = Guid.CreateVersion7();

            foreach (var material in bom.Materials)
            {
                var totalNeeded = (uint)(material.Quantity * item.Quantity);
                var reserveTask = new InventoryReserveTaskV1(material.MaterialId, totalNeeded, reservationId);

                var replyMsg = await nats.RequestAsync<InventoryReserveTaskV1, InventoryReserveReplyV1>(
                    NatsContract.Inventory.CommandStream.Subjects.ReserveV1,
                    reserveTask,
                    cancellationToken: cs);

                if (replyMsg.Data is not { Status: InventoryReserveTaskStatus.Success })
                {
                    order.Status = OrderStatus.Error;
                    order.ErrorMessage = replyMsg.Data?.ErrorMessage
                        ?? $"Failed to reserve material {material.MaterialId} in inventory.";
                    await db.SaveChangesAsync(cs);

                    return Results.Conflict(order);
                }
            }

            // Create 1 ProducerTaskV1 per unit so UI can track granular unit-by-unit progress
            var unitMaterials = bom.Materials
                .Select(m => new ProducerTaskV1.MaterialListItem(m.MaterialId, (uint)m.Quantity))
                .ToList();

            var itemTasks = new List<Order.OrderProductionTask>(item.Quantity);
            for (var unit = 0; unit < item.Quantity; unit++)
            {
                var producerTask = new ProducerTaskV1(item.ProductId, item.Revision, unitMaterials, reservationId);
                producerTasks.Add(producerTask);
                itemTasks.Add(new Order.OrderProductionTask
                {
                    TaskId = producerTask.Id,
                    Status = OrderTaskStatus.Pending,
                });
            }

            orderItems.Add(new Order.OrderItem
            {
                ProductId = item.ProductId,
                Revision = item.Revision,
                BOMRevision = reservationId,
                Quantity = item.Quantity,
                Tasks = itemTasks,
            });
        }

        // 2. Materials reserved -> transition to Reserved and persist tasks before publishing
        order.Items = orderItems;
        order.Status = OrderStatus.Reserved;
        await db.SaveChangesAsync(cs);

        // 3. Publish production tasks to JetStream
        try
        {
            foreach (var task in producerTasks)
            {
                var ack = await js.PublishAsync(
                    NatsContract.Producer.CommandStream.Subjects.StartV1,
                    task,
                    cancellationToken: cs);
                ack.EnsureSuccess();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            order.Status = OrderStatus.Error;
            order.ErrorMessage = $"Failed to enqueue production tasks: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return Results.Created($"/api/orders/{order.Id}", order);
    }

    public sealed record CreateOrderRequest(List<CreateOrderItemRequest> Items);

    public sealed record CreateOrderItemRequest(Guid ProductId, uint Revision, int Quantity);
}
