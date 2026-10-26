using Catalog.Api.Persistence;
using Catalog.Core.Models;
using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Endpoints;

public static class BillOfMaterialEndpoints
{
    public static IEndpointRouteBuilder MapBillOfMaterialEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/bill-of-material")
                       .WithTags("BillOfMaterial");

        group.MapGet("/{productId:guid}/revisions/{revision:long}", GetBillOfMaterialByProductIdAndRevision)
             .WithName("GetBillOfMaterialByProductIdAndRevision")
             .Produces<BillOfMaterial>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{productId:guid}/{revisionId:guid}", GetBillOfMaterialByProductIdAndRevisionId)
             .WithName("GetBillOfMaterialByProductIdAndRevisionId")
             .Produces<BillOfMaterial>()
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

    public static async Task<IResult> GetBillOfMaterialByProductIdAndRevisionId(
        Guid productId, Guid revisionId, CatalogDbContext db, CancellationToken cs)
    {
        var revision = await db.BillOfMaterialRevisions
                               .SingleOrDefaultAsync(r => r.ProductId == productId && r.Id == revisionId, cs);
        if (revision is null) return Results.NotFound();

        var billOfMaterialItems = await db.BillOfMaterialItems
                                          .Where(b => b.RevisionId == revisionId)
                                          .ToListAsync(cs);

        var res = new BillOfMaterial
        {
            ProductId = productId,
            Revision = revision.Revision,
            Materials = billOfMaterialItems.Select(b => new BillOfMaterial.Record(b.MaterialId, b.Quantity)).ToList(),
        };

        return Results.Ok(res);
    }

    public static async Task<IResult> GetBillOfMaterialByProductIdAndRevision(
        Guid productId, uint revision, CatalogDbContext db, CancellationToken cs)
    {
        var revisionId = await db.BillOfMaterialRevisions
                                 .Where(r => r.ProductId == productId && r.Revision == revision)
                                 .Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cs);
        return revisionId is null
            ? Results.NotFound()
            : await GetBillOfMaterialByProductIdAndRevisionId(productId, revisionId.Value, db, cs);
    }

    public static async Task<IResult> CreateBillOfMaterial(BillOfMaterial billOfMaterial, CatalogDbContext db,
                                                           CancellationToken cs)
    {
        if (billOfMaterial.Revision == 0 || billOfMaterial.Materials.Any(m => m.Quantity <= 0))
            return Results.BadRequest("Revision and quantities must be positive.");
        if (billOfMaterial.Materials.Select(m => m.MaterialId).Distinct().Count() != billOfMaterial.Materials.Count)
            return Results.Conflict("A material can occur only once in a revision.");

        var billOfMaterialRevision = new BillOfMaterialRevision
        {
            ProductId = billOfMaterial.ProductId,
            Revision = billOfMaterial.Revision,
        };

        await db.BillOfMaterialRevisions.AddAsync(billOfMaterialRevision, cs);

        var billOfMaterialItems = billOfMaterial.Materials.Select(m => new BillOfMaterialItem
        {
            RevisionId = billOfMaterialRevision.Id,
            MaterialId = m.MaterialId,
            Quantity = m.Quantity,
        }).ToList();


        await db.BillOfMaterialItems.AddRangeAsync(billOfMaterialItems, cs);
        await db.SaveChangesAsync(cs);

        return Results.Created($"/api/catalog/bill-of-material/{billOfMaterial.ProductId}/{billOfMaterialRevision.Id}",
                               billOfMaterial);
    }

    public static async Task<IResult> ReplaceBillOfMaterial(BillOfMaterial billOfMaterial, CatalogDbContext db,
                                                            CancellationToken cs)
    {
        if (billOfMaterial.Revision == 0 || billOfMaterial.Materials.Any(m => m.Quantity <= 0))
            return Results.BadRequest("Revision and quantities must be positive.");
        if (billOfMaterial.Materials.Select(m => m.MaterialId).Distinct().Count() != billOfMaterial.Materials.Count)
            return Results.Conflict("A material can occur only once in a revision.");

        var revisionId = await db.BillOfMaterialRevisions
                                 .Where(r => r.ProductId == billOfMaterial.ProductId &&
                                             r.Revision == billOfMaterial.Revision)
                                 .Select(r => (Guid?)r.Id)
                                 .SingleOrDefaultAsync(cs);
        if (revisionId is null) return Results.NotFound();

        var existingItems = await db.BillOfMaterialItems
                                    .Where(b => b.RevisionId == revisionId)
                                    .ToListAsync(cs);

        var desired = billOfMaterial.Materials.ToDictionary(m => m.MaterialId);
        foreach (var existing in existingItems)
            if (desired.Remove(existing.MaterialId, out var item)) existing.Quantity = item.Quantity;
            else db.BillOfMaterialItems.Remove(existing);
        db.BillOfMaterialItems.AddRange(desired.Values.Select(m => new BillOfMaterialItem
        {
            RevisionId = revisionId.Value,
            MaterialId = m.MaterialId,
            Quantity = m.Quantity,
        }));
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> DeleteBillOfMaterial(Guid productId, Guid revisionId, CatalogDbContext db,
                                                           CancellationToken cs)
    {
        var revision = await db.BillOfMaterialRevisions
                               .SingleOrDefaultAsync(r => r.ProductId == productId && r.Id == revisionId, cs);
        if (revision is null)
            return Results.NotFound();

        db.BillOfMaterialRevisions.Remove(revision);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> AddItemToBillOfMaterial(Guid productId, Guid revisionId,
                                                              BillOfMaterial.Record item, CatalogDbContext db,
                                                              CancellationToken cs)
    {
        if (!await db.BillOfMaterialRevisions.AnyAsync(r => r.ProductId == productId && r.Id == revisionId, cs))
            return Results.NotFound();
        if (item.Quantity <= 0) return Results.BadRequest("Quantity must be positive.");
        var existingItem = await db.BillOfMaterialItems
                                   .FirstOrDefaultAsync(
                                       b => b.RevisionId == revisionId && b.MaterialId == item.MaterialId, cs);

        if (existingItem != null)
            return Results.Conflict(
                $"Item with MaterialId {item.MaterialId} already exists for ProductId {productId}.");

        var newItem = new BillOfMaterialItem
        {
            RevisionId = revisionId,
            MaterialId = item.MaterialId,
            Quantity = item.Quantity,
        };

        await db.BillOfMaterialItems.AddAsync(newItem, cs);
        await db.SaveChangesAsync(cs);

        return Results.Created($"/api/catalog/bill-of-material/{productId}/{revisionId}", newItem);
    }

    public static async Task<IResult> UpdateItemInBillOfMaterial(Guid productId, Guid revisionId,
                                                                 BillOfMaterial.Record item, CatalogDbContext db,
                                                                 CancellationToken cs)
    {
        if (!await db.BillOfMaterialRevisions.AnyAsync(r => r.ProductId == productId && r.Id == revisionId, cs))
            return Results.NotFound();
        if (item.Quantity <= 0) return Results.BadRequest("Quantity must be positive.");
        var existingItem = await db.BillOfMaterialItems
                                   .FirstOrDefaultAsync(
                                       b => b.RevisionId == revisionId && b.MaterialId == item.MaterialId, cs);

        if (existingItem == null)
            return Results.NotFound($"Item with MaterialId {item.MaterialId} not found for ProductId {productId}.");

        existingItem.Quantity = item.Quantity;
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> RemoveItemFromBillOfMaterial(Guid productId, Guid revisionId, Guid materialId,
                                                                   CatalogDbContext db, CancellationToken cs)
    {
        if (!await db.BillOfMaterialRevisions.AnyAsync(r => r.ProductId == productId && r.Id == revisionId, cs))
            return Results.NotFound();
        var existingItem = await db.BillOfMaterialItems
                                   .FirstOrDefaultAsync(b => b.RevisionId == revisionId && b.MaterialId == materialId,
                                                        cs);

        if (existingItem == null)
            return Results.NotFound($"Item with MaterialId {materialId} not found for ProductId {productId}.");

        db.BillOfMaterialItems.Remove(existingItem);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }
}