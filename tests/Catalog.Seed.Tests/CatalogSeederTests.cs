using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Seed;
using Manufacturing.Catalog.Client;
using Manufacturing.Core.Models;

namespace Catalog.Seed.Tests;

public class CatalogSeederTests
{
    internal static SeedCatalog SmallSeed() => new(
        [new("WIRE-24AWG-BLACK", "Wire 24 AWG Black", MeasurementUnit.Meter), new("PCB-2L", "PCB 2-layer", MeasurementUnit.SquareCentimeter)],
        [new("PRODUCT-TEST", "Test board", [new("PCB-2L", 80), new("WIRE-24AWG-BLACK", 2)])]);

    [Fact]
    public async Task RerunDoesNotWriteAndUpdatesPreserveIds()
    {
        using var server = new CatalogHandler();
        using var http = new HttpClient(server) { BaseAddress = new("http://test/") };
        var seeder = new CatalogSeeder(new CatalogClient(http), TextWriter.Null);
        var seed = SmallSeed();
        await seeder.RunAsync(seed);
        Assert.Equal(4, server.Writes.Count);
        var ids = server.Materials.Select(m => m.Id).Append(server.Products.Single().Id).ToArray();
        server.Writes.Clear();
        server.Boms.Values.Single().Materials = server.Boms.Values.Single().Materials.Reverse().ToArray();
        await seeder.RunAsync(seed);
        Assert.Empty(server.Writes);
        seed = seed with
        {
            Materials = [seed.Materials[0] with { Name = "Renamed wire" }, seed.Materials[1]],
            Products = [seed.Products[0] with { Name = "Renamed board", Materials = [new("PCB-2L", 90), new("WIRE-24AWG-BLACK", 3)] }]
        };
        await seeder.RunAsync(seed);
        Assert.Equal(3, server.Writes.Count);
        Assert.All(server.Writes, write => Assert.StartsWith("PUT", write));
        Assert.Equal(ids, server.Materials.Select(m => m.Id).Append(server.Products.Single().Id));
        server.Writes.Clear();
        await seeder.RunAsync(seed);
        Assert.Empty(server.Writes);
    }

    [Fact]
    public async Task DryRunDoesNotWriteEvenWhenEverythingIsMissing()
    {
        using var server = new CatalogHandler();
        using var http = new HttpClient(server) { BaseAddress = new("http://test/") };
        var output = new StringWriter();
        await new CatalogSeeder(new CatalogClient(http), output).RunAsync(SmallSeed(), dryRun: true);
        Assert.Empty(server.Writes);
        Assert.Contains("Materials: 2 created", output.ToString());
        Assert.Contains("BOMs: 1 created", output.ToString());
    }

    [Fact]
    public async Task ProductsOnlyChecksAllDependenciesAndNeverWritesMaterials()
    {
        using var server = new CatalogHandler();
        using var http = new HttpClient(server) { BaseAddress = new("http://test/") };
        var seeder = new CatalogSeeder(new CatalogClient(http), TextWriter.Null);
        var seed = SmallSeed();
        await Assert.ThrowsAsync<InvalidDataException>(() => seeder.RunAsync(seed, productsOnly: true));
        Assert.Empty(server.Writes);
        server.Materials.AddRange(seed.Materials.Select(m => new Manufacturing.Catalog.Client.Material
        {
            Id = Guid.NewGuid(), Sku = m.Sku, Name = "Keep existing name", MeasurementUnit = (int)m.MeasurementUnit
        }));
        server.Materials[0].MeasurementUnit = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => seeder.RunAsync(seed, productsOnly: true));
        Assert.Empty(server.Writes);
        server.Materials[0].MeasurementUnit = 5;
        await seeder.RunAsync(seed, productsOnly: true, dryRun: true);
        Assert.Empty(server.Writes);
        await seeder.RunAsync(seed, productsOnly: true);
        Assert.Equal(2, server.Writes.Count);
        Assert.DoesNotContain(server.Writes, w => w.Contains("/materials"));
        Assert.All(server.Materials, m => Assert.Equal("Keep existing name", m.Name));
    }

    [Fact]
    public async Task DuplicateRemoteSkuAndInvalidRecipeFailBeforeWrites()
    {
        using var server = new CatalogHandler();
        using var http = new HttpClient(server) { BaseAddress = new("http://test/") };
        var seeder = new CatalogSeeder(new CatalogClient(http), TextWriter.Null);
        var seed = SmallSeed();
        await Assert.ThrowsAsync<InvalidDataException>(() => seeder.RunAsync(seed with
        { Products = [seed.Products[0] with { Materials = [new("UNKNOWN", 1)] }] }));
        Assert.Empty(server.Writes);
        server.Materials.AddRange(Enumerable.Range(0, 2).Select(_ => new Manufacturing.Catalog.Client.Material
        { Id = Guid.NewGuid(), Sku = "DUPLICATE", Name = "Duplicate", MeasurementUnit = 0 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => seeder.RunAsync(seed));
        Assert.Empty(server.Writes);
    }

    [Fact]
    public async Task FailedBomCanBeResumedWithoutDuplicatingProduct()
    {
        using var server = new CatalogHandler { FailNextBom = true };
        using var http = new HttpClient(server) { BaseAddress = new("http://test/") };
        var seeder = new CatalogSeeder(new CatalogClient(http), TextWriter.Null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.RunAsync(SmallSeed()));
        Assert.Contains("BOM PRODUCT-TEST: HTTP 500", error.Message);
        var id = server.Products.Single().Id;
        server.Writes.Clear();
        await seeder.RunAsync(SmallSeed());
        Assert.Single(server.Writes);
        Assert.Equal(id, server.Products.Single().Id);
        Assert.Equal(2, server.Boms[id].Materials.Count);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public List<Manufacturing.Catalog.Client.Material> Materials { get; } = [];
        public List<Manufacturing.Catalog.Client.Product> Products { get; } = [];
        public Dictionary<Guid, Manufacturing.Catalog.Client.BillOfMaterial> Boms { get; } = [];
        public List<string> Writes { get; } = [];
        public bool FailNextBom { get; set; }
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            var method = request.Method.Method;
            if (method != "GET") Writes.Add($"{method} {path}");
            if (path == "/api/catalog/materials")
            {
                if (method == "GET") return Reply(Materials);
                var body = await request.Content!.ReadFromJsonAsync<Manufacturing.Catalog.Client.Material>(Json, ct);
                if (method == "POST") { body!.Id = Guid.NewGuid(); Materials.Add(body); return Reply(body, 201); }
                Materials[Materials.FindIndex(m => m.Id == body!.Id)] = body!;
                return Reply(null, 204);
            }
            if (path == "/api/catalog/api/products")
            {
                if (method == "GET") return Reply(Products);
                var body = await request.Content!.ReadFromJsonAsync<Manufacturing.Catalog.Client.Product>(Json, ct);
                body!.Id = Guid.NewGuid(); Products.Add(body); return Reply(body, 201);
            }
            if (path.StartsWith("/api/catalog/api/products/") && method == "PUT")
            {
                var body = await request.Content!.ReadFromJsonAsync<Manufacturing.Catalog.Client.Product>(Json, ct);
                Assert.EndsWith(body!.Id.ToString(), path);
                Products[Products.FindIndex(p => p.Id == body.Id)] = body;
                return Reply(null, 204);
            }
            if (method == "GET" && path.StartsWith("/api/catalog/bill-of-material/"))
                return Boms.TryGetValue(Guid.Parse(path.Split('/')[4]), out var bom) && bom.Revision == uint.Parse(path.Split('/')[^1])
                    ? Reply(bom) : Reply(null, 404);
            if (path == "/api/catalog/bill-of-material")
            {
                if (FailNextBom) { FailNextBom = false; return Reply("Simulated failure", 500); }
                var body = await request.Content!.ReadFromJsonAsync<Manufacturing.Catalog.Client.BillOfMaterial>(Json, ct);
                Assert.Contains(Products, p => p.Id == body!.ProductId);
                Assert.All(body!.Materials, item => Assert.Contains(Materials, m => m.Id == item.MaterialId));
                Boms[body.ProductId] = body;
                return Reply(body, method == "POST" ? 201 : 204);
            }
            throw new InvalidOperationException($"Unexpected request: {method} {path}");
        }

        private static HttpResponseMessage Reply(object? body, int status = 200) => new((HttpStatusCode)status)
        { Content = JsonContent.Create(body, options: Json) };
    }
}
