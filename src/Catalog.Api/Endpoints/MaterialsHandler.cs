using Catalog.Api.Persistence;
using Catalog.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Catalog.Api.Endpoints.CatalogEndpoints;
using Domain = Manufacturing.Core.Models;

namespace Catalog.Api.Endpoints;

public sealed class MaterialsHandler(CatalogDbContext db) : IMaterialsHandler
{
    public async Task<Ok<Material[]>> ListMaterialsAsync(CancellationToken cancellationToken)
    {
        var materials = await db.Materials.AsNoTracking().ToListAsync(cancellationToken);
        return TypedResults.Ok(materials.Select(ToContract).ToArray());
    }

    public async Task<Results<Ok<Material>, NotFound<ProblemDetails>>> GetMaterialAsync(
        Guid id, CancellationToken cancellationToken)
    {
        return await db.Materials.FindAsync([id], cancellationToken) is { } material
            ? TypedResults.Ok(ToContract(material))
            : TypedResults.NotFound(Missing(id));
    }

    public async Task<Results<Created<Material>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>>>
        CreateMaterialAsync(MaterialInput input, CancellationToken cancellationToken)
    {
        var material = new Domain.Material
        {
            SKU = input.Sku,
            Name = input.Name,
            MeasurementUnit = ToDomain(input.MeasurementUnit),
        };

        db.Materials.Add(material);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/catalog/materials/{material.Id}", ToContract(material));
    }

    public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound<ProblemDetails>, Conflict<ProblemDetails>>>
        ReplaceMaterialAsync(Guid id, MaterialInput input, CancellationToken cancellationToken)
    {
        var material = await db.Materials.FindAsync([id], cancellationToken);
        if (material is null) return TypedResults.NotFound(Missing(id));

        material.SKU = input.Sku;
        material.Name = input.Name;
        material.MeasurementUnit = ToDomain(input.MeasurementUnit);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    public async Task<Results<NoContent, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> DeleteMaterialAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var material = await db.Materials.FindAsync([id], cancellationToken);
        if (material is null) return TypedResults.NotFound(Missing(id));

        db.Materials.Remove(material);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static Material ToContract(Domain.Material material) =>
        new(material.Id, material.SKU, material.Name, CatalogEndpoints.ToContract(material.MeasurementUnit));

    private static ProblemDetails Missing(Guid id) => Problem($"Material {id} was not found.");
}
