using Catalog.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Api.Persistence.Configurations;

class BillOfMaterialItemConfiguration : IEntityTypeConfiguration<BillOfMaterialItem>
{
    public void Configure(EntityTypeBuilder<BillOfMaterialItem> builder)
    {
        builder.ToTable("BillOfMaterialItems");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Material)
            .WithMany()
            .HasForeignKey(x => x.MaterialId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Quantity)
            .HasColumnName("quantity")
            .IsRequired();
    }
}