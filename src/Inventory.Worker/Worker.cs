using Inventory.Contracts.Models;
using Inventory.DAL;
using Inventory.DAL.Entities;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;

namespace Inventory.Worker;

public class Worker(
    ILogger<Worker> logger,
    InventoryNatsClient inventory,
    IServiceScopeFactory scopeFactory,
    IConfiguration? configuration = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Reserve and release run concurrently and both change stock; LockStockAsync serializes them per material.
        Task[] loops =
        [
            inventory.RespondReserveV1Async(HandleReserveAsync, LogCommandError, cancellation.Token),
            inventory.RespondReleaseV1Async(HandleReleaseAsync, LogCommandError, cancellation.Token),
            ConsumeGrabAsync(cancellation.Token),
        ];

        try
        {
            await await Task.WhenAny(loops);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await Task.WhenAll(loops);
            }
            catch (OperationCanceledException) when (!loops.Any(loop => loop.IsFaulted))
            {
            }
        }
    }

    private void LogCommandError(Exception ex, string subject) =>
        logger.LogError(ex, "Error processing inventory command on {Subject}", subject);

    private async Task ConsumeGrabAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in inventory.ReadInventoryWorkerAsync(cancellationToken))
        {
            try
            {
                message.EnsureSuccess();
                if (message.Data is { } task &&
                    await HandleGrabAsync(task, cancellationToken) is { Status: not InventoryGrabTaskStatus.Success } reply)
                {
                    logger.LogWarning("Grab task {TaskId} failed with {Status}: {Message}",
                                      reply.RequestId, reply.Status, reply.ErrorMessage);
                }

                await message.AckAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCommandError(ex, message.Subject);
                await message.NakAsync(cancellationToken: cancellationToken);
            }
        }
    }

    public async Task<InventoryReserveReplyV1> HandleReserveAsync(
        InventoryReserveTaskV1 task,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var reservationId = task.ReservationId ?? Guid.CreateVersion7();
        await db.Database.ExecuteSqlAsync(
            $"""INSERT INTO "Reservations" ("Id", "Status") VALUES ({reservationId}, {(int)ReservationStatus.Reserved}) ON CONFLICT DO NOTHING""",
            ct);
        var reservation = (await LockReservationAsync(db, reservationId, ct))!;
        // A release that ran first (e.g. Orders timed out on this reserve) leaves it Canceled; never revive it.
        if (reservation.Status != ReservationStatus.Reserved)
        {
            return new InventoryReserveReplyV1(
                task.Id,
                reservationId,
                InventoryReserveTaskStatus.Error,
                $"Reservation {reservationId} is {reservation.Status}.");
        }

        // FOR UPDATE cannot lock a missing row, so create it first; a concurrent first reserve waits here, then skips.
        var defaultStock = configuration?.GetValue<uint?>("Inventory:DefaultInitialStock") ?? 10000u;
        if (defaultStock > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "MaterialStocks" ("Id", "Quantity") VALUES ({task.MaterialId}, {(long)defaultStock}) ON CONFLICT DO NOTHING""",
                ct);
        }

        var stock = await LockStockAsync(db, task.MaterialId, ct);

        if (stock is null || stock.Quantity < task.Quantity)
        {
            return new InventoryReserveReplyV1(
                task.Id,
                null,
                InventoryReserveTaskStatus.NotInStock,
                $"Insufficient stock for material {task.MaterialId}.");
        }

        stock.Quantity -= task.Quantity;

        var existingItem = reservation.Items.FirstOrDefault(i => i.MaterialId == task.MaterialId);
        if (existingItem is null)
        {
            db.ReservationItems.Add(new ReservationItem
            {
                ReservationId = reservationId,
                MaterialId = task.MaterialId,
                Quantity = task.Quantity,
                ConsumedQuantity = 0,
            });
        }
        else
        {
            existingItem.Quantity += task.Quantity;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Reserved {Quantity} of material {MaterialId} in reservation {ReservationId}",
            task.Quantity, task.MaterialId, reservationId);

        return new InventoryReserveReplyV1(task.Id, reservationId, InventoryReserveTaskStatus.Success, null);
    }

    public async Task<InventoryReleaseReplyV1> HandleReleaseAsync(
        InventoryReleaseTaskV1 task,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // A release that beats its reserve leaves a Canceled placeholder, so the late reserve is refused.
        if (await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Reservations" ("Id", "Status") VALUES ({task.ReservationId}, {(int)ReservationStatus.Canceled}) ON CONFLICT DO NOTHING""",
                ct) == 1)
        {
            await tx.CommitAsync(ct);
            return new InventoryReleaseReplyV1(task.Id, InventoryReleaseTaskStatus.NotFound);
        }

        var reservation = (await LockReservationAsync(db, task.ReservationId, ct))!;

        if (reservation.Status == ReservationStatus.Reserved)
        {
            // A fixed lock order keeps concurrent releases from deadlocking.
            foreach (var item in reservation.Items.OrderBy(i => i.MaterialId))
            {
                if (await LockStockAsync(db, item.MaterialId, ct) is { } stock)
                {
                    stock.Quantity += item.Quantity - item.ConsumedQuantity;
                }
            }

            reservation.Status = ReservationStatus.Canceled;
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Released reservation {ReservationId}", task.ReservationId);
        }

        return new InventoryReleaseReplyV1(task.Id, InventoryReleaseTaskStatus.Released);
    }

    // Reservation row lock until the transaction ends; every handler takes it before any stock lock.
    private static async Task<Reservation?> LockReservationAsync(InventoryDbContext db, Guid id, CancellationToken ct)
    {
        var reservation = (await db.Reservations
                                   .FromSql($"""SELECT * FROM "Reservations" WHERE "Id" = {id} FOR UPDATE""")
                                   .ToListAsync(ct))
            .SingleOrDefault();
        if (reservation is not null)
        {
            await db.Entry(reservation).Collection(r => r.Items).LoadAsync(ct);
        }

        return reservation;
    }

    // Row lock until the transaction ends, so concurrent reserve/release cannot lose an update.
    private static async Task<MaterialStock?> LockStockAsync(InventoryDbContext db, Guid materialId, CancellationToken ct) =>
        (await db.MaterialStocks
                 .FromSql($"""SELECT * FROM "MaterialStocks" WHERE "Id" = {materialId} FOR UPDATE""")
                 .ToListAsync(ct))
        .SingleOrDefault();

    public async Task<InventoryGrabReplyV1> HandleGrabAsync(
        InventoryGrabTaskV1 task,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var reservation = await LockReservationAsync(db, task.ReservationId, ct);

        if (reservation is not { Status: ReservationStatus.Reserved } || reservation.Items.Count == 0)
        {
            return new InventoryGrabReplyV1(
                task.Id,
                InventoryGrabTaskStatus.ReservationIsEmpty,
                $"Reservation {task.ReservationId} was not found, is not active or has no items.");
        }

        var item = reservation.Items.FirstOrDefault(i => i.MaterialId == task.MaterialId);
        if (item is null || item.Quantity - item.ConsumedQuantity < task.Quantity)
        {
            return new InventoryGrabReplyV1(
                task.Id,
                InventoryGrabTaskStatus.Failure,
                $"Insufficient reserved quantity for material {task.MaterialId} in reservation {task.ReservationId}.");
        }

        item.ConsumedQuantity += task.Quantity;

        var allConsumed = reservation.Items.All(i => i.ConsumedQuantity >= i.Quantity);
        if (allConsumed)
        {
            reservation.Status = ReservationStatus.Consumed;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var deliveredEvent = new InventoryDeliveredEventV1(
            task.ReservationId,
            task.MaterialId,
            task.Quantity,
            allConsumed);

        await inventory.PublishDeliveredV1Async(deliveredEvent, ct);

        logger.LogInformation(
            "Grabbed and delivered {Quantity} of material {MaterialId} from reservation {ReservationId}",
            task.Quantity, task.MaterialId, task.ReservationId);

        return new InventoryGrabReplyV1(task.Id, InventoryGrabTaskStatus.Success, null);
    }
}
