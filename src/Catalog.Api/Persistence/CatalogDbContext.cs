using Catalog.Core.Models;
using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Persistence;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<BillOfMaterialItem> BillOfMaterialItems => Set<BillOfMaterialItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration<Product>(new Configurations.ProductConfiguration());
        modelBuilder.ApplyConfiguration<Material>(new Configurations.MaterialConfiguration());
        modelBuilder.ApplyConfiguration<BillOfMaterialItem>(new Configurations.BillOfMaterialItemConfiguration());
    }
}