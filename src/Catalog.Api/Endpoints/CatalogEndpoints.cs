namespace Catalog.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IServiceCollection AddCatalogEndpoints(this IServiceCollection services)
    {
        services.AddCatalogApi();
        services.AddScoped<IMaterialsHandler, MaterialsHandler>();
        services.AddScoped<IProductsHandler, ProductsHandler>();
        services.AddScoped<IBillsOfMaterialHandler, BillsOfMaterialHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapCatalogApi().AddEndpointFilter<CatalogConflictFilter>();
        return app;
    }
}
