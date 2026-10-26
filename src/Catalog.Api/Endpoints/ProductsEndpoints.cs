using Catalog.Api.Persistence;
using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Endpoints;

public static class ProductsEndpoints
{
    public static IEndpointRouteBuilder MapProductsEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/products")
                       .WithTags("Products");

        group.MapGet("/", GetAllProducts)
             .WithName("GetAllProducts")
             .Produces<List<Product>>();

        group.MapGet("/{id:guid}", GetProductById)
             .WithName("GetProductById")
             .Produces<Product>()
             .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateProduct)
             .WithName("CreateProduct")
             .Accepts<CreateProductRequest>("application/json")
             .Produces<Product>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", UpdateProduct)
             .WithName("UpdateProduct")
             .Accepts<UpdateProductRequest>("application/json")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound)
             .Produces(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteProduct)
             .WithName("DeleteProduct")
             .Produces(StatusCodes.Status204NoContent)
             .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public static async Task<IResult> GetAllProducts(CatalogDbContext db, CancellationToken cs)
    {
        return Results.Ok(await db.Products.AsNoTracking().ToListAsync(cs));
    }

    public static async Task<IResult> GetProductById(Guid Id, CatalogDbContext db, CancellationToken cs)
    {
        var product = await db.Products.FindAsync([Id], cs);

        return product is not null ? Results.Ok(product) : Results.NotFound();
    }

    public static async Task<IResult> CreateProduct(CreateProductRequest request, CatalogDbContext db,
                                                    CancellationToken cs)
    {
        var product = new Product
        {
            SKU = request.SKU,
            Name = request.Name,
        };

        await db.Products.AddAsync(product, cs);
        await db.SaveChangesAsync(cs);

        return Results.Created($"/api/catalog/products/{product.Id}", product);
    }

    public static async Task<IResult> UpdateProduct(Guid id, UpdateProductRequest request, CatalogDbContext db,
                                                    CancellationToken cs)
    {
        if (request.Id != id) return Results.BadRequest("Route and body product IDs must match.");

        if (request.SKU is null && request.Name is null)
            return Results.BadRequest("At least one field (SKU or Name) must be provided for update.");

        var existingProduct = await db.Products.FindAsync([request.Id], cs);

        if (existingProduct is null) return Results.NotFound();

        existingProduct.SKU = request.SKU ?? existingProduct.SKU;
        existingProduct.Name = request.Name ?? existingProduct.Name;

        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public static async Task<IResult> DeleteProduct(Guid id, CatalogDbContext db, CancellationToken cs)
    {
        var existingProduct = await db.Products.FindAsync([id], cs);

        if (existingProduct is null) return Results.NotFound();

        db.Products.Remove(existingProduct);
        await db.SaveChangesAsync(cs);

        return Results.NoContent();
    }

    public record CreateProductRequest(string SKU, string Name);

    public record UpdateProductRequest(Guid Id, string? SKU, string? Name);
}