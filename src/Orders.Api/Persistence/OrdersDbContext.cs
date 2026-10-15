using Microsoft.EntityFrameworkCore;
using Orders.Core;

namespace Orders.Api.Persistence;

public class OrdersDbContext(
    DbContextOptions<OrdersDbContext> options
) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(OrdersDbContext).Assembly
        );
    }
}
