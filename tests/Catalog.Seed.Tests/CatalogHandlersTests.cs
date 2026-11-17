using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Catalog.Api;
using Catalog.Api.Endpoints;
using Catalog.Api.Persistence;
using Catalog.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Domain = Manufacturing.Core.Models;
using Entities = Catalog.Core.Models;

namespace Catalog.Seed.Tests;

public class CatalogHandlersTests
{
    [Fact]
    public async Task ProductsHandlerCrudAndMissingCases()
    {
        var existingId = Guid.CreateVersion7();
        await using var db = CreateDb(cmd =>
            cmd.CommandText.Contains("FROM \"Products\"") && !cmd.CommandText.Contains("WHERE")
                ? Table(("id", existingId), ("name", "Existing"), ("SKU", "SKU-1"))
                : Empty());
        var handler = new ProductsHandler(db);

        var listed = await handler.ListProductsAsync(default);
        Assert.Single(listed.Value!, p => p.Id == existingId && p.Sku == "SKU-1" && p.Name == "Existing");

        var createdResult = await handler.CreateProductAsync(new ProductInput("SKU-2", "New Product"), default);
        var created = Assert.IsType<Created<Product>>(createdResult.Result);
        Assert.Equal("SKU-2", created.Value!.Sku);
        Assert.Equal($"/api/catalog/products/{created.Value.Id}", created.Location);

        var found = await handler.GetProductAsync(created.Value.Id, default);
        Assert.Equal("New Product", Assert.IsType<Ok<Product>>(found.Result).Value!.Name);

        var replaced = await handler.ReplaceProductAsync(created.Value.Id, new ProductInput("SKU-2B", "Renamed"), default);
        Assert.IsType<NoContent>(replaced.Result);
        Assert.Equal("SKU-2B", (await db.Products.FindAsync(created.Value.Id))!.SKU);

        var deleted = await handler.DeleteProductAsync(created.Value.Id, default);
        Assert.IsType<NoContent>(deleted.Result);

        var missingId = Guid.CreateVersion7();
        Assert.IsType<NotFound<ProblemDetails>>((await handler.GetProductAsync(missingId, default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.ReplaceProductAsync(missingId, new ProductInput("X", "Y"), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>((await handler.DeleteProductAsync(missingId, default)).Result);
    }

    [Fact]
    public async Task MaterialsHandlerCrudAndMissingCases()
    {
        var existingId = Guid.CreateVersion7();
        await using var db = CreateDb(cmd =>
            cmd.CommandText.Contains("FROM \"Materials\"") && !cmd.CommandText.Contains("WHERE")
                ? Table(("id", existingId), ("measurement_unit", (int)Domain.MeasurementUnit.Meter),
                        ("name", "Wire"), ("SKU", "WIRE-1"))
                : Empty());
        var handler = new MaterialsHandler(db);

        var listed = await handler.ListMaterialsAsync(default);
        Assert.Single(listed.Value!, m => m.Id == existingId && m.MeasurementUnit == MeasurementUnit.Meter);

        var createdResult = await handler.CreateMaterialAsync(
            new MaterialInput("PCB-1", "Board", MeasurementUnit.SquareCentimeter), default);
        var created = Assert.IsType<Created<Material>>(createdResult.Result);
        Assert.Equal(MeasurementUnit.SquareCentimeter, created.Value!.MeasurementUnit);

        var found = await handler.GetMaterialAsync(created.Value.Id, default);
        Assert.Equal("PCB-1", Assert.IsType<Ok<Material>>(found.Result).Value!.Sku);

        var replaced = await handler.ReplaceMaterialAsync(
            created.Value.Id, new MaterialInput("PCB-2", "Board v2", MeasurementUnit.Each), default);
        Assert.IsType<NoContent>(replaced.Result);
        Assert.Equal(Domain.MeasurementUnit.Each, (await db.Materials.FindAsync(created.Value.Id))!.MeasurementUnit);

        var deleted = await handler.DeleteMaterialAsync(created.Value.Id, default);
        Assert.IsType<NoContent>(deleted.Result);

        var missingId = Guid.CreateVersion7();
        Assert.IsType<NotFound<ProblemDetails>>((await handler.GetMaterialAsync(missingId, default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.ReplaceMaterialAsync(missingId, new MaterialInput("X", "Y", MeasurementUnit.Each), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>((await handler.DeleteMaterialAsync(missingId, default)).Result);
    }

    [Fact]
    public async Task BillsOfMaterialValidationAndCrudCases()
    {
        var productId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var matA = Guid.CreateVersion7();
        var matB = Guid.CreateVersion7();
        var matC = Guid.CreateVersion7();
        var revisionExists = true;
        var itemExists = true;

        await using var db = CreateDb(cmd =>
        {
            var sql = cmd.CommandText;
            if (!revisionExists) return sql.Contains("EXISTS") ? Table(("?column?", false)) : Empty();
            if (sql.Contains("EXISTS")) return Table(("?column?", true));
            if (sql.Contains("FROM \"BillOfMaterialRevisions\""))
                return sql.Contains("ProductId") && sql.Contains("Revision") && !sql.Contains("b.\"ProductId\", b.\"Revision\"")
                    ? Table(("id", revisionId))
                    : Table(("id", revisionId), ("ProductId", productId), ("Revision", 1L));
            if (sql.Contains("FROM \"BillOfMaterialItems\""))
            {
                if (!itemExists) return Empty();
                return sql.Contains("b.\"RevisionId\", b.\"MaterialId\", b.quantity")
                    ? Table(("RevisionId", revisionId), ("MaterialId", matA), ("quantity", 5))
                    : Table(("MaterialId", matA), ("quantity", 5));
            }
            return Empty();
        });

        var handler = new BillsOfMaterialHandler(db);

        // Create validation: revision 0, non-positive quantity, duplicate materials
        Assert.IsType<BadRequest<ProblemDetails>>(
            (await handler.CreateBillOfMaterialAsync(new BillOfMaterial(productId, 0, [new(matA, 1)]), default)).Result);
        Assert.IsType<BadRequest<ProblemDetails>>(
            (await handler.CreateBillOfMaterialAsync(new BillOfMaterial(productId, 1, [new(matA, 0)]), default)).Result);
        Assert.IsType<Conflict<ProblemDetails>>(
            (await handler.CreateBillOfMaterialAsync(new BillOfMaterial(productId, 1, [new(matA, 1), new(matA, 2)]), default)).Result);

        // Create success
        var created = await handler.CreateBillOfMaterialAsync(new BillOfMaterial(productId, 1, [new(matA, 3)]), default);
        Assert.IsType<Created<BillOfMaterial>>(created.Result);

        // Get by revision & by revisionId
        var got = await handler.GetBillOfMaterialAsync(productId, 1, default);
        Assert.Single(Assert.IsType<Ok<BillOfMaterial>>(got.Result).Value!.Materials);

        // Replace validation & success (updates matA, adds matB)
        Assert.IsType<BadRequest<ProblemDetails>>(
            (await handler.ReplaceBillOfMaterialAsync(new BillOfMaterial(productId, 0, []), default)).Result);
        Assert.IsType<Conflict<ProblemDetails>>(
            (await handler.ReplaceBillOfMaterialAsync(new BillOfMaterial(productId, 1, [new(matA, 1), new(matA, 2)]), default)).Result);
        var replaced = await handler.ReplaceBillOfMaterialAsync(
            new BillOfMaterial(productId, 1, [new(matA, 10), new(matB, 4)]), default);
        Assert.IsType<NoContent>(replaced.Result);

        // Item operations when item exists
        Assert.IsType<BadRequest<ProblemDetails>>(
            (await handler.AddBillOfMaterialItemAsync(productId, revisionId, new(matA, 0), default)).Result);
        Assert.IsType<Conflict<ProblemDetails>>(
            (await handler.AddBillOfMaterialItemAsync(productId, revisionId, new(matA, 2), default)).Result);
        Assert.IsType<BadRequest<ProblemDetails>>(
            (await handler.UpdateBillOfMaterialItemAsync(productId, revisionId, new(matA, 0), default)).Result);
        Assert.IsType<NoContent>(
            (await handler.UpdateBillOfMaterialItemAsync(productId, revisionId, new(matA, 9), default)).Result);
        Assert.IsType<NoContent>(
            (await handler.RemoveBillOfMaterialItemAsync(productId, revisionId, matA, default)).Result);

        // Item operations when item does not exist
        itemExists = false;
        Assert.IsType<Created>(
            (await handler.AddBillOfMaterialItemAsync(productId, revisionId, new(matC, 2), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.UpdateBillOfMaterialItemAsync(productId, revisionId, new(matC, 2), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.RemoveBillOfMaterialItemAsync(productId, revisionId, matC, default)).Result);

        // Delete revision success
        Assert.IsType<NoContent>((await handler.DeleteBillOfMaterialAsync(productId, revisionId, default)).Result);

        // Missing revision cases
        revisionExists = false;
        Assert.IsType<NotFound<ProblemDetails>>((await handler.GetBillOfMaterialAsync(productId, 99, default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>((await handler.GetBillOfMaterialByRevisionIdAsync(productId, revisionId, default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.ReplaceBillOfMaterialAsync(new BillOfMaterial(productId, 99, []), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>((await handler.DeleteBillOfMaterialAsync(productId, revisionId, default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.AddBillOfMaterialItemAsync(productId, revisionId, new(matA, 1), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.UpdateBillOfMaterialItemAsync(productId, revisionId, new(matA, 1), default)).Result);
        Assert.IsType<NotFound<ProblemDetails>>(
            (await handler.RemoveBillOfMaterialItemAsync(productId, revisionId, matA, default)).Result);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_Materials_SKU", "POST", 409)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_Products_SKU", "POST", 409)]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_BillOfMaterialRevisions_ProductId_Revision", "POST", 409)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "FK_BillOfMaterialItems_Materials_MaterialId", "DELETE", 409)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "FK_BillOfMaterialItems_Materials_MaterialId", "POST", 400)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "FK_BillOfMaterialRevisions_Products_ProductId", "POST", 400)]
    public async Task ConflictFilterMapsPostgresConstraintsToExpectedStatus(
        string sqlState, string constraint, string method, int expectedStatus)
    {
        var filter = new CatalogConflictFilter();
        var httpContext = new DefaultHttpContext { Request = { Method = method } };
        var context = new DefaultEndpointFilterInvocationContext(httpContext);
        var pgError = new PostgresException("error", "ERROR", "ERROR", sqlState, constraintName: constraint);

        var result = await filter.InvokeAsync(context, _ => throw new DbUpdateException("db error", pgError));

        var statusResult = Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false);
        Assert.Equal(expectedStatus, statusResult.StatusCode);
    }

    [Fact]
    public async Task ConflictFilterPassesThroughSuccessAndRethrowsUnknownErrors()
    {
        var filter = new CatalogConflictFilter();
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        Assert.Equal("ok", await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("ok")));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            filter.InvokeAsync(context, _ => throw new DbUpdateException("unhandled")).AsTask());
    }

    [Fact]
    public void AddCatalogEndpointsRegistersAllHandlers()
    {
        var services = new ServiceCollection()
            .AddDbContext<CatalogDbContext>(o => o.UseNpgsql("Host=127.0.0.1"))
            .AddCatalogEndpoints()
            .BuildServiceProvider();

        using var scope = services.CreateScope();
        Assert.IsType<MaterialsHandler>(scope.ServiceProvider.GetRequiredService<IMaterialsHandler>());
        Assert.IsType<ProductsHandler>(scope.ServiceProvider.GetRequiredService<IProductsHandler>());
        Assert.IsType<BillsOfMaterialHandler>(scope.ServiceProvider.GetRequiredService<IBillsOfMaterialHandler>());
    }

    [Fact]
    public async Task CatalogClientCoversRemainingOperationsAndErrors()
    {
        var productId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var materialId = Guid.CreateVersion7();
        var bom = new BillOfMaterial(productId, 1, [new(materialId, 2)]);

        using var http = new HttpClient(new StubHandler(req => req.Method.Method switch
        {
            "GET" when req.RequestUri!.AbsolutePath.EndsWith($"/materials/{materialId}") =>
                Reply(new Material(materialId, "M-1", "Mat", MeasurementUnit.Each), HttpStatusCode.OK),
            "GET" when req.RequestUri!.AbsolutePath.EndsWith($"/{revisionId}") =>
                Reply(bom, HttpStatusCode.OK),
            "POST" when req.RequestUri!.AbsolutePath.EndsWith("/items") =>
                new HttpResponseMessage(HttpStatusCode.Created),
            "DELETE" or "PUT" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        })) { BaseAddress = new Uri("http://catalog/") };

        var client = new CatalogClient(http);
        Assert.Equal("M-1", (await client.GetMaterialAsync(materialId)).Sku);
        Assert.Equal(1u, (await client.GetBillOfMaterialByRevisionIdAsync(productId, revisionId)).Revision);
        await client.DeleteMaterialAsync(materialId);
        await client.DeleteProductAsync(productId);
        await client.DeleteBillOfMaterialAsync(productId, revisionId);
        await client.AddBillOfMaterialItemAsync(productId, revisionId, new(materialId, 3));
        await client.UpdateBillOfMaterialItemAsync(productId, revisionId, new(materialId, 5));
        await client.RemoveBillOfMaterialItemAsync(productId, revisionId, materialId);

        var err = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProductAsync(productId));
        Assert.Equal(HttpStatusCode.NotFound, err.StatusCode);
    }

    private static CatalogDbContext CreateDb(Func<DbCommand, DbDataReader> responder)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=127.0.0.1")
            .AddInterceptors(new NoOpConnectionInterceptor(), new QueryInterceptor(responder), new NoOpSaveInterceptor())
            .Options;
        return new CatalogDbContext(options);
    }

    private static DataTableReader Empty() => new DataTable().CreateDataReader();

    private static DataTableReader Table(params (string Name, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, value) in columns)
            table.Columns.Add(name, value.GetType());
        table.Rows.Add(columns.Select(c => c.Value).ToArray());
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

    private sealed class NoOpSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
    }

    private sealed class QueryInterceptor(Func<DbCommand, DbDataReader> responder) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(responder(command)));
    }
}
