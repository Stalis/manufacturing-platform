using Manufacturing.Catalog.Client;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.Hosting;
using Orders.Api;
using Orders.Api.Endpoints;
using Orders.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Orders")
    )
);

var catalogBaseUrl = builder.Configuration["CatalogApi:BaseUrl"] ?? "http://localhost:5142";
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
});

builder.Services.AddNats(configureOpts: options =>
    options with
    {
        Url = builder.Configuration["NATS:Url"] ?? NatsOpts.Default.Url,
        Name = "Manufacturing.Orders.Api",
        SerializerRegistry = new NatsJsonContextSerializerRegistry(
            ProducerTaskV1JsonContext.Default,
            ProducerEventV1JsonContext.Default,
            InventoryReserveTaskV1JsonContext.Default,
            InventoryReserveReplyV1JsonContext.Default
        ),
    });

builder.Services.AddHostedService<OrderProgressTracker>();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseHttpsRedirection();

app.MapOrdersEndpoints();

app.Run();
