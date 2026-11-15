using System.Net;
using Catalog.Contracts;
using Inventory.Contracts.Models;
using Manufacturing.Nats.Contract;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using Orders.Api.Persistence;
using Orders.Core.Models;
using Producer.Contracts.Models;
using static Orders.Api.Endpoints.OrdersEndpoints;
using Contract = Orders.Contracts;
using DomainBillOfMaterial = Manufacturing.Core.Models.BillOfMaterial;

namespace Orders.Api.Endpoints;

public sealed class OrdersHandler(
    OrdersDbContext db,
    CatalogClient catalog,
    INatsConnection nats,
    ILogger<OrdersHandler> logger) : Contract.IOrdersHandler
{
    // One production task is created per unit; keep a single request from exhausting memory.
    private const int MaxQuantity = 10_000;

    public async Task<Ok<Contract.Order[]>> ListOrdersAsync(CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking().ToListAsync(cancellationToken);
        return TypedResults.Ok(orders.Select(ToContract).ToArray());
    }

    public async Task<Results<Ok<Contract.Order>, NotFound<ProblemDetails>>> GetOrderAsync(
        Guid id, CancellationToken cancellationToken)
    {
        return await db.Orders.FindAsync([id], cancellationToken) is { } order
            ? TypedResults.Ok(ToContract(order))
            : TypedResults.NotFound(Problem($"Order {id} was not found."));
    }

    public async Task<Results<Ok<Contract.Order>, NotFound<ProblemDetails>>> GetOrderByTaskAsync(
        Guid taskId, CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .Where(o => o.Items.Any(i => i.Tasks.Any(t => t.TaskId == taskId)))
            .FirstOrDefaultAsync(cancellationToken);
        return order is not null
            ? TypedResults.Ok(ToContract(order))
            : TypedResults.NotFound(Problem($"No order has task {taskId}."));
    }

    public async Task<Results<Created<Contract.Order>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>>>
        CreateOrderAsync(Contract.CreateOrderRequest request, CancellationToken cs)
    {
        if (request.Items.Length == 0 ||
            request.Items.Any(i => i.Quantity is <= 0 or > MaxQuantity || i.Revision == 0))
        {
            return TypedResults.BadRequest(Problem(
                $"Order must contain at least one item with positive revision and quantity 1..{MaxQuantity}."));
        }

        var resolvedBoms = new List<(Contract.CreateOrderItem Item, DomainBillOfMaterial Bom, BomSnapshot Snapshot)>();

        foreach (var item in request.Items)
        {
            BillOfMaterial catalogBom;
            try
            {
                catalogBom = await catalog.GetBillOfMaterialAsync(item.ProductId, item.Revision, cs);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return TypedResults.NotFound(
                    Problem($"BOM for product {item.ProductId} revision {item.Revision} was not found."));
            }

            var bom = new DomainBillOfMaterial
            {
                ProductId = catalogBom.ProductId,
                Revision = catalogBom.Revision,
                Materials = catalogBom.Materials
                    .Select(m => new DomainBillOfMaterial.Record(m.MaterialId, m.Quantity))
                    .ToList(),
            };
            if (bom.Materials.Any(m => (long)m.Quantity * item.Quantity > uint.MaxValue))
            {
                return TypedResults.BadRequest(
                    Problem($"Material quantity for product {item.ProductId} exceeds the inventory limit."));
            }

            var snapshot = new BomSnapshot { BillOfMaterial = bom };
            resolvedBoms.Add((item, bom, snapshot));
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

        var producer = new ProducerNatsClient(nats);
        var inventory = new InventoryNatsClient(nats);
        var producerTasks = new List<ProducerTaskV1>();
        var orderItems = new List<Order.OrderItem>();
        var reservations = new List<Guid>();

        foreach (var (item, bom, _) in resolvedBoms)
        {
            var reservationId = Guid.CreateVersion7();
            reservations.Add(reservationId);

            foreach (var material in bom.Materials)
            {
                var totalNeeded = (uint)((long)material.Quantity * item.Quantity);
                var reserveTask = new InventoryReserveTaskV1(material.MaterialId, totalNeeded, reservationId);

                var replyMsg = await inventory.RequestReserveV1Async(reserveTask, cs);

                if (replyMsg.Data is not { Status: InventoryReserveTaskStatus.Success })
                {
                    // The order exists; the contract reports reservation failures through its Error status.
                    order.Status = OrderStatus.Error;
                    var label = await MaterialLabelAsync(material.MaterialId, cs);
                    order.ErrorMessage = replyMsg.Data?.Status == InventoryReserveTaskStatus.NotInStock
                        ? $"Insufficient stock for {label}: {totalNeeded} needed."
                        : $"Failed to reserve {label} in inventory.";
                    await db.SaveChangesAsync(cs);
                    await ReleaseAsync(inventory, reservations, order.Id, cs);

                    return TypedResults.Created($"/api/orders/{order.Id}", ToContract(order));
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
                await producer.PublishStartV1Async(task, cs);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            order.Status = OrderStatus.Error;
            order.ErrorMessage = $"Failed to enqueue production tasks: {ex.Message}";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return TypedResults.Created($"/api/orders/{order.Id}", ToContract(order));
    }

    // Order errors are shown to users, so name the material rather than its catalog id.
    private async Task<string> MaterialLabelAsync(Guid materialId, CancellationToken cancellationToken)
    {
        try
        {
            var material = await catalog.GetMaterialAsync(materialId, cancellationToken);
            return $"{material.Name} ({material.Sku})";
        }
        // HttpClient reports its own timeout as TaskCanceledException; only the caller's cancellation propagates.
        catch (Exception ex) when (ex is HttpRequestException ||
                                   ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return $"material {materialId}";
        }
    }

    // Best effort: the order is already saved as Error, so a failed release is logged rather than surfaced.
    private async Task ReleaseAsync(
        InventoryNatsClient inventory, IEnumerable<Guid> reservations, Guid orderId, CancellationToken cancellationToken)
    {
        foreach (var reservationId in reservations)
        {
            try
            {
                await inventory.RequestReleaseV1Async(new InventoryReleaseTaskV1(reservationId), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to release reservation {ReservationId} of order {OrderId}",
                                  reservationId, orderId);
            }
        }
    }
}
