using Catalog.Api.Persistence;
using Catalog.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Catalog.Api.Endpoints.CatalogEndpoints;
using Domain = Manufacturing.Core.Models;

namespace Catalog.Api.Endpoints;

public sealed class ProductsHandler(CatalogDbContext db) : IProductsHandler
{
    public async Task<Ok<Product[]>> ListProductsAsync(CancellationToken cancellationToken)
    {
        var products = await db.Products.AsNoTracking().ToListAsync(cancellationToken);
        return TypedResults.Ok(products.Select(ToContract).ToArray());
    }

    public async Task<Results<Ok<Product>, NotFound<ProblemDetails>>> GetProductAsync(
        Guid id, CancellationToken cancellationToken)
    {
        return await db.Products.FindAsync([id], cancellationToken) is { } product
            ? TypedResults.Ok(ToContract(product))
            : TypedResults.NotFound(Missing(id));
    }

    public async Task<Results<Created<Product>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>>>
        CreateProductAsync(ProductInput input, CancellationToken cancellationToken)
    {
        var product = new Domain.Product
        {
            SKU = input.Sku,
            Name = input.Name,
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/catalog/products/{product.Id}", ToContract(product));
    }

    public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound<ProblemDetails>, Conflict<ProblemDetails>>>
        ReplaceProductAsync(Guid id, ProductInput input, CancellationToken cancellationToken)
    {
        var product = await db.Products.FindAsync([id], cancellationToken);
        if (product is null) return TypedResults.NotFound(Missing(id));

        product.SKU = input.Sku;
        product.Name = input.Name;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    public async Task<Results<NoContent, NotFound<ProblemDetails>>> DeleteProductAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var product = await db.Products.FindAsync([id], cancellationToken);
        if (product is null) return TypedResults.NotFound(Missing(id));

        db.Products.Remove(product);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static Product ToContract(Domain.Product product) => new(product.Id, product.SKU, product.Name);

    private static ProblemDetails Missing(Guid id) => Problem($"Product {id} was not found.");
}
