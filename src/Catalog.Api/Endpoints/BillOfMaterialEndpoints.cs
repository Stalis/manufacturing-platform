using Microsoft.EntityFrameworkCore;
using Catalog.Api.Persistence;
using Manufacturing.Core.Models;
using Catalog.Core.Models;


namespace Catalog.Api.Endpoints;

public static class BillOfMaterialEndpoints
{
    public static IEndpointRouteBuilder MapBillOfMaterialEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/bill-of-material")
            .WithTags("BillOfMaterial");

        group.MapGet("/{productId:guid}/{revisionId:guid}", GetBillOfMaterialByProductIdAndRevisionId)
            .WithName("GetBillOfMaterialByProductIdAndRevisionId")
            .Produces<BillOfMaterial>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateBillOfMaterial)
            .WithName("CreateBillOfMaterial")
            .Accepts<BillOfMaterial>("application/json")
            .Produces<BillOfMaterial>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPut("/", ReplaceBillOfMaterial)
            .WithName("ReplaceBillOfMaterial")
            .Accepts<BillOfMaterial>("application/json")
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{productId:guid}/{revisionId:guid}", DeleteBillOfMaterial)
            .WithName("DeleteBillOfMaterial")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{productId:guid}/{revisionId:guid}/items", AddItemToBillOfMaterial)
            .WithName("AddItemToBillOfMaterial")
            .Accepts<BillOfMaterial.Record>("application/json")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{productId:guid}/{revisionId:guid}/items", UpdateItemInBillOfMaterial)
            .WithName("UpdateItemInBillOfMaterial")
            .Accepts<BillOfMaterial.Record>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{productId:guid}/{revisionId:guid}/items/{materialId:guid}", RemoveItemFromBillOfMaterial)
            .WithName("RemoveItemFromBillOfMaterial")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public static async Task<IResult> GetBillOfMaterialByProductIdAndRevisionId(Guid productId, Guid revisionId, CatalogDbContext db, CancellationToken cs)
    {
        var billOfMaterialItems = await db.BillOfMaterialItems
            .Where(b => b.RevisionId == revisionId)
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

    public static async Task<IResult> CreateBillOfMaterial(BillOfMaterial billOfMaterial, CatalogDbContext db, CancellationToken cs)
    {
        var billOfMaterialRevision = new BillOfMaterialRevision
        {
            ProductId = billOfMaterial.ProductId,
            Revision = 1
        };

        await db.BillOfMaterialRevisions.AddAsync(billOfMaterialRevision, cs);
        await db.SaveChangesAsync(cs);

        var billOfMaterialItems = billOfMaterial.Materials.Select(m => new BillOfMaterialItem
        {
            RevisionId = billOfMaterialRevision.Id,
            MaterialId = m.MaterialId,
            Quantity = m.Quantity
        }).ToList();



        await db.BillOfMaterialItems.AddRangeAsync(billOfMaterialItems, cs);
        await db.SaveChangesAsync(cs);

        return Results.Created($"/api/bill-of-material/{billOfMaterial.ProductId}", billOfMaterial);
    }

    public static async Task<IResult> ReplaceBillOfMaterial(BillOfMaterial billOfMaterial, CatalogDbContext db, CancellationToken cs)
    {
        var revisionId = await db.BillOfMaterialRevisions
            .Where(r => r.ProductId == billOfMaterial.ProductId)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cs);

        var existingItems = await db.BillOfMaterialItems
            .Where(b => b.RevisionId == revisionId)
            .ToListAsync(cs);

        if (existingItems.Count == 0)
        {
            return Results.NotFound();
        }

        db.BillOfMaterialItems.RemoveRange(existingItems);

        var newItems = billOfMaterial.Materials.Select(m => new BillOfMaterialItem
        {
            RevisionId = revisionId,
            MaterialId = m.MaterialId,
            Quantity = m.Quantity
        }).ToList();

        await db.BillOfMaterialItems.AddRangeAsync(newItems, cs);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> DeleteBillOfMaterial(Guid productId, Guid revisionId, CatalogDbContext db, CancellationToken cs)
    {
        var existingItems = await db.BillOfMaterialItems
        .Where(b => b.RevisionId == revisionId)
        .ToListAsync(cs);

        if (existingItems.Count == 0)
        {
            return Results.NotFound();
        }

        db.BillOfMaterialItems.RemoveRange(existingItems);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> AddItemToBillOfMaterial(Guid productId, Guid revisionId, BillOfMaterial.Record item, CatalogDbContext db, CancellationToken cs)
    {
        var existingItem = await db.BillOfMaterialItems
            .FirstOrDefaultAsync(b => b.RevisionId == revisionId && b.MaterialId == item.MaterialId, cs);

        if (existingItem != null)
        {
            return Results.Conflict($"Item with MaterialId {item.MaterialId} already exists for ProductId {productId}.");
        }

        var newItem = new BillOfMaterialItem
        {
            RevisionId = revisionId,
            MaterialId = item.MaterialId,
            Quantity = item.Quantity
        };

        await db.BillOfMaterialItems.AddAsync(newItem, cs);
        await db.SaveChangesAsync(cs);

        return Results.Created($"/api/bill-of-material/{productId}/{item.MaterialId}", newItem);
    }

    public static async Task<IResult> UpdateItemInBillOfMaterial(Guid productId, Guid revisionId, BillOfMaterial.Record item, CatalogDbContext db, CancellationToken cs)
    {
        var existingItem = await db.BillOfMaterialItems
            .FirstOrDefaultAsync(b => b.RevisionId == revisionId && b.MaterialId == item.MaterialId, cs);

        if (existingItem == null)
        {
            return Results.NotFound($"Item with MaterialId {item.MaterialId} not found for ProductId {productId}.");
        }

        existingItem.Quantity = item.Quantity;
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> RemoveItemFromBillOfMaterial(Guid productId, Guid revisionId, Guid materialId, CatalogDbContext db, CancellationToken cs)
    {
        var existingItem = await db.BillOfMaterialItems
            .FirstOrDefaultAsync(b => b.RevisionId == revisionId && b.MaterialId == materialId, cs);

        if (existingItem == null)
        {
            return Results.NotFound($"Item with MaterialId {materialId} not found for ProductId {productId}.");
        }

        db.BillOfMaterialItems.Remove(existingItem);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }
}
