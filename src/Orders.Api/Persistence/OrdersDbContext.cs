using Microsoft.EntityFrameworkCore;
using Orders.Core.Models;

namespace Orders.Api.Persistence;

public class OrdersDbContext(
    DbContextOptions<OrdersDbContext> options
) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<BomSnapshot> BomSnapshots => Set<BomSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(OrdersDbContext).Assembly
        );
    }
}