using Catalog.Core.Models;
using Manufacturing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Api.Persistence.Configurations;

internal class BillOfMaterialRevisionConfiguration : IEntityTypeConfiguration<BillOfMaterialRevision>
{
    public void Configure(EntityTypeBuilder<BillOfMaterialRevision> builder)
    {
        builder.ToTable("BillOfMaterialRevisions");

        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.ProductId, x.Revision }).IsUnique();
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Id)
               .HasColumnName("id")
               .ValueGeneratedNever();
    }
}