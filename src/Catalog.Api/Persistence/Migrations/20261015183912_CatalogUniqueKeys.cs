using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogUniqueKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Report duplicate keys before changing any indexes; existing records require explicit reconciliation.
            migrationBuilder.Sql("""
                DO $$
                DECLARE duplicates text;
                BEGIN
                    SELECT string_agg(label, E'\n') INTO duplicates FROM (
                        SELECT 'Material SKU: ' || "SKU" AS label FROM "Materials" GROUP BY "SKU" HAVING count(*) > 1
                        UNION ALL
                        SELECT 'Product SKU: ' || "SKU" FROM "Products" GROUP BY "SKU" HAVING count(*) > 1
                        UNION ALL
                        SELECT 'BOM pair: ' || "ProductId"::text || '/' || "MaterialId"::text
                        FROM "BillOfMaterialItems" GROUP BY "ProductId", "MaterialId" HAVING count(*) > 1
                    ) keys;
                    IF duplicates IS NOT NULL THEN
                        RAISE EXCEPTION 'Resolve duplicate catalog keys before migrating: %', duplicates;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_ProductId",
                table: "BillOfMaterialItems");

            migrationBuilder.CreateIndex(
                name: "IX_Products_SKU",
                table: "Products",
                column: "SKU",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Materials_SKU",
                table: "Materials",
                column: "SKU",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_ProductId_MaterialId",
                table: "BillOfMaterialItems",
                columns: new[] { "ProductId", "MaterialId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_SKU",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Materials_SKU",
                table: "Materials");

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_ProductId_MaterialId",
                table: "BillOfMaterialItems");

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_ProductId",
                table: "BillOfMaterialItems",
                column: "ProductId");
        }
    }
}
