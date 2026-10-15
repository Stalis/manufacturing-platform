namespace Catalog.Api.Endpoints;

using Microsoft.AspNetCore.Routing;


public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/catalog")
            .AddEndpointFilter<CatalogConflictFilter>()
            .WithTags("Catalog");

        group.MapMaterialsEndpoints();
        group.MapProductsEndpoints();
        group.MapBillOfMaterialEndpoints();

        return app;
    }
}
