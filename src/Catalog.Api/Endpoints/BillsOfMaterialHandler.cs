using Catalog.Api.Persistence;
using Catalog.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Catalog.Api.CatalogApiEndpoints;
using Entities = Catalog.Core.Models;

namespace Catalog.Api.Endpoints;

public sealed class BillsOfMaterialHandler(CatalogDbContext db) : IBillsOfMaterialHandler
{
    public async Task<Results<Ok<BillOfMaterial>, NotFound<ProblemDetails>>> GetBillOfMaterialAsync(
        Guid productId, uint revision, CancellationToken cancellationToken)
    {
        var revisionId = await FindRevisionIdAsync(productId, revision, cancellationToken);
        return revisionId is null
            ? TypedResults.NotFound(Problem($"Product {productId} has no revision {revision}."))
            : await GetBillOfMaterialByRevisionIdAsync(productId, revisionId.Value, cancellationToken);
    }

    public async Task<Results<Ok<BillOfMaterial>, NotFound<ProblemDetails>>> GetBillOfMaterialByRevisionIdAsync(
        Guid productId, Guid revisionId, CancellationToken cancellationToken)
    {
        var revision = await db.BillOfMaterialRevisions
                               .SingleOrDefaultAsync(r => r.ProductId == productId && r.Id == revisionId,
                                                     cancellationToken);
        if (revision is null) return TypedResults.NotFound(MissingRevision(productId, revisionId));

        var items = await db.BillOfMaterialItems
                            .Where(b => b.RevisionId == revisionId)
                            .Select(b => new BillOfMaterialItem(b.MaterialId, b.Quantity))
                            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(new BillOfMaterial(productId, revision.Revision, items));
    }

    public async Task<Results<Created<BillOfMaterial>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>>>
        CreateBillOfMaterialAsync(BillOfMaterial billOfMaterial, CancellationToken cancellationToken)
    {
        if (Invalid(billOfMaterial) is { } invalid) return TypedResults.BadRequest(invalid);
        if (Duplicated(billOfMaterial) is { } duplicated) return TypedResults.Conflict(duplicated);

        var revision = new Entities.BillOfMaterialRevision
        {
            ProductId = billOfMaterial.ProductId,
            Revision = billOfMaterial.Revision,
        };

        db.BillOfMaterialRevisions.Add(revision);
        db.BillOfMaterialItems.AddRange(billOfMaterial.Materials.Select(m => new Entities.BillOfMaterialItem
        {
            RevisionId = revision.Id,
            MaterialId = m.MaterialId,
            Quantity = m.Quantity,
        }));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/catalog/bill-of-material/{billOfMaterial.ProductId}/{revision.Id}", billOfMaterial);
    }

    public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound<ProblemDetails>, Conflict<ProblemDetails>>>
        ReplaceBillOfMaterialAsync(BillOfMaterial billOfMaterial, CancellationToken cancellationToken)
    {
        if (Invalid(billOfMaterial) is { } invalid) return TypedResults.BadRequest(invalid);
        if (Duplicated(billOfMaterial) is { } duplicated) return TypedResults.Conflict(duplicated);

        var revisionId = await FindRevisionIdAsync(billOfMaterial.ProductId, billOfMaterial.Revision, cancellationToken);
        if (revisionId is null)
        {
            return TypedResults.NotFound(
                Problem($"Product {billOfMaterial.ProductId} has no revision {billOfMaterial.Revision}."));
        }

        var existingItems = await db.BillOfMaterialItems
                                    .Where(b => b.RevisionId == revisionId)
                                    .ToListAsync(cancellationToken);

        var desired = billOfMaterial.Materials.ToDictionary(m => m.MaterialId);
        foreach (var existing in existingItems)
            if (desired.Remove(existing.MaterialId, out var item)) existing.Quantity = item.Quantity;
            else db.BillOfMaterialItems.Remove(existing);
        db.BillOfMaterialItems.AddRange(desired.Values.Select(m => new Entities.BillOfMaterialItem
        {
            RevisionId = revisionId.Value,
            MaterialId = m.MaterialId,
            Quantity = m.Quantity,
        }));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    public async Task<Results<NoContent, NotFound<ProblemDetails>>> DeleteBillOfMaterialAsync(
        Guid productId, Guid revisionId, CancellationToken cancellationToken)
    {
        var revision = await db.BillOfMaterialRevisions
                               .SingleOrDefaultAsync(r => r.ProductId == productId && r.Id == revisionId,
                                                     cancellationToken);
        if (revision is null) return TypedResults.NotFound(MissingRevision(productId, revisionId));

        db.BillOfMaterialRevisions.Remove(revision);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    public async Task<Results<Created, BadRequest<ProblemDetails>, NotFound<ProblemDetails>, Conflict<ProblemDetails>>>
        AddBillOfMaterialItemAsync(Guid productId, Guid revisionId, BillOfMaterialItem item,
                                   CancellationToken cancellationToken)
    {
        if (!await RevisionExistsAsync(productId, revisionId, cancellationToken))
            return TypedResults.NotFound(MissingRevision(productId, revisionId));
        if (item.Quantity <= 0) return TypedResults.BadRequest(Problem("Quantity must be positive."));
        if (await FindItemAsync(revisionId, item.MaterialId, cancellationToken) is not null)
        {
            return TypedResults.Conflict(
                Problem($"Material {item.MaterialId} already occurs in revision {revisionId}."));
        }

        db.BillOfMaterialItems.Add(new Entities.BillOfMaterialItem
        {
            RevisionId = revisionId,
            MaterialId = item.MaterialId,
            Quantity = item.Quantity,
        });
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/catalog/bill-of-material/{productId}/{revisionId}");
    }

    public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound<ProblemDetails>>>
        UpdateBillOfMaterialItemAsync(Guid productId, Guid revisionId, BillOfMaterialItem item,
                                      CancellationToken cancellationToken)
    {
        if (!await RevisionExistsAsync(productId, revisionId, cancellationToken))
            return TypedResults.NotFound(MissingRevision(productId, revisionId));
        if (item.Quantity <= 0) return TypedResults.BadRequest(Problem("Quantity must be positive."));
        var existing = await FindItemAsync(revisionId, item.MaterialId, cancellationToken);
        if (existing is null) return TypedResults.NotFound(MissingItem(revisionId, item.MaterialId));

        existing.Quantity = item.Quantity;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    public async Task<Results<NoContent, NotFound<ProblemDetails>>> RemoveBillOfMaterialItemAsync(
        Guid productId, Guid revisionId, Guid materialId, CancellationToken cancellationToken)
    {
        if (!await RevisionExistsAsync(productId, revisionId, cancellationToken))
            return TypedResults.NotFound(MissingRevision(productId, revisionId));
        var existing = await FindItemAsync(revisionId, materialId, cancellationToken);
        if (existing is null) return TypedResults.NotFound(MissingItem(revisionId, materialId));

        db.BillOfMaterialItems.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static ProblemDetails? Invalid(BillOfMaterial billOfMaterial) =>
        billOfMaterial.Revision == 0 || billOfMaterial.Materials.Any(m => m.Quantity <= 0)
            ? Problem("Revision and quantities must be positive.")
            : null;

    private static ProblemDetails? Duplicated(BillOfMaterial billOfMaterial) =>
        billOfMaterial.Materials.Select(m => m.MaterialId).Distinct().Count() != billOfMaterial.Materials.Length
            ? Problem("A material can occur only once in a revision.")
            : null;

    private Task<Guid?> FindRevisionIdAsync(Guid productId, uint revision, CancellationToken cancellationToken) =>
        db.BillOfMaterialRevisions.Where(r => r.ProductId == productId && r.Revision == revision)
                                  .Select(r => (Guid?)r.Id)
                                  .SingleOrDefaultAsync(cancellationToken);

    private Task<bool> RevisionExistsAsync(Guid productId, Guid revisionId, CancellationToken cancellationToken) =>
        db.BillOfMaterialRevisions.AnyAsync(r => r.ProductId == productId && r.Id == revisionId, cancellationToken);

    private Task<Entities.BillOfMaterialItem?> FindItemAsync(
        Guid revisionId, Guid materialId, CancellationToken cancellationToken) =>
        db.BillOfMaterialItems.FirstOrDefaultAsync(b => b.RevisionId == revisionId && b.MaterialId == materialId,
                                                   cancellationToken);

    private static ProblemDetails MissingRevision(Guid productId, Guid revisionId) =>
        Problem($"Product {productId} has no revision {revisionId}.");

    private static ProblemDetails MissingItem(Guid revisionId, Guid materialId) =>
        Problem($"Material {materialId} does not occur in revision {revisionId}.");
}
