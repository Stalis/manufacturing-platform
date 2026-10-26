using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Api.Persistence.Configurations;

internal class MaterialConfiguration : IEntityTypeConfiguration<Material>
{
    public void Configure(EntityTypeBuilder<Material> builder)
    {
        builder.ToTable("Materials");

        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.SKU).IsUnique();

        builder.Property(x => x.Id)
               .HasColumnName("id")
               .ValueGeneratedNever();

        builder.Property(x => x.Name)
               .HasColumnName("name")
               .HasMaxLength(100)
               .IsRequired();

        builder.Property(x => x.MeasurementUnit)
               .HasColumnName("measurement_unit")
               .IsRequired();
    }
}