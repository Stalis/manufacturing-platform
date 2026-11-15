using Catalog.Contracts;
using Microsoft.AspNetCore.Mvc;
using Domain = Manufacturing.Core.Models;

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

    internal static ProblemDetails Problem(string detail) => new() { Detail = detail };

    // Contract and storage enums share member names; values are independent (the database stores integers).
    internal static MeasurementUnit ToContract(Domain.MeasurementUnit unit) => Enum.Parse<MeasurementUnit>(unit.ToString());

    internal static Domain.MeasurementUnit ToDomain(MeasurementUnit unit) => Enum.Parse<Domain.MeasurementUnit>(unit.ToString());
}
