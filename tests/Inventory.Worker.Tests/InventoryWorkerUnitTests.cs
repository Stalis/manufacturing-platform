using System.Data;
using System.Data.Common;
using Inventory.Contracts.Models;
using Inventory.DAL;
using Inventory.DAL.Entities;
using Manufacturing.Nats.Contract;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;

namespace Inventory.Worker.Tests;

public class InventoryWorkerUnitTests
{
    [Fact]
    public async Task ReserveAccumulatesItemsRespectsStockLimitsAndRefusesCanceledReservation()
    {
        var store = new InMemoryInventoryStore();
        await using var harness = CreateWorker(store, defaultStock: 10);
        var material = Guid.CreateVersion7();
        var reservation = Guid.CreateVersion7();

        // First reserve creates stock (10) and deducts 4 -> 6 left
        var first = await harness.Worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 4, reservation), default);
        Assert.Equal(InventoryReserveTaskStatus.Success, first.Status);
        Assert.Equal(6u, store.Stocks[material]);

        // Second reserve of same material in same reservation accumulates item quantity (4 + 3 = 7) -> 3 left
        var second = await harness.Worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 3, reservation), default);
        Assert.Equal(InventoryReserveTaskStatus.Success, second.Status);
        Assert.Equal(3u, store.Stocks[material]);
        Assert.Equal(7u, store.Reservations[reservation].Items.Single().Quantity);

        // Exceeding remaining stock returns NotInStock without modifying stock
        var insufficient = await harness.Worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 5, reservation), default);
        Assert.Equal(InventoryReserveTaskStatus.NotInStock, insufficient.Status);
        Assert.Equal(3u, store.Stocks[material]);

        // Release returns stock to 10 and marks reservation Canceled
        var release = await harness.Worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);
        Assert.Equal(InventoryReleaseTaskStatus.Released, release.Status);
        Assert.Equal(10u, store.Stocks[material]);
        Assert.Equal(ReservationStatus.Canceled, store.Reservations[reservation].Status);

        // Late reserve on Canceled reservation is refused
        var lateReserve = await harness.Worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 1, reservation), default);
        Assert.Equal(InventoryReserveTaskStatus.Error, lateReserve.Status);
    }

    [Fact]
    public async Task ZeroDefaultStockAndReleaseBeforeReserveBehaveCorrectly()
    {
        var store = new InMemoryInventoryStore();
        await using var harness = CreateWorker(store, defaultStock: 0);
        var material = Guid.CreateVersion7();
        var reservation = Guid.CreateVersion7();

        // Release before reserve creates Canceled tombstone and returns NotFound
        var earlyRelease = await harness.Worker.HandleReleaseAsync(new InventoryReleaseTaskV1(reservation), default);
        Assert.Equal(InventoryReleaseTaskStatus.NotFound, earlyRelease.Status);
        Assert.Equal(ReservationStatus.Canceled, store.Reservations[reservation].Status);

        // With DefaultInitialStock = 0, unseeded material is NotInStock
        var reserve = await harness.Worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 1), default);
        Assert.Equal(InventoryReserveTaskStatus.NotInStock, reserve.Status);
    }

    [Fact]
    public async Task GrabValidatesReservationAndConsumesUntilComplete()
    {
        var store = new InMemoryInventoryStore();
        await using var harness = CreateWorker(store, defaultStock: 20);
        var material = Guid.CreateVersion7();
        var reservation = Guid.CreateVersion7();

        // Missing reservation -> ReservationIsEmpty
        var missingGrab = await harness.Worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, material, 1), default);
        Assert.Equal(InventoryGrabTaskStatus.ReservationIsEmpty, missingGrab.Status);

        await harness.Worker.HandleReserveAsync(new InventoryReserveTaskV1(material, 5, reservation), default);

        // Grabbing unknown material or more than reserved -> Failure
        var unknownMat = await harness.Worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, Guid.CreateVersion7(), 1), default);
        Assert.Equal(InventoryGrabTaskStatus.Failure, unknownMat.Status);

        var tooMuch = await harness.Worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, material, 6), default);
        Assert.Equal(InventoryGrabTaskStatus.Failure, tooMuch.Status);

        // Partial grab (2 of 5) commits ConsumedQuantity = 2 and keeps status Reserved
        // (NATS publish throws after commit because no server is running on 127.0.0.1:1)
        await Assert.ThrowsAnyAsync<NatsException>(() =>
            harness.Worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, material, 2), default));
        Assert.Equal(2u, store.Reservations[reservation].Items.Single().ConsumedQuantity);
        Assert.Equal(ReservationStatus.Reserved, store.Reservations[reservation].Status);

        // Remaining grab (3 of 5) marks reservation Consumed
        await Assert.ThrowsAnyAsync<NatsException>(() =>
            harness.Worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, material, 3), default));
        Assert.Equal(5u, store.Reservations[reservation].Items.Single().ConsumedQuantity);
        Assert.Equal(ReservationStatus.Consumed, store.Reservations[reservation].Status);

        // Grabbing from an already-Consumed reservation returns ReservationIsEmpty
        var afterConsumed = await harness.Worker.HandleGrabAsync(new InventoryGrabTaskV1(reservation, material, 1), default);
        Assert.Equal(InventoryGrabTaskStatus.ReservationIsEmpty, afterConsumed.Status);
    }

    private static WorkerHarness CreateWorker(InMemoryInventoryStore store, uint defaultStock)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Inventory:DefaultInitialStock"] = defaultStock.ToString(),
            })
            .Build();
        var services = new ServiceCollection()
            .AddDbContext<InventoryDbContext>(o => o
                .UseNpgsql("Host=127.0.0.1")
                .AddInterceptors(store, new NoOpConnectionInterceptor(), new NoOpTransactionInterceptor()))
            .BuildServiceProvider();
        var nats = new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:1", ConnectTimeout = TimeSpan.FromMilliseconds(50) });
        var worker = new Worker(
            NullLogger<Worker>.Instance,
            new InventoryNatsClient(nats),
            services.GetRequiredService<IServiceScopeFactory>(),
            config);
        return new WorkerHarness(worker, services, nats);
    }

    private sealed record WorkerHarness(Worker Worker, ServiceProvider Services, NatsConnection Nats) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Nats.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    private sealed class InMemoryInventoryStore : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public Dictionary<Guid, (ReservationStatus Status, List<ReservationItem> Items)> Reservations { get; } = [];
        public Dictionary<Guid, uint> Stocks { get; } = [];

        public override InterceptionResult<DbCommand> CommandCreating(
            CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result) =>
            InterceptionResult<DbCommand>.SuppressWithResult(new FakeDbCommand());

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var sql = command.CommandText;
            if (sql.Contains("INSERT INTO \"Reservations\""))
            {
                var id = (Guid)command.Parameters[0].Value!;
                var status = (ReservationStatus)(int)command.Parameters[1].Value!;
                var inserted = Reservations.TryAdd(id, (status, [])) ? 1 : 0;
                return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(inserted));
            }
            if (sql.Contains("INSERT INTO \"MaterialStocks\""))
            {
                var id = (Guid)command.Parameters[0].Value!;
                var qty = (uint)(long)command.Parameters[1].Value!;
                var inserted = Stocks.TryAdd(id, qty) ? 1 : 0;
                return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(inserted));
            }
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            var sql = command.CommandText;
            var id = (Guid)command.Parameters[0].Value!;
            if (sql.Contains("FROM \"Reservations\""))
            {
                var table = new DataTable();
                table.Columns.Add("Id", typeof(Guid));
                table.Columns.Add("Status", typeof(int));
                if (Reservations.TryGetValue(id, out var res))
                    table.Rows.Add(id, (int)res.Status);
                return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
            }
            if (sql.Contains("FROM \"ReservationItems\""))
            {
                var table = new DataTable();
                table.Columns.Add("ReservationId", typeof(Guid));
                table.Columns.Add("MaterialId", typeof(Guid));
                table.Columns.Add("ConsumedQuantity", typeof(long));
                table.Columns.Add("Quantity", typeof(long));
                if (Reservations.TryGetValue(id, out var res))
                    foreach (var item in res.Items)
                        table.Rows.Add(item.ReservationId, item.MaterialId, (long)item.ConsumedQuantity, (long)item.Quantity);
                return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
            }
            if (sql.Contains("FROM \"MaterialStocks\""))
            {
                var table = new DataTable();
                table.Columns.Add("Id", typeof(Guid));
                table.Columns.Add("Quantity", typeof(long));
                if (Stocks.TryGetValue(id, out var qty))
                    table.Rows.Add(id, (long)qty);
                return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
            }
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader()));
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var tracker = eventData.Context!.ChangeTracker;
            foreach (var entry in tracker.Entries<Reservation>())
                if (Reservations.TryGetValue(entry.Entity.Id, out var current))
                    Reservations[entry.Entity.Id] = (entry.Entity.Status, current.Items);

            foreach (var entry in tracker.Entries<ReservationItem>())
            {
                var items = Reservations[entry.Entity.ReservationId].Items;
                var idx = items.FindIndex(i => i.MaterialId == entry.Entity.MaterialId);
                var copy = new ReservationItem
                {
                    ReservationId = entry.Entity.ReservationId,
                    MaterialId = entry.Entity.MaterialId,
                    Quantity = entry.Entity.Quantity,
                    ConsumedQuantity = entry.Entity.ConsumedQuantity,
                };
                if (idx >= 0) items[idx] = copy;
                else items.Add(copy);
            }

            foreach (var entry in tracker.Entries<MaterialStock>())
                Stocks[entry.Entity.Id] = entry.Entity.Quantity;

            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }

    private sealed class NoOpConnectionInterceptor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken ct = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class NoOpTransactionInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default) =>
            ValueTask.FromResult(InterceptionResult<DbTransaction>.SuppressWithResult(new FakeDbTransaction(connection)));
    }

    private sealed class FakeDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeDbCommand : DbCommand
    {
        private readonly FakeParameterCollection _parameters = new();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new Npgsql.NpgsqlParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new DataTable().CreateDataReader();
    }

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (DbParameter p in values) _items.Add(p); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items.First(p => p.ParameterName == parameterName);
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAll(p => p.ParameterName == parameterName);
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }
}
