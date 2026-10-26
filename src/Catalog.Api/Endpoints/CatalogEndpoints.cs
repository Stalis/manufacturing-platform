namespace Catalog.Api.Endpoints;

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