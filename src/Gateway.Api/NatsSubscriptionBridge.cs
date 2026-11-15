using System.Net;
using Gateway.Api.GraphQL;
using HotChocolate.Subscriptions;
using Manufacturing.Nats.Contract;
using NATS.Client.Core;
using Orders.Contracts;

namespace Gateway.Api;

public sealed class NatsSubscriptionBridge(
    ILogger<NatsSubscriptionBridge> logger,
    INatsConnection nats,
    ITopicEventSender eventSender,
    OrdersClient ordersClient) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var producer = new ProducerNatsClient(nats);

            await foreach (var message in producer.ReadGatewayApiProducerAsync(stoppingToken))
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

                    OrderGql? gqlOrder = null;
                    try
                    {
                        gqlOrder = OrderGql.FromContract(
                            await ordersClient.GetOrderByTaskAsync(producerEvent.TaskId, stoppingToken));
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                    }

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
