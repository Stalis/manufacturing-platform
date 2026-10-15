
using Catalog.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Api.Persistence.Configurations;

class BillOfMaterialRevisionConfiguration : IEntityTypeConfiguration<BillOfMaterialRevision>
{
    public void Configure(EntityTypeBuilder<BillOfMaterialRevision> builder)
    {
        builder.ToTable("BillOfMaterialRevisions");

        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.ProductId, x.Revision }).IsUnique();

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
    }
}
