using Inventory.DAL.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.DAL.Configurations;

public class MaterialStockConfiguration : IEntityTypeConfiguration<MaterialStock>
{
    public void Configure(EntityTypeBuilder<MaterialStock> builder)
    { 
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
               .ValueGeneratedNever();
        
        builder.Property(x => x.Quantity)
               .IsRequired();
    }
}