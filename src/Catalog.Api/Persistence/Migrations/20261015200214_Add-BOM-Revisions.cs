using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBOMRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_BillOfMaterialItems",
                table: "BillOfMaterialItems");

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_ProductId_MaterialId",
                table: "BillOfMaterialItems");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "BillOfMaterialItems",
                newName: "RevisionId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BillOfMaterialItems",
                table: "BillOfMaterialItems",
                columns: new[] { "RevisionId", "MaterialId" });

            migrationBuilder.CreateTable(
                name: "BillOfMaterialRevisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillOfMaterialRevisions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_ProductId",
                table: "BillOfMaterialItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_RevisionId_MaterialId",
                table: "BillOfMaterialItems",
                columns: new[] { "RevisionId", "MaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialRevisions_ProductId_Revision",
                table: "BillOfMaterialRevisions",
                columns: new[] { "ProductId", "Revision" },
                unique: true);

            // Older items each had an independent ID; group them into one initial revision per product.
            migrationBuilder.Sql("""
                INSERT INTO "BillOfMaterialRevisions" (id, "ProductId", "Revision")
                SELECT gen_random_uuid(), "ProductId", 1 FROM "BillOfMaterialItems" GROUP BY "ProductId";
                UPDATE "BillOfMaterialItems" items
                SET "RevisionId" = revisions.id
                FROM "BillOfMaterialRevisions" revisions
                WHERE items."ProductId" = revisions."ProductId" AND revisions."Revision" = 1;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_BillOfMaterialItems_BillOfMaterialRevisions_RevisionId",
                table: "BillOfMaterialItems",
                column: "RevisionId",
                principalTable: "BillOfMaterialRevisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "BillOfMaterialRevisions" GROUP BY "ProductId" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Cannot downgrade multiple BOM revisions to an unversioned BOM';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_BillOfMaterialItems_BillOfMaterialRevisions_RevisionId",
                table: "BillOfMaterialItems");

            migrationBuilder.DropTable(
                name: "BillOfMaterialRevisions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BillOfMaterialItems",
                table: "BillOfMaterialItems");

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_ProductId",
                table: "BillOfMaterialItems");

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_RevisionId_MaterialId",
                table: "BillOfMaterialItems");

            migrationBuilder.Sql("""UPDATE "BillOfMaterialItems" SET "RevisionId" = gen_random_uuid();""");

            migrationBuilder.RenameColumn(
                name: "RevisionId",
                table: "BillOfMaterialItems",
                newName: "id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BillOfMaterialItems",
                table: "BillOfMaterialItems",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_ProductId_MaterialId",
                table: "BillOfMaterialItems",
                columns: new[] { "ProductId", "MaterialId" },
                unique: true);
        }
    }
}
