using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Core.Models;

namespace Orders.Api.Persistence.Configurations;

internal class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
               .HasColumnName("id")
               .ValueGeneratedNever();

        builder.Property(x => x.Status)
               .HasColumnName("status")
               .HasConversion<string>()
               .HasMaxLength(32)
               .IsRequired();

        builder.Property(x => x.ErrorMessage)
               .HasColumnName("error_message")
               .HasMaxLength(512);

        builder.Ignore(x => x.TotalTasksCount);
        builder.Ignore(x => x.CompletedTasksCount);
        builder.Ignore(x => x.InProgressTasksCount);
        builder.Ignore(x => x.ProgressPercentage);

        builder.OwnsMany(x => x.Items, b =>
        {
            b.ToJson("items");
            b.Ignore(i => i.CompletedQuantity);
            b.OwnsMany(i => i.Tasks);
        });
    }
}
