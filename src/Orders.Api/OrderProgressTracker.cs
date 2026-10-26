using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Net;
using Orders.Api.Persistence;
using Orders.Core.Models;
using Producer.Contracts.Models;

namespace Orders.Api;

public sealed class OrderProgressTracker(
    ILogger<OrderProgressTracker> logger,
    INatsConnection nats,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var js = nats.CreateJetStreamContext();

        var consumer = await js.GetConsumerAsync(
            NatsContract.Orders.OrdersApiProducerConsumer.StreamName,
            NatsContract.Orders.OrdersApiProducerConsumer.DurableName,
            stoppingToken);

        await foreach (var message in consumer.ConsumeAsync<ProducerEventV1>(cancellationToken: stoppingToken))
        {
            if (message.Data is not { } producerEvent || producerEvent.TaskId == Guid.Empty)
            {
                await message.AckAsync(cancellationToken: stoppingToken);
                continue;
            }

            try
            {
                await HandleProducerEventAsync(producerEvent, stoppingToken);
                await message.AckAsync(cancellationToken: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to update order progress for task {TaskId}", producerEvent.TaskId);
                await message.NakAsync(cancellationToken: stoppingToken);
            }
        }
    }

    public async Task<bool> HandleProducerEventAsync(ProducerEventV1 producerEvent, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        var activeOrders = await db.Orders
            .Where(o => o.Status != OrderStatus.Done)
            .ToListAsync(ct);

        var matchingOrder = activeOrders.FirstOrDefault(o =>
            o.Items.Any(i => i.Tasks.Any(t => t.TaskId == producerEvent.TaskId)));

        if (matchingOrder is null)
        {
            logger.LogDebug("Order for producer task {TaskId} not found or already completed", producerEvent.TaskId);
            return false;
        }

        var taskItem = matchingOrder.Items
            .SelectMany(i => i.Tasks)
            .First(t => t.TaskId == producerEvent.TaskId);

        taskItem.Status = producerEvent.Type switch
        {
            ProducerEventType.TaskStarted => OrderTaskStatus.InProgress,
            ProducerEventType.TaskCompleted => OrderTaskStatus.Completed,
            ProducerEventType.TaskFailed or ProducerEventType.TaskInvalid => OrderTaskStatus.Failed,
            ProducerEventType.TaskStopped => OrderTaskStatus.Stopped,
            _ => taskItem.Status,
        };
        taskItem.LastMessage = producerEvent.Message;

        matchingOrder.RecalculateStatus();

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Updated Order {OrderId} (Status: {Status}, Progress: {Completed}/{Total} [{Percent}%]) on event {EventType} for Task {TaskId}",
            matchingOrder.Id,
            matchingOrder.Status,
            matchingOrder.CompletedTasksCount,
            matchingOrder.TotalTasksCount,
            matchingOrder.ProgressPercentage,
            producerEvent.Type,
            producerEvent.TaskId);

        return true;
    }
}
