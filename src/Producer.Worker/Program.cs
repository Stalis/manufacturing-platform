using NATS.Client.Core;
using NATS.Client.Hosting;
using Producer.Worker;


var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNats(configureOpts: options =>
                             options with
                             {
                                 Url = builder.Configuration["NATS:Url"] ?? builder.Configuration["Producer:NATS:Url"] ?? NatsOpts.Default.Url,
                                 Name = "Manufacturing.Producer.Worker",
                                 SerializerRegistry = new NatsJsonContextSerializerRegistry(
                                     ProducerTaskV1JsonContext.Default,
                                     ProducerEventV1JsonContext.Default,
                                     InventoryGrabTaskV1JsonContext.Default,
                                     InventoryGrabReplyV1JsonContext.Default,
                                     InventoryDeliveredEventV1JsonContext.Default
                                     ),
                             });
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();