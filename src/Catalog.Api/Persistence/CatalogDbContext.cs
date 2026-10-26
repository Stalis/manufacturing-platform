using Catalog.Api.Persistence.Configurations;
using Catalog.Core.Models;
using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Api.Persistence;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<BillOfMaterialItem> BillOfMaterialItems => Set<BillOfMaterialItem>();
    public DbSet<BillOfMaterialRevision> BillOfMaterialRevisions => Set<BillOfMaterialRevision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new ProductConfiguration());
        modelBuilder.ApplyConfiguration(new MaterialConfiguration());
        modelBuilder.ApplyConfiguration(new BillOfMaterialItemConfiguration());
        modelBuilder.ApplyConfiguration(new BillOfMaterialRevisionConfiguration());
    }
}