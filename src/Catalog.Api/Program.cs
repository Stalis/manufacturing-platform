using Catalog.Api.Endpoints;
using Catalog.Api.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Catalog")
    )
);

var app = builder.Build();

if (!EF.IsDesignTime &&
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
}

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapCatalogEndpoints();

app.Run();
