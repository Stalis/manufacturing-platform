using Inventory.Contracts.Models;
using Inventory.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Npgsql;

namespace Inventory.Worker.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVENTORY_TEST_CONNECTION")))
            Skip = "Set INVENTORY_TEST_CONNECTION to a disposable PostgreSQL server with CREATEDB permission.";
    }
}

public class StockTests
{
    private const uint DefaultStock = 10_000;

    [PostgresFact]
    public Task ReleaseReturnsUnconsumedStockOnce() => WithWorkerAsync(async (worker, db) =>
    {
        var (a, b, reservation) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await worker.HandleReserveAsync(new InventoryReserveTaskV1(a, 3, reservation), default);
        await worker.HandleReserveAsync(new InventoryReserveTaskV1(b, 5, reservation), default);
        Assert.Equal(DefaultStock - 3, await StockAsync(db, a));

        var first = await worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);
        var again = await worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);

        Assert.Equal(InventoryReleaseTaskStatus.Released, first.Status);
        Assert.Equal(InventoryReleaseTaskStatus.Released, again.Status);
        Assert.Equal(DefaultStock, await StockAsync(db, a));
        Assert.Equal(DefaultStock, await StockAsync(db, b));
        Assert.Equal(ReservationStatus.Canceled,
                     (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservation)).Status);
    });

    [PostgresFact]
    public Task ConcurrentFirstReservesOfAMaterialBothSucceed() => WithWorkerAsync(async (worker, db) =>
    {
        var material = Guid.NewGuid();
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 2), default)));

        Assert.All(replies, r => Assert.Equal(InventoryReserveTaskStatus.Success, r.Status));
        Assert.Equal(DefaultStock - 16, await StockAsync(db, material));
    });

    private static async Task<uint> StockAsync(InventoryDbContext db, Guid materialId) =>
        (await db.MaterialStocks.AsNoTracking().SingleAsync(s => s.Id == materialId)).Quantity;

    private static async Task WithWorkerAsync(Func<Worker, InventoryDbContext, Task> test)
    {
        var adminConnection = Environment.GetEnvironmentVariable("INVENTORY_TEST_CONNECTION")!;
        var database = "inventory_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database, Pooling = false }
                .ConnectionString;
            await using var services = new ServiceCollection()
                .AddDbContext<InventoryDbContext>(o => o.UseNpgsql(connection))
                .BuildServiceProvider();
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.MigrateAsync();

            // The handlers under test never touch NATS; the connection stays unopened.
            await using var nats = new NatsConnection();
            var worker = new Worker(NullLogger<Worker>.Instance, nats,
                                    services.GetRequiredService<IServiceScopeFactory>());
            await test(worker, db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
