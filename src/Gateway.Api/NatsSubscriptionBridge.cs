using Gateway.Api.Clients;
using Gateway.Api.GraphQL;
using HotChocolate.Subscriptions;
using Manufacturing.Nats.Contract;
using NATS.Client.Core;
using NATS.Net;
using Producer.Contracts.Models;

namespace Gateway.Api;

public sealed class NatsSubscriptionBridge(
    ILogger<NatsSubscriptionBridge> logger,
    INatsConnection nats,
    ITopicEventSender eventSender,
    IOrdersClient ordersClient) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var js = nats.CreateJetStreamContext();

            var consumer = await js.GetConsumerAsync(
                NatsContract.Gateway.GatewayApiProducerConsumer.StreamName,
                NatsContract.Gateway.GatewayApiProducerConsumer.DurableName,
                stoppingToken);

            await foreach (var message in consumer.ConsumeAsync<ProducerEventV1>(cancellationToken: stoppingToken))
            {
                if (message.Data is not { } producerEvent)
                {
                    await message.AckAsync(cancellationToken: stoppingToken);
                    continue;
                }

                try
                {
                    // Allow Orders.Api a brief moment to commit the updated task status
                    await Task.Delay(50, stoppingToken);

                    var allOrders = await ordersClient.GetAllOrdersAsync(stoppingToken);
                    var matchingOrder = allOrders.FirstOrDefault(o =>
                        o.Items.Any(i => i.Tasks.Any(t => t.TaskId == producerEvent.TaskId)));

                    var gqlOrder = matchingOrder is null ? null : OrderGql.FromCore(matchingOrder);

                    if (gqlOrder is not null)
                    {
                        await eventSender.SendAsync(Subscription.OrderUpdatedTopic, gqlOrder, stoppingToken);
                    }

                    await eventSender.SendAsync(
                        Subscription.ProducerEventTopic,
                        new ProducerEventGql(producerEvent.TaskId, producerEvent.Type, producerEvent.Message, gqlOrder),
                        stoppingToken);

                    await message.AckAsync(cancellationToken: stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Error forwarding producer event for task {TaskId} to GraphQL subscribers", producerEvent.TaskId);
                    await message.AckAsync(cancellationToken: stoppingToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "NATS JetStream subscription bridge could not connect on startup.");
        }
    }
}
