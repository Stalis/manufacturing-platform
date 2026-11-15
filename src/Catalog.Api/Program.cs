using Catalog.Api.Endpoints;
using Catalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Catalog")
    )
);
builder.Services.AddCatalogEndpoints();

var app = builder.Build();

if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Emitted from contracts/catalog.tsp by tools/Contracts.Generator; not committed.
    var openApi = Path.Combine(app.Environment.ContentRootPath, "openapi.json");
    app.MapGet("/openapi/v1.json", () => File.Exists(openApi)
        ? Results.File(openApi, "application/json")
        : Results.Problem("Run `npm --prefix tools/Contracts.Generator run generate`.", statusCode: 404));
}

app.MapCatalogEndpoints();

app.Run();
