using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Catalog.Api.Endpoints;

public sealed class CatalogConflictFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Materials_SKU" or "IX_Products_SKU" or "IX_BillOfMaterialItems_ProductId_MaterialId"
        })
        {
            return Results.Conflict("A record with this SKU or product/material pair already exists.");
        }
    }
}
