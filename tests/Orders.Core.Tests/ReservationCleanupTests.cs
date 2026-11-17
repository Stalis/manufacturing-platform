using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Producer.Contracts.Models;
using Catalog.Contracts;
using Inventory.Contracts.Models;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Orders.Api.Endpoints;
using Orders.Api.Persistence;
using Orders.Contracts;
using Tests.Shared;
using DomainOrder = Orders.Core.Models.Order;
using DomainStatus = Orders.Core.Models.OrderStatus;

namespace Orders.Core.Tests;

public class ReservationCleanupTests
{
    private const string DatabaseVariable = "ORDERS_TEST_CONNECTION";
    private const string NatsVariable = "ORDERS_TEST_NATS_URL";

    [PostgresFact(DatabaseVariable, NatsVariable)]
    public Task MalformedMaterialLabelCannotPreventCleanup() => WithDatabaseAsync(async (db, connection) =>
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var inventory = new InventoryNatsClient(connection);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var labelAfterRelease = false;
        var reserveLoop = inventory.RespondReserveV1Async((task, _) => Task.FromResult(
            new InventoryReserveReplyV1(task.Id, task.ReservationId, InventoryReserveTaskStatus.NotInStock, null)),
            (_, _) => { }, stop.Token);
        var releaseLoop = inventory.RespondReleaseV1Async((task, _) =>
        {
            released.SetResult();
            return Task.FromResult(new InventoryReleaseReplyV1(task.Id, InventoryReleaseTaskStatus.Released));
        }, (_, _) => { }, stop.Token);
        await connection.PingAsync(stop.Token);
        try
        {
            using var http = new HttpClient(new CatalogStub(() => labelAfterRelease = released.Task.IsCompletedSuccessfully))
                { BaseAddress = new Uri("http://catalog/") };
            var handler = new OrdersHandler(db, new CatalogClient(http), new ProducerNatsClient(connection),
                inventory, NullLogger<OrdersHandler>.Instance);
            await handler.CreateOrderAsync(new CreateOrderRequest([new CreateOrderItem(Guid.NewGuid(), 1, 1)]), stop.Token);
            var saved = await db.Orders.AsNoTracking().SingleAsync(stop.Token);
            Assert.Equal(DomainStatus.Error, saved.Status);
            Assert.Contains("Insufficient stock for material", saved.ErrorMessage);
            Assert.True(released.Task.IsCompletedSuccessfully);
            Assert.True(labelAfterRelease);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(Task.WhenAll(reserveLoop, releaseLoop));
        }
    });

    [PostgresFact(DatabaseVariable, NatsVariable)]
    public async Task CallerCancellationAfterReserveStillPersistsAndPublishes()
    {
        using var caller = new CancellationTokenSource();
        await WithDatabaseAsync(async (db, connection) =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var inventory = new InventoryNatsClient(connection);
            var loop = inventory.RespondReserveV1Async((task, _) => Task.FromResult(
                new InventoryReserveReplyV1(task.Id, task.ReservationId, InventoryReserveTaskStatus.Success, null)),
                (_, _) => { }, stop.Token);
            var jetStream = new NatsJSContext(connection);
            var stream = await jetStream.CreateStreamAsync(new StreamConfig(
                "test_" + Guid.NewGuid().ToString("N"), ["manufacturing.producer.tasks.start.v1"]));
            await connection.PingAsync(stop.Token);
            try
            {
                using var http = new HttpClient(new CatalogStub(() => { })) { BaseAddress = new Uri("http://catalog/") };
                var handler = new OrdersHandler(db, new CatalogClient(http), new ProducerNatsClient(connection),
                    inventory, NullLogger<OrdersHandler>.Instance);
                await handler.CreateOrderAsync(new CreateOrderRequest([new CreateOrderItem(Guid.NewGuid(), 1, 1)]), caller.Token);
                Assert.True(caller.IsCancellationRequested);
                Assert.Equal(DomainStatus.Reserved, (await db.Orders.AsNoTracking().SingleAsync()).Status);
                await stream.RefreshAsync();
                Assert.Equal(1L, stream.Info.State.Messages);
            }
            finally
            {
                await stream.DeleteAsync();
                await stop.CancelAsync();
                await IgnoreCancellationAsync(loop);
            }
        }, new CancelAfterReservation(caller));
    }

    [PostgresFact(DatabaseVariable, NatsVariable)]
    public Task HandlerFailureReturnsAnErrorInsteadOfTimingOut() => WithDatabaseAsync(async (_, connection) =>
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inventory = new InventoryNatsClient(connection);
        var loop = inventory.RespondReserveV1Async((_, _) => throw new InvalidOperationException("bad\npayload"),
            (_, _) => { }, stop.Token);
        await connection.PingAsync(stop.Token);
        try
        {
            var error = await Assert.ThrowsAsync<NatsException>(async () =>
                await inventory.RequestReserveV1Async(new InventoryReserveTaskV1(Guid.NewGuid(), 1), stop.Token));
            Assert.Contains("bad payload", error.Message);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(loop);
        }
    });

    private static async Task IgnoreCancellationAsync(Task loop)
    {
        try { await loop; }
        catch (OperationCanceledException) { }
    }

    private static async Task WithDatabaseAsync(Func<OrdersDbContext, NatsConnection, Task> test, IInterceptor? interceptor = null)
    {
        var database = "orders_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable(DatabaseVariable));
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(DatabaseVariable)!)
                { Database = database, Pooling = false }.ConnectionString;
            var options = new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            await using var db = new OrdersDbContext(options.Options);
            await db.Database.MigrateAsync();
            await using var connection = new NatsConnection(new NatsOpts
                {
                    Url = Environment.GetEnvironmentVariable(NatsVariable)!,
                    SerializerRegistry = new NatsJsonContextSerializerRegistry(TestNatsContext.Default),
                });
            await test(db, connection);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class CancelAfterReservation(CancellationTokenSource caller) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<DomainOrder>().Any(e => e.Entity.Status == DomainStatus.Reserved))
                caller.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CatalogStub(Action beforeMaterialLookup) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("bill-of-material"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new BillOfMaterial(Guid.NewGuid(), 1,
                        [new BillOfMaterialItem(Guid.NewGuid(), 2)]))
                });
            beforeMaterialLookup();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}

[JsonSerializable(typeof(InventoryReserveTaskV1))]
[JsonSerializable(typeof(InventoryReserveReplyV1))]
[JsonSerializable(typeof(InventoryReleaseTaskV1))]
[JsonSerializable(typeof(InventoryReleaseReplyV1))]
[JsonSerializable(typeof(ProducerTaskV1))]
internal partial class TestNatsContext : JsonSerializerContext;
