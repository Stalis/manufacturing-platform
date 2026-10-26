using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders.Core.Models;

namespace Orders.Api.Persistence.Configurations;

internal class BomSnapshotConfiguration : IEntityTypeConfiguration<BomSnapshot>
{
    public void Configure(EntityTypeBuilder<BomSnapshot> builder)
    {
        builder.ToTable("BomSnapshots");

        builder.HasKey(x => x.Hash);
        builder.Ignore(x => x.BillOfMaterial);

        builder.Property(x => x.Json)
               .HasColumnName("json")
               .HasColumnType("jsonb")
               .IsRequired();

        builder.Property(x => x.Hash)
               .HasColumnName("hash")
               .HasColumnType("varchar")
               .HasMaxLength(128)
               .IsRequired();
    }
}