using Manufacturing.Catalog.Client;

namespace Catalog.Seed;

public sealed class CatalogSeeder(ICatalogClient client, TextWriter output)
{
    public async Task RunAsync(SeedCatalog seed, bool productsOnly = false, bool dryRun = false,
                               CancellationToken ct = default)
    {
        seed.Validate();
        var materials = SeedCatalog.Index(await client.GetAllMaterialsAsync(ct), m => m.Sku, "remote materials");
        var products = SeedCatalog.Index(await client.GetAllProductsAsync(ct), p => p.Sku, "remote products");
        var desiredMaterials = seed.Materials.ToDictionary(m => m.Sku);
        if (productsOnly)
            foreach (var sku in seed.Products.SelectMany(p => p.Materials).Select(m => m.Sku).Distinct())
            {
                SeedCatalog.Check(materials.TryGetValue(sku, out var material), $"Missing remote material: {sku}");
                SeedCatalog.Check(material.MeasurementUnit == (int)desiredMaterials[sku].MeasurementUnit,
                                  $"Measurement unit mismatch: {sku}");
            }

        // Read every existing BOM before writing, so preflight failures cannot leave a partial seed.
        var boms = new Dictionary<string, BillOfMaterial?>();
        foreach (var product in seed.Products)
        {
            BillOfMaterial? bom = null;
            if (products.TryGetValue(product.Sku, out var existing))
                try
                {
                    bom = await client.GetBillOfMaterialByProductIdAndRevisionAsync(existing.Id, product.Revision, ct);
                }
                catch (ApiException error) when (error.StatusCode == 404)
                {
                }

            if (bom is not null)
                SeedCatalog.Check(bom.Materials.Select(m => m.MaterialId).Distinct().Count() == bom.Materials.Count,
                                  $"Duplicate material in remote BOM: {product.Sku}");
            boms.Add(product.Sku, bom);
        }

        var materialCounts = new Counts();
        var productCounts = new Counts();
        var bomCounts = new Counts();
        var operation = "";
        try
        {
            if (!productsOnly)
                foreach (var desired in seed.Materials)
                {
                    materials.TryGetValue(desired.Sku, out var existing);
                    var action = existing is null ? "create" :
                        existing.Name != desired.Name
                        || existing.MeasurementUnit != (int)desired.MeasurementUnit ? "update" : "unchanged";
                    operation = $"{action} material {desired.Sku}";
                    Report(materialCounts, action, operation);
                    if (dryRun || action == "unchanged") continue;
                    if (existing is null)
                        materials.Add(desired.Sku, await client.CreateMaterialAsync(new CreateMaterialRequest
                        {
                            Sku = desired.Sku, Name = desired.Name, MeasurementUnit = (int)desired.MeasurementUnit,
                        }, ct));
                    else
                        await client.UpdateMaterialAsync(new UpdateMaterialRequest
                        {
                            Id = existing.Id, Sku = desired.Sku, Name = desired.Name,
                            MeasurementUnit = (int)desired.MeasurementUnit,
                        }, ct);
                }

            foreach (var desired in seed.Products)
            {
                products.TryGetValue(desired.Sku, out var existing);
                var action = existing is null ? "create" : existing.Name != desired.Name ? "update" : "unchanged";
                operation = $"{action} product {desired.Sku}";
                Report(productCounts, action, operation);
                if (!dryRun)
                {
                    if (existing is null)
                        existing = await client.CreateProductAsync(
                            new CreateProductRequest { Sku = desired.Sku, Name = desired.Name }, ct);
                    else if (action == "update")
                        await client.UpdateProductAsync(existing.Id, new UpdateProductRequest
                        {
                            Id = existing.Id, Sku = desired.Sku, Name = desired.Name,
                        }, ct);
                }

                var currentBom = boms[desired.Sku];
                var equal = currentBom is not null && currentBom.Materials.Count == desired.Materials.Length
                                                   && desired.Materials.All(item =>
                                                                                materials.TryGetValue(
                                                                                    item.Sku, out var material)
                                                                                && currentBom.Materials.Any(line =>
                                                                                    line.MaterialId ==
                                                                                    material.Id &&
                                                                                    line.Quantity ==
                                                                                    item.Quantity));
                var bomAction = currentBom is null ? "create" : equal ? "unchanged" : "update";
                operation = $"{bomAction} BOM {desired.Sku}";
                Report(bomCounts, bomAction, operation);
                if (dryRun || equal) continue;
                var bom = new BillOfMaterial
                {
                    ProductId = existing!.Id,
                    Revision = desired.Revision,
                    Materials = desired.Materials.Select(item => new Record
                    {
                        MaterialId = materials[item.Sku].Id, Quantity = item.Quantity,
                    }).ToArray(),
                };
                if (currentBom is null) await client.CreateBillOfMaterialAsync(bom, ct);
                else await client.ReplaceBillOfMaterialAsync(bom, ct);
            }
        }
        catch (ApiException error)
        {
            throw new InvalidOperationException($"Failed to {operation}: HTTP {error.StatusCode}. {error.Response}",
                                                error);
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException(
                $"Failed to {operation}: {error.Message}. Rerun to reconcile; the request may have succeeded.", error);
        }

        if (dryRun) output.WriteLine("Dry run: planned changes only; no writes sent.");
        output.WriteLine(productsOnly ? "Materials: skipped (products-only)" : $"Materials: {materialCounts}");
        output.WriteLine($"Products: {productCounts}");
        output.WriteLine($"BOMs: {bomCounts}");
    }

    private void Report(Counts counts, string action, string operation)
    {
        if (action == "create") counts.Created++;
        else if (action == "update") counts.Updated++;
        else counts.Unchanged++;
        if (action != "unchanged") output.WriteLine(operation);
    }

    private sealed class Counts
    {
        public int Created;
        public int Unchanged;
        public int Updated;

        public override string ToString()
        {
            return $"{Created} created, {Updated} updated, {Unchanged} unchanged";
        }
    }
}