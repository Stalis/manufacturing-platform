using Inventory.DAL.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.DAL.Configurations;

public class ReservationItemConfiguration : IEntityTypeConfiguration<ReservationItem>
{
    public void Configure(EntityTypeBuilder<ReservationItem> builder)
    {
        builder.HasKey(x => new { x.ReservationId, x.MaterialId });
        builder.Property(x => x.ConsumedQuantity) .IsRequired();
        builder.Property(x => x.Quantity).IsRequired();
        
        builder.HasOne(x => x.Reservation)
               .WithMany(x => x.Items)
               .HasForeignKey(x => x.ReservationId);
    }
}