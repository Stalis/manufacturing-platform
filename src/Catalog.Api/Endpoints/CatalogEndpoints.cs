namespace Catalog.Api.Endpoints;

using Microsoft.AspNetCore.Routing;
using Catalog.Api.Persistence;
using Manufacturing.Core.Models;
using System.Linq;
using Microsoft.EntityFrameworkCore;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/catalog")
            .WithTags("Catalog");

        var materialGroup = group.MapGroup("/materials")
            .WithTags("Materials");
        materialGroup.MapGet("/{id:guid}", GetMaterialById)
            .WithName("GetMaterialById")
            .Produces<Material>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        var productGroup = group.MapGroup("/products")
            .WithTags("Products");
        productGroup.MapGet("/{id:guid}", GetProductById)
            .WithName("GetProductById")
            .Produces<Product>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        var billOfMaterialGroup = group.MapGroup("/bill-of-material")
            .WithTags("BillOfMaterial");

        return app;
    }


    public static async Task<IResult> GetProductById(Guid Id, CatalogDbContext db, CancellationToken cs)
    {
        var product = await db.Products.FindAsync([Id], cs);

        return product is not null ? Results.Ok(product) : Results.NotFound();
    }

    public static async Task<IResult> GetBillOfMaterialByProductId(Guid productId, CatalogDbContext db, CancellationToken cs)
    {
        var billOfMaterialItems = await db.BillOfMaterialItems
            .Where(b => b.ProductId == productId)
            .ToListAsync(cs);

        if (billOfMaterialItems.Count == 0)
        {
            return Results.NotFound();
        }

        var res = new BillOfMaterial
        {
            ProductId = productId,
            Materials = billOfMaterialItems.Select(b => new BillOfMaterial.Record(b.MaterialId, b.Quantity)).ToList()
        };

        return Results.Ok(res);
    }

    public static async Task<IResult> GetMaterialById(Guid Id, CatalogDbContext db, CancellationToken cs)
    {
        var material = await db.Materials.FindAsync([Id], cs);

        return material is not null ? Results.Ok(material) : Results.NotFound();
    }
}
