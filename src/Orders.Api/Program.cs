using Catalog.Contracts;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.Hosting;
using Orders.Api;
using Orders.Api.Endpoints;
using Orders.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Orders")
    )
);

var catalogBaseUrl = builder.Configuration["CatalogApi:BaseUrl"] ?? "http://localhost:5142";
builder.Services.AddHttpClient<CatalogClient>(client =>
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
            InventoryReserveReplyV1JsonContext.Default,
            InventoryReleaseTaskV1JsonContext.Default,
            InventoryReleaseReplyV1JsonContext.Default
        ),
    });

builder.Services.AddSingleton<ProducerNatsClient>();
builder.Services.AddSingleton<InventoryNatsClient>();
builder.Services.AddHostedService<OrderProgressTracker>();
builder.Services.AddOrdersEndpoints();

var app = builder.Build();

if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOrdersApi();

app.Run();
