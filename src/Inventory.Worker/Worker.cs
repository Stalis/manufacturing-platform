using System.Text.Json;
using Inventory.Contracts.Models;
using Inventory.DAL;
using Inventory.DAL.Entities;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

namespace Inventory.Worker;

public class Worker(
    ILogger<Worker> logger,
    INatsConnection nats,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var js = nats.CreateJetStreamContext();

        var consumer = await js.GetConsumerAsync(
            NatsContract.Inventory.InventoryWorkerConsumer.StreamName,
            NatsContract.Inventory.InventoryWorkerConsumer.DurableName,
            stoppingToken);

        await foreach (var message in consumer.ConsumeAsync<NatsMemoryOwner<byte>>(cancellationToken: stoppingToken))
        {
            using (message.Data)
            {
                try
                {
                    if (message.Subject == NatsContract.Inventory.CommandStream.Subjects.ReserveV1)
                    {
                        var task = JsonSerializer.Deserialize(
                            message.Data.Span,
                            InventoryReserveTaskV1JsonContext.Default.InventoryReserveTaskV1);
                        if (task is not null)
                        {
                            var reply = await HandleReserveAsync(task, stoppingToken);
                            if (!string.IsNullOrEmpty(message.ReplyTo))
                            {
                                await nats.PublishAsync(message.ReplyTo, reply, cancellationToken: stoppingToken);
                            }
                        }
                    }
                    else if (message.Subject == NatsContract.Inventory.CommandStream.Subjects.GrabV1)
                    {
                        var task = JsonSerializer.Deserialize(
                            message.Data.Span,
                            InventoryGrabTaskV1JsonContext.Default.InventoryGrabTaskV1);
                        if (task is not null)
                        {
                            var reply = await HandleGrabAsync(js, task, stoppingToken);
                            if (!string.IsNullOrEmpty(message.ReplyTo))
                            {
                                await nats.PublishAsync(message.ReplyTo, reply, cancellationToken: stoppingToken);
                            }
                        }
                    }
                    else
                    {
                        logger.LogWarning("Unknown inventory subject {Subject}", message.Subject);
                    }

                    await message.AckAsync(cancellationToken: stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error processing inventory command on {Subject}", message.Subject);
                    await message.NakAsync(cancellationToken: stoppingToken);
                }
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

        var stock = await db.MaterialStocks.FirstOrDefaultAsync(s => s.Id == task.MaterialId, ct);
        if (stock is null || stock.Quantity < task.Quantity)
        {
            return new InventoryReserveReplyV1(
                task.Id,
                null,
                InventoryReserveTaskStatus.NotInStock,
                $"Insufficient stock for material {task.MaterialId}.");
        }

        stock.Quantity -= task.Quantity;

        var reservationId = task.ReservationId ?? Guid.CreateVersion7();
        var reservation = await db.Reservations
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            reservation = new Reservation
            {
                Id = reservationId,
                Status = ReservationStatus.Reserved,
            };
            db.Reservations.Add(reservation);
        }
        else
        {
            reservation.Status = ReservationStatus.Reserved;
        }

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

    public async Task<InventoryGrabReplyV1> HandleGrabAsync(
        INatsJSContext js,
        InventoryGrabTaskV1 task,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var reservation = await db.Reservations
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == task.ReservationId, ct);

        if (reservation is null || reservation.Items.Count == 0)
        {
            return new InventoryGrabReplyV1(
                task.Id,
                InventoryGrabTaskStatus.ReservationIsEmpty,
                $"Reservation {task.ReservationId} was not found or has no items.");
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

        var ack = await js.PublishAsync(
            NatsContract.Inventory.EventStream.Subjects.DeliveredV1,
            deliveredEvent,
            cancellationToken: ct);
        ack.EnsureSuccess();

        logger.LogInformation(
            "Grabbed and delivered {Quantity} of material {MaterialId} from reservation {ReservationId}",
            task.Quantity, task.MaterialId, task.ReservationId);

        return new InventoryGrabReplyV1(task.Id, InventoryGrabTaskStatus.Success, null);
    }
}
