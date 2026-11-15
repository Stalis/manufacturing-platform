using Inventory.Contracts.Models;
using Manufacturing.Nats.Contract;
using Producer.Contracts.Models;

namespace Producer.Worker;

public class Worker(ILogger<Worker> logger, ProducerNatsClient producer, InventoryNatsClient inventory) : BackgroundService
{
    private ValueTask SendEvent(ProducerEventV1 message, CancellationToken cancellationToken)
    {
        return message.Type switch
        {
            ProducerEventType.TaskStarted => producer.PublishStartedV1Async(message, cancellationToken),
            ProducerEventType.TaskCompleted => producer.PublishCompletedV1Async(message, cancellationToken),
            ProducerEventType.TaskFailed => producer.PublishFailedV1Async(message, cancellationToken),
            ProducerEventType.TaskInvalid => producer.PublishInvalidV1Async(message, cancellationToken),
            ProducerEventType.TaskStopped => producer.PublishStoppedV1Async(message, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Type, "Unsupported producer event type."),
        };
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in producer.ReadProducerWorkerAsync(stoppingToken))
        {
            if (message.Data is not { } produceTask)
            {
                logger.LogWarning("Producer task is empty");
                await SendEvent(
                    new ProducerEventV1(Guid.Empty, ProducerEventType.TaskInvalid, "Task is empty"),
                    stoppingToken);
                await message.AckAsync(cancellationToken: stoppingToken);
                continue;
            }

            logger.LogInformation("Producer task started for product {ProduceTaskProduct}", produceTask.ProductId);
            await SendEvent(new ProducerEventV1(produceTask.Id, ProducerEventType.TaskStarted, "Task started"), stoppingToken);

            if (produceTask.ReservationId is { } reservationId && produceTask.Materials.Count > 0)
            {
                var materialsGrabbed = await GrabMaterialsFromInventoryAsync(reservationId, produceTask.Materials, stoppingToken);
                if (!materialsGrabbed)
                {
                    logger.LogWarning("Failed to grab materials for task {TaskId} (Reservation {ReservationId})", produceTask.Id, reservationId);
                    await SendEvent(new ProducerEventV1(produceTask.Id, ProducerEventType.TaskFailed, "Failed to grab materials from inventory"), stoppingToken);
                    await message.AckAsync(cancellationToken: stoppingToken);
                    continue;
                }
            }

            var result = await DoWork(produceTask, stoppingToken);
            if (result == ProductionResult.Stopped)
            {
                logger.LogInformation("Producer task stopped");
                await SendEvent(new ProducerEventV1(produceTask.Id, ProducerEventType.TaskStopped, "Task stopped"), CancellationToken.None);
                await message.NakAsync(cancellationToken: CancellationToken.None);
                continue;
            }

            logger.LogInformation("Producer task completed");
            await SendEvent(new ProducerEventV1(produceTask.Id, ProducerEventType.TaskCompleted, "Task completed"), stoppingToken);
            await message.AckAsync(cancellationToken: stoppingToken);
        }
    }

    private async Task<bool> GrabMaterialsFromInventoryAsync(
        Guid reservationId,
        List<ProducerTaskV1.MaterialListItem> materials,
        CancellationToken ct)
    {
        foreach (var material in materials)
        {
            var grabTask = new InventoryGrabTaskV1(reservationId, material.MaterialId, material.Count);
            await inventory.PublishGrabV1Async(grabTask, ct);
        }

        var remaining = materials.ToDictionary(m => m.MaterialId, m => m.Count);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await foreach (var deliveryMsg in inventory.ReadProducerWorkerInventoryAsync(timeoutCts.Token))
            {
                if (deliveryMsg.Data is not { } delivered)
                {
                    await deliveryMsg.AckAsync(cancellationToken: ct);
                    continue;
                }

                if (delivered.ReservationId != reservationId)
                {
                    await deliveryMsg.AckAsync(cancellationToken: ct);
                    continue;
                }

                await deliveryMsg.AckAsync(cancellationToken: ct);

                if (remaining.TryGetValue(delivered.MaterialId, out var needed))
                {
                    if (delivered.Quantity >= needed)
                        remaining.Remove(delivered.MaterialId);
                    else
                        remaining[delivered.MaterialId] = needed - delivered.Quantity;
                }

                if (remaining.Count == 0 || delivered.IsReservationCompleted)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Timed out waiting for inventory delivery on reservation {ReservationId}", reservationId);
            return false;
        }

        return remaining.Count == 0;
    }

    internal enum ProductionResult
    {
        Ok,
        Stopped,
    }
    
    internal async Task<ProductionResult> DoWork(ProducerTaskV1 produceTask, CancellationToken cancellationToken)
    {
        logger.LogInformation("Running producing of {ProduceTaskProduct}", produceTask.ProductId);
        try
        {
            await Task.Delay(1000, cancellationToken);
            return ProductionResult.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProductionResult.Stopped;
        }
    }
}
