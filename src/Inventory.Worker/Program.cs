using Inventory.DAL;
using Inventory.Worker;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddDbContext<InventoryDbContext>(options => 
        options.UseNpgsql(builder.Configuration.GetConnectionString("Inventory")));
builder.Services.AddNats(
        configureOpts: opts => opts with
        {
                Url = builder.Configuration["NATS:Url"] ?? NatsOpts.Default.Url,
                Name = "Manufacturing.Inventory.Worker",
                SerializerRegistry = new NatsJsonContextSerializerRegistry(
                        InventoryGrabReplyV1JsonContext.Default,
                        InventoryGrabTaskV1JsonContext.Default,
                        InventoryReserveTaskV1JsonContext.Default,
                        InventoryReserveReplyV1JsonContext.Default,
                        InventoryDeliveredEventV1JsonContext.Default
                        ),
        });

var host = builder.Build();

if (!EF.IsDesignTime)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
}

await host.RunAsync();