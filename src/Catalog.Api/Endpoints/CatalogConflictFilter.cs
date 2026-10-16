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
            ConstraintName: "IX_Materials_SKU" or "IX_Products_SKU" or "PK_BillOfMaterialItems"
                or "IX_BillOfMaterialRevisions_ProductId_Revision" or "IX_BillOfMaterialItems_RevisionId_MaterialId"
        })
        {
            return Results.Conflict("A record with this SKU, product/revision, or revision/material pair already exists.");
        }
    }
}
