using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Catalog.Api.CatalogApiEndpoints;

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
                                                  ConstraintName: "IX_Materials_SKU" or "IX_Products_SKU"
                                                  or "PK_BillOfMaterialItems"
                                                  or "IX_BillOfMaterialRevisions_ProductId_Revision"
                                                  or "IX_BillOfMaterialItems_RevisionId_MaterialId",
                                              })
        {
            return TypedResults.Conflict(Problem(
                "A record with this SKU, product/revision, or revision/material pair already exists."));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
                                              {
                                                  SqlState: PostgresErrorCodes.ForeignKeyViolation,
                                                  ConstraintName: var constraint,
                                              })
        {
            if (constraint == "FK_BillOfMaterialItems_Materials_MaterialId"
                && HttpMethods.IsDelete(context.HttpContext.Request.Method))
            {
                return TypedResults.Conflict(Problem("Cannot delete a material that is referenced by a bill of material."));
            }

            return TypedResults.BadRequest(Problem("Referenced product, revision, or material does not exist."));
        }
    }
}
