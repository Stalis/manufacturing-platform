using Inventory.Contracts.Models;
using Inventory.DAL;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Npgsql;
using Tests.Shared;

namespace Inventory.Worker.Tests;

public class StockTests
{
    private const uint DefaultStock = 10_000;
    private const string Connection = "INVENTORY_TEST_CONNECTION";

    [PostgresFact(Connection)]
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

    [PostgresFact(Connection)]
    public Task ConcurrentFirstReservesOfAMaterialBothSucceed() => WithWorkerAsync(async (worker, db) =>
    {
        var material = Guid.NewGuid();
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 2), default)));

        Assert.All(replies, r => Assert.Equal(InventoryReserveTaskStatus.Success, r.Status));
        Assert.Equal(DefaultStock - 16, await StockAsync(db, material));
    });

    [PostgresFact(Connection)]
    public Task ReleaseAfterPartialGrabReturnsOnlyUnconsumedStock() => WithWorkerAsync(async (worker, db) =>
    {
        var (material, reservation) = (Guid.NewGuid(), Guid.NewGuid());
        await worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 5, reservation), default);
        // The delivered event is published after the commit, and no NATS server is reachable here.
        await Assert.ThrowsAnyAsync<NatsException>(() =>
            worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, material, 2), default));

        var release = await worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);

        Assert.Equal(InventoryReleaseTaskStatus.Released, release.Status);
        Assert.Equal(DefaultStock - 2, await StockAsync(db, material));
    });

    [PostgresFact(Connection)]
    public Task ReleaseBeforeReserveRefusesTheLateReserve() => WithWorkerAsync(async (worker, db) =>
    {
        var (material, reservation) = (Guid.NewGuid(), Guid.NewGuid());

        var release = await worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);
        var reserve = await worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 3, reservation), default);

        Assert.Equal(InventoryReleaseTaskStatus.NotFound, release.Status);
        Assert.NotEqual(InventoryReserveTaskStatus.Success, reserve.Status);
        Assert.Equal(DefaultStock, await StockAsync(db, material));
    });

    [PostgresFact(Connection)]
    public Task ReserveAfterCompletedReleaseIsRefused() => WithWorkerAsync(async (worker, db) =>
    {
        var (material, reservation) = (Guid.NewGuid(), Guid.NewGuid());
        await worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 3, reservation), default);
        await worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);

        var reserve = await worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 3, reservation), default);

        Assert.NotEqual(InventoryReserveTaskStatus.Success, reserve.Status);
        Assert.Equal(DefaultStock, await StockAsync(db, material));
        Assert.Equal(ReservationStatus.Canceled,
                     (await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservation)).Status);
    });

    [PostgresFact(Connection)]
    public Task ConcurrentReleasesReturnStockOnlyOnce() => WithWorkerAsync(async (worker, db) =>
    {
        var (material, reservation) = (Guid.NewGuid(), Guid.NewGuid());
        await worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 5, reservation), default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default)));
        Assert.Equal(DefaultStock, await StockAsync(db, material));
    });

    // The worker creates a material's stock row with DefaultStock on its first successful reserve.
    private static async Task<uint> StockAsync(InventoryDbContext db, Guid materialId) =>
        (await db.MaterialStocks.AsNoTracking().SingleOrDefaultAsync(s => s.Id == materialId))?.Quantity
        ?? DefaultStock;

    private static async Task WithWorkerAsync(Func<Worker, InventoryDbContext, Task> test)
    {
        var adminConnection = Environment.GetEnvironmentVariable(Connection)!;
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

            // Only grab publishes; an unreachable server makes that fail fast instead of reaching a dev NATS.
            await using var nats = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:1" });
            var worker = new Worker(NullLogger<Worker>.Instance, new InventoryNatsClient(nats),
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
