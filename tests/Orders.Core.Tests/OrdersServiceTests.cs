using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Catalog.Contracts;
using Manufacturing.Nats.Contract;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Orders.Api;
using Orders.Api.Endpoints;
using Orders.Api.Persistence;
using Orders.Contracts;
using Producer.Contracts.Models;
using DomainOrder = Orders.Core.Models.Order;
using DomainStatus = Orders.Core.Models.OrderStatus;
using DomainTaskStatus = Orders.Core.Models.OrderTaskStatus;
using ProducerWorker = Producer.Worker.Worker;

namespace Orders.Core.Tests;

public class OrdersServiceTests
{
    [Fact]
    public async Task GetListAndGetByTaskHandleFoundAndMissingOrders()
    {
        var taskId = Guid.CreateVersion7();
        var existing = new DomainOrder
        {
            Status = DomainStatus.InProgress,
            Items =
            [
                new DomainOrder.OrderItem
                {
                    ProductId = Guid.CreateVersion7(),
                    Revision = 1,
                    BOMRevision = Guid.CreateVersion7(),
                    Quantity = 1,
                    Tasks = [new DomainOrder.OrderProductionTask { TaskId = taskId, Status = DomainTaskStatus.InProgress }],
                },
            ],
        };
        var itemsJson = """[{"ProductId":"00000000-0000-0000-0000-000000000001","Revision":1,"BOMRevision":"00000000-0000-0000-0000-000000000002","Quantity":1,"Tasks":[{"TaskId":"""
                        + $"\"{taskId}\"" + ""","Status":1,"LastMessage":"Running"}]}]""";
        var hasMatch = true;

        await using var db = CreateDb(cmd =>
            hasMatch && cmd.CommandText.Contains("FROM \"Orders\"") && !cmd.CommandText.Contains("WHERE o.id =")
                ? OrderRow(existing.Id, null, "InProgress", itemsJson)
                : Empty());
        db.Orders.Attach(existing);

        await using var nats = UnreachableNats();
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.NotFound)))
            { BaseAddress = new Uri("http://catalog/") };
        var handler = new OrdersHandler(
            db, new CatalogClient(http), new ProducerNatsClient(nats), new InventoryNatsClient(nats),
            NullLogger<OrdersHandler>.Instance);

        var listed = await handler.ListOrdersAsync(default);
        Assert.Single(listed.Value!, o => o.Id == existing.Id && o.Status == OrderStatus.InProgress);

        var got = await handler.GetOrderAsync(existing.Id, default);
        Assert.Equal(existing.Id, Assert.IsType<Ok<Order>>(got.Result).Value!.Id);

        var byTask = await handler.GetOrderByTaskAsync(taskId, default);
        Assert.Equal(existing.Id, Assert.IsType<Ok<Order>>(byTask.Result).Value!.Id);

        hasMatch = false;
        Assert.IsType<NotFound<ProblemDetails>>((await handler.GetOrderAsync(Guid.CreateVersion7(), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>((await handler.GetOrderByTaskAsync(Guid.CreateVersion7(), default)).Result);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    [InlineData(1, 10_001)]
    public async Task CreateOrderRejectsInvalidItemParameters(uint revision, int quantity)
    {
        await using var db = CreateDb(_ => Empty());
        await using var nats = UnreachableNats();
        using var http = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)))
            { BaseAddress = new Uri("http://catalog/") };
        var handler = new OrdersHandler(
            db, new CatalogClient(http), new ProducerNatsClient(nats), new InventoryNatsClient(nats),
            NullLogger<OrdersHandler>.Instance);

        var emptyResult = await handler.CreateOrderAsync(new CreateOrderRequest([]), default);
        Assert.IsType<BadRequest<ProblemDetails>>(emptyResult.Result);

        var itemResult = await handler.CreateOrderAsync(
            new CreateOrderRequest([new CreateOrderItem(Guid.CreateVersion7(), revision, quantity)]), default);
        Assert.IsType<BadRequest<ProblemDetails>>(itemResult.Result);
    }

    [Fact]
    public async Task CreateOrderHandlesMissingBomOverflowAndReserveFailureWithEnrichedLabel()
    {
        var productId = Guid.CreateVersion7();
        var materialId = Guid.CreateVersion7();
        var materialPerUnit = 2;

        await using var db = CreateDb(_ => Empty());
        await using var nats = UnreachableNats();
        using var http = new HttpClient(new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains($"/bill-of-material/{productId}/revisions/99"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.Contains("/bill-of-material/"))
                return Reply(new BillOfMaterial(productId, 1, [new(materialId, materialPerUnit)]), HttpStatusCode.OK);
            if (path.EndsWith($"/materials/{materialId}"))
                return Reply(new Material(materialId, "WIRE-1", "Copper Wire", MeasurementUnit.Meter), HttpStatusCode.OK);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })) { BaseAddress = new Uri("http://catalog/") };

        var handler = new OrdersHandler(
            db, new CatalogClient(http), new ProducerNatsClient(nats), new InventoryNatsClient(nats),
            NullLogger<OrdersHandler>.Instance);

        // 1. Missing BOM in Catalog -> 404 NotFound
        var missingBom = await handler.CreateOrderAsync(
            new CreateOrderRequest([new CreateOrderItem(productId, 99, 1)]), default);
        Assert.IsType<NotFound<ProblemDetails>>(missingBom.Result);

        // 2. Material quantity * order quantity > uint.MaxValue -> 400 BadRequest
        materialPerUnit = 1_000_000;
        var overflow = await handler.CreateOrderAsync(
            new CreateOrderRequest([new CreateOrderItem(productId, 1, 5_000)]), default);
        Assert.IsType<BadRequest<ProblemDetails>>(overflow.Result);

        // 3. Reserve failure enriches error message with material name & SKU from Catalog
        materialPerUnit = 2;
        var failedReserve = await handler.CreateOrderAsync(
            new CreateOrderRequest([new CreateOrderItem(productId, 1, 2)]), default);
        var created = Assert.IsType<Created<Order>>(failedReserve.Result).Value!;
        Assert.Equal(OrderStatus.Error, created.Status);
        Assert.Equal("Failed to reserve Copper Wire (WIRE-1) in inventory.", created.ErrorMessage);
    }

    [Fact]
    public async Task OrderProgressTrackerUpdatesTaskLifecycleAndIgnoresUnknownTasks()
    {
        var orderId = Guid.CreateVersion7();
        var task1 = Guid.CreateVersion7();
        var task2 = Guid.CreateVersion7();
        var itemsJson = $$"""
            [{"ProductId":"{{Guid.CreateVersion7()}}","Revision":1,"BOMRevision":"{{Guid.CreateVersion7()}}","Quantity":2,
              "Tasks":[{"TaskId":"{{task1}}","Status":0,"LastMessage":null},{"TaskId":"{{task2}}","Status":0,"LastMessage":null}]}]
            """;

        DomainOrder? lastSaved = null;
        await using var services = new ServiceCollection()
            .AddDbContext<OrdersDbContext>(o => o
                .UseNpgsql("Host=127.0.0.1")
                .AddInterceptors(
                    new NoOpConnectionInterceptor(),
                    new QueryInterceptor(_ => OrderRow(orderId, null, "Reserved", itemsJson)),
                    new CaptureSaveInterceptor(order =>
                    {
                        lastSaved = order;
                        itemsJson = System.Text.Json.JsonSerializer.Serialize(order.Items);
                    })))
            .BuildServiceProvider();

        await using var nats = UnreachableNats();
        var tracker = new OrderProgressTracker(
            NullLogger<OrderProgressTracker>.Instance,
            new ProducerNatsClient(nats),
            services.GetRequiredService<IServiceScopeFactory>());

        // Unknown task returns false
        Assert.False(await tracker.HandleProducerEventAsync(
            new ProducerEventV1(Guid.CreateVersion7(), ProducerEventType.TaskStarted, "ignored"), default));

        // Task 1 starts -> InProgress (0%)
        Assert.True(await tracker.HandleProducerEventAsync(
            new ProducerEventV1(task1, ProducerEventType.TaskStarted, "Started"), default));
        Assert.Equal(DomainStatus.InProgress, lastSaved!.Status);
        Assert.Equal(1, lastSaved.InProgressTasksCount);

        // Task 1 completes -> InProgress (50%)
        Assert.True(await tracker.HandleProducerEventAsync(
            new ProducerEventV1(task1, ProducerEventType.TaskCompleted, "Completed 1"), default));
        Assert.Equal(DomainStatus.InProgress, lastSaved.Status);
        Assert.Equal(50, lastSaved.ProgressPercentage);

        // Task 2 stops -> InProgress
        Assert.True(await tracker.HandleProducerEventAsync(
            new ProducerEventV1(task2, ProducerEventType.TaskStopped, "Stopped"), default));
        Assert.Equal(DomainTaskStatus.Stopped, lastSaved.Items[0].Tasks[1].Status);

        // Task 2 completes -> Done (100%)
        Assert.True(await tracker.HandleProducerEventAsync(
            new ProducerEventV1(task2, ProducerEventType.TaskCompleted, "Completed 2"), default));
        Assert.Equal(DomainStatus.Done, lastSaved.Status);
        Assert.Equal(100, lastSaved.ProgressPercentage);

        // TaskInvalid -> Failed / Error
        Assert.True(await tracker.HandleProducerEventAsync(
            new ProducerEventV1(task2, ProducerEventType.TaskInvalid, "Invalid task"), default));
        Assert.Equal(DomainStatus.Error, lastSaved.Status);
        Assert.Equal("Invalid task", lastSaved.ErrorMessage);
    }

    [Fact]
    public async Task OrdersClientAndEndpointsRegistrationWorkAsExpected()
    {
        var orderId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var sample = new Order(orderId, OrderStatus.Reserved, null, 1, 0, 0, 0,
            [new OrderItem(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), 1, 0,
                [new OrderTask(taskId, OrderTaskStatus.Pending, null)])]);

        using var http = new HttpClient(new StubHandler(req => req.Method.Method switch
        {
            "GET" when req.RequestUri!.AbsolutePath == "/api/orders" => Reply(new[] { sample }, HttpStatusCode.OK),
            "GET" when req.RequestUri!.AbsolutePath == $"/api/orders/{orderId}" => Reply(sample, HttpStatusCode.OK),
            "GET" when req.RequestUri!.AbsolutePath == $"/api/orders/by-task/{taskId}" => Reply(sample, HttpStatusCode.OK),
            "POST" when req.RequestUri!.AbsolutePath == "/api/orders" => Reply(sample, HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://orders/") };

        var client = new OrdersClient(http);
        Assert.Single(await client.ListOrdersAsync());
        Assert.Equal(orderId, (await client.GetOrderAsync(orderId)).Id);
        Assert.Equal(orderId, (await client.GetOrderByTaskAsync(taskId)).Id);
        Assert.Equal(orderId, (await client.CreateOrderAsync(new CreateOrderRequest([]))).Id);

        var notFound = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetOrderAsync(Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        await using var nats = UnreachableNats();
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<OrdersDbContext>(o => o.UseNpgsql("Host=127.0.0.1"))
            .AddSingleton(new CatalogClient(http))
            .AddSingleton(new ProducerNatsClient(nats))
            .AddSingleton(new InventoryNatsClient(nats))
            .AddOrdersEndpoints()
            .BuildServiceProvider();

        using var scope = services.CreateScope();
        Assert.IsType<OrdersHandler>(scope.ServiceProvider.GetRequiredService<IOrdersHandler>());
        Assert.Throws<InvalidOperationException>(() => OrdersApiEndpoints.MapEnum<OrderStatus>((DomainStatus)99));
    }

    [Fact]
    public async Task ProducerWorkerDoWorkRespectsCancellation()
    {
        await using var nats = UnreachableNats();
        var worker = new ProducerWorker(
            NullLogger<ProducerWorker>.Instance, new ProducerNatsClient(nats), new InventoryNatsClient(nats));
        var task = new ProducerTaskV1(Guid.CreateVersion7(), 1, []);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Equal(ProducerWorker.ProductionResult.Stopped, await worker.DoWork(task, cancelled.Token));
        Assert.Equal(ProducerWorker.ProductionResult.Ok, await worker.DoWork(task, CancellationToken.None));
    }

    private static NatsConnection UnreachableNats() =>
        new(new NatsOpts { Url = "nats://127.0.0.1:1", ConnectTimeout = TimeSpan.FromMilliseconds(50) });

    private static OrdersDbContext CreateDb(Func<DbCommand, DbDataReader> responder)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql("Host=127.0.0.1")
            .AddInterceptors(new NoOpConnectionInterceptor(), new QueryInterceptor(responder), new CaptureSaveInterceptor(_ => { }))
            .Options;
        return new OrdersDbContext(options);
    }

    private static DbDataReader Empty() => new DataTable().CreateDataReader();

    private static DbDataReader OrderRow(Guid id, string? errorMessage, string status, string itemsJson)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(Guid));
        table.Columns.Add("error_message", typeof(string));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("items", typeof(string));
        table.Rows.Add(id, (object?)errorMessage ?? DBNull.Value, status, itemsJson);
        return table.CreateDataReader();
    }

    private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status) =>
        new(status) { Content = JsonContent.Create(value) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }

    private sealed class NoOpConnectionInterceptor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken ct = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class CaptureSaveInterceptor(Action<DomainOrder> onOrderSaved) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<DomainOrder>().FirstOrDefault()?.Entity is { } order)
                onOrderSaved(order);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }

    private sealed class QueryInterceptor(Func<DbCommand, DbDataReader> responder) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(responder(command)));
    }
}
