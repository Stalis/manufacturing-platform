using System.Net;
using System.Net.Http.Json;
using Catalog.Api.Endpoints;
using Catalog.Api.Persistence;
using Catalog.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Domain = Manufacturing.Core.Models;

namespace Catalog.Seed.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CATALOG_SEED_TEST_CONNECTION")))
            Skip = "Set CATALOG_SEED_TEST_CONNECTION to a disposable PostgreSQL server with CREATEDB permission.";
    }
}

public class PostgresIntegrationTests
{
    [PostgresFact]
    public async Task RealApiMigrationConflictsAndReruns()
    {
        var adminConnection = Environment.GetEnvironmentVariable("CATALOG_SEED_TEST_CONNECTION")!;
        var database = "seed_test_" + Guid.NewGuid().ToString("N");
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
            var options = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connection).Options;
            await using (var db = new CatalogDbContext(options))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20261015142523_InitialCreate");
                db.Materials.AddRange(Enumerable.Range(0, 2).Select(_ => new Domain.Material
                {
                    SKU = "DUPLICATE", Name = "Duplicate", MeasurementUnit = Domain.MeasurementUnit.Each,
                }));
                await db.SaveChangesAsync();
                var duplicate = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
                Assert.Contains("Material SKU: DUPLICATE", duplicate.MessageText);
                Assert.Equal(2, await db.Materials.CountAsync());
                await db.Materials.ExecuteDeleteAsync();
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "Products" (id, "SKU", name) VALUES ('00000000-0000-0000-0000-000000000001', 'LEGACY-P', 'Legacy product');
                                                     INSERT INTO "Materials" (id, "SKU", name, measurement_unit) VALUES ('00000000-0000-0000-0000-000000000002', 'LEGACY-M', 'Legacy material', 0);
                                                     INSERT INTO "Materials" (id, "SKU", name, measurement_unit) VALUES ('00000000-0000-0000-0000-000000000004', 'LEGACY-M2', 'Legacy material 2', 0);
                                                     INSERT INTO "BillOfMaterialItems" (id, "ProductId", "MaterialId", quantity)
                                                     VALUES ('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002', 7);
                                                     INSERT INTO "BillOfMaterialItems" (id, "ProductId", "MaterialId", quantity)
                                                     VALUES ('00000000-0000-0000-0000-000000000005', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000004', 3);
                                                     """);
                await migrator.MigrateAsync();
                Assert.Equal(10, await db.BillOfMaterialItems.SumAsync(m => m.Quantity));
                Assert.Equal(2, await db.BillOfMaterialItems.CountAsync());
                Assert.Single(await db.BillOfMaterialItems.Select(m => m.RevisionId).Distinct().ToListAsync());
                Assert.Equal(1u, (await db.BillOfMaterialRevisions.SingleAsync()).Revision);
                await db.Products.ExecuteDeleteAsync();
                await db.Materials.ExecuteDeleteAsync();
            }

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(connection));
            builder.Services.AddCatalogEndpoints();
            await using var app = builder.Build();
            app.MapCatalogEndpoints();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var client = new CatalogClient(http);
            var seed = SeedGenerator.Generate(SeedGeneratorTests.Data());
            var output = new StringWriter();
            var seeder = new CatalogSeeder(client, output);
            await seeder.RunAsync(seed);
            Assert.Equal(557, (await client.ListMaterialsAsync()).Length);
            Assert.Equal(12, (await client.ListProductsAsync()).Length);
            output.GetStringBuilder().Clear();
            await seeder.RunAsync(seed);
            Assert.Contains("Materials: 0 created, 0 updated, 557 unchanged", output.ToString());
            Assert.Contains("BOMs: 0 created, 0 updated, 12 unchanged", output.ToString());

            var product = (await client.ListProductsAsync()).Single(p => p.Sku == seed.Products[0].Sku);
            var material = (await client.ListMaterialsAsync()).First();
            await AssertStatus(HttpStatusCode.Conflict,
                               () => client.CreateProductAsync(new ProductInput(product.Sku, product.Name)));
            await AssertStatus(HttpStatusCode.Conflict, () => client.CreateMaterialAsync(
                                   new MaterialInput(material.Sku, material.Name, material.MeasurementUnit)));
            var bom = await client.GetBillOfMaterialAsync(product.Id, 1);
            await AssertStatus(HttpStatusCode.Conflict, () => client.CreateBillOfMaterialAsync(bom));

            // Replacing overlapping BOM lines exercises unique-index delete/insert ordering in EF.
            var changed = seed.Products[0] with
            {
                Name = "Updated product",
                Materials = seed.Products[0].Materials.Select(m => m with { Quantity = m.Quantity + 1 }).ToArray(),
            };
            var updated = seed with { Products = [changed, .. seed.Products.Skip(1)] };
            await seeder.RunAsync(updated);
            Assert.Equal("Updated product", (await client.GetProductAsync(product.Id)).Name);
            Assert.Equal(changed.Materials.Sum(m => m.Quantity),
                         (await client.GetBillOfMaterialAsync(product.Id, 1)).Materials.Sum(m => m.Quantity));

            var nextRevision = updated with
            {
                Products = [changed with { Revision = 2, Materials = [changed.Materials[0]] }],
            };
            await seeder.RunAsync(nextRevision);
            Assert.Equal(2u, (await client.GetBillOfMaterialAsync(product.Id, 2)).Revision);
            Assert.Single((await client.GetBillOfMaterialAsync(product.Id, 2)).Materials);
            Assert.Equal(changed.Materials.Length,
                         (await client.GetBillOfMaterialAsync(product.Id, 1)).Materials.Length);

            // An empty revision remains discoverable and can be repaired on rerun.
            await client.ReplaceBillOfMaterialAsync(new BillOfMaterial(product.Id, 2, []));
            await seeder.RunAsync(nextRevision);
            Assert.Single((await client.GetBillOfMaterialAsync(product.Id, 2)).Materials);

            await AssertStatus(HttpStatusCode.Conflict, () => client.CreateBillOfMaterialAsync(
                                   new BillOfMaterial(product.Id, 3, [bom.Materials[0], bom.Materials[0]])));
            await AssertStatus(HttpStatusCode.BadRequest, () => client.CreateBillOfMaterialAsync(
                                   new BillOfMaterial(product.Id, 3, [new BillOfMaterialItem(Guid.NewGuid(), 1)])));
            await AssertStatus(HttpStatusCode.NotFound, () => client.GetBillOfMaterialAsync(product.Id, 3));
            await using (var db = new CatalogDbContext(options))
            {
                var revisionId = await db.BillOfMaterialRevisions
                                         .Where(r => r.ProductId == product.Id && r.Revision == 2)
                                         .Select(r => r.Id).SingleAsync();
                await AssertStatus(HttpStatusCode.NotFound,
                                   () => client.GetBillOfMaterialByRevisionIdAsync(Guid.NewGuid(), revisionId));
            }

            // Contract records reject bodies that omit required properties.
            using var incomplete = await http.PutAsJsonAsync($"/api/catalog/products/{product.Id}",
                                                             new { name = "Missing SKU" });
            Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
            await AssertStatus(HttpStatusCode.BadRequest, () => client.CreateBillOfMaterialAsync(
                                   new BillOfMaterial(Guid.NewGuid(), 1, [new BillOfMaterialItem(material.Id, 1)])));
            using var materialInUse = await http.DeleteAsync($"/api/catalog/materials/{bom.Materials[0].MaterialId}");
            Assert.Equal(HttpStatusCode.Conflict, materialInUse.StatusCode);
            Assert.Equal("application/problem+json", materialInUse.Content.Headers.ContentType?.MediaType);
            await app.StopAsync();
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertStatus(HttpStatusCode status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(action);
        Assert.Equal(status, error.StatusCode);
    }
}