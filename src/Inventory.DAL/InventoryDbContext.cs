using Inventory.DAL.Configurations;
using Inventory.DAL.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.DAL;

public class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options
) : DbContext(options)
{
    public DbSet<MaterialStock> MaterialStocks => Set<MaterialStock>();
    public DbSet<ReservationItem> ReservationItems => Set<ReservationItem>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ApplyConfiguration(new MaterialStockConfiguration());
        modelBuilder.ApplyConfiguration(new ReservationItemConfiguration());
        modelBuilder.ApplyConfiguration(new ReservationConfiguration());
    }
}