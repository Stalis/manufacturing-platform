using Catalog.Api.Persistence;
using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Endpoints;

public static class MaterialsEndpoints
{
    public static IEndpointRouteBuilder MapMaterialsEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var materialGroup = app.MapGroup("/materials")
                               .WithTags("Materials");

        materialGroup.MapGet("/{id:guid}", GetMaterialById)
                     .WithName("GetMaterialById")
                     .Produces<Material>()
                     .Produces(StatusCodes.Status404NotFound);

        materialGroup.MapGet("/", GetAllMaterials)
                     .WithName("GetAllMaterials")
                     .Produces<List<Material>>();

        materialGroup.MapPost("/", CreateMaterial)
                     .WithName("CreateMaterial")
                     .Accepts<CreateMaterialRequest>("application/json")
                     .Produces<Material>(StatusCodes.Status201Created)
                     .Produces(StatusCodes.Status409Conflict)
                     .Produces(StatusCodes.Status400BadRequest);

        materialGroup.MapPut("/", UpdateMaterial)
                     .WithName("UpdateMaterial")
                     .Accepts<UpdateMaterialRequest>("application/json")
                     .Produces(StatusCodes.Status409Conflict)
                     .Produces(StatusCodes.Status204NoContent)
                     .Produces(StatusCodes.Status404NotFound);

        materialGroup.MapDelete("/{id:guid}", DeleteMaterial)
                     .WithName("DeleteMaterial")
                     .Produces(StatusCodes.Status204NoContent)
                     .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public static async Task<IResult> GetMaterialById(Guid Id, CatalogDbContext db, CancellationToken cs)
    {
        var material = await db.Materials.FindAsync([Id], cs);

        return material is not null ? Results.Ok(material) : Results.NotFound();
    }

    public static async Task<IResult> GetAllMaterials(CatalogDbContext db, CancellationToken cs)
    {
        var materials = await db.Materials.ToListAsync(cs);

        return Results.Ok(materials);
    }

    public static async Task<IResult> CreateMaterial(CreateMaterialRequest request, CatalogDbContext db,
                                                     CancellationToken cs)
    {
        var material = new Material
        {
            Name = request.Name,
            SKU = request.SKU,
            MeasurementUnit = request.MeasurementUnit,
        };

        db.Materials.Add(material);
        await db.SaveChangesAsync(cs);

        return Results.Created($"/api/catalog/materials/{material.Id}", material);
    }

    public static async Task<IResult> UpdateMaterial(UpdateMaterialRequest request, CatalogDbContext db,
                                                     CancellationToken cs)
    {
        if (request.Name is null && request.SKU is null && request.MeasurementUnit is null)
            return Results.BadRequest("At least one field must be provided for update.");

        var existingMaterial = await db.Materials.FindAsync([request.Id], cs);

        if (existingMaterial is null) return Results.NotFound();


        existingMaterial.Name = request.Name ?? existingMaterial.Name;
        existingMaterial.SKU = request.SKU ?? existingMaterial.SKU;
        existingMaterial.MeasurementUnit = request.MeasurementUnit ?? existingMaterial.MeasurementUnit;

        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> DeleteMaterial(Guid Id, CatalogDbContext db, CancellationToken cs)
    {
        var material = await db.Materials.FindAsync([Id], cs);

        if (material is null) return Results.NotFound();

        db.Materials.Remove(material);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public record CreateMaterialRequest(string Name, string SKU, MeasurementUnit MeasurementUnit);

    public record UpdateMaterialRequest(Guid Id, string? Name, string? SKU, MeasurementUnit? MeasurementUnit);
}