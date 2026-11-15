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

app.MapCatalogEndpoints();

app.Run();
