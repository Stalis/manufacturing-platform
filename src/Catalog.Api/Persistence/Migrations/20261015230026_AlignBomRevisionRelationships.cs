using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignBomRevisionRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "BillOfMaterialItems" items
                        JOIN "BillOfMaterialRevisions" revisions ON revisions.id = items."RevisionId"
                        WHERE items."ProductId" <> revisions."ProductId"
                    ) THEN
                        RAISE EXCEPTION 'BOM item and revision product IDs disagree; reconcile before migrating';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_BillOfMaterialItems_Products_ProductId",
                table: "BillOfMaterialItems");

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_ProductId",
                table: "BillOfMaterialItems");

            migrationBuilder.DropIndex(
                name: "IX_BillOfMaterialItems_RevisionId_MaterialId",
                table: "BillOfMaterialItems");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "BillOfMaterialItems");

            migrationBuilder.AddForeignKey(
                name: "FK_BillOfMaterialRevisions_Products_ProductId",
                table: "BillOfMaterialRevisions",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BillOfMaterialRevisions_Products_ProductId",
                table: "BillOfMaterialRevisions");

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "BillOfMaterialItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("""
                UPDATE "BillOfMaterialItems" items SET "ProductId" = revisions."ProductId"
                FROM "BillOfMaterialRevisions" revisions WHERE items."RevisionId" = revisions.id;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_ProductId",
                table: "BillOfMaterialItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BillOfMaterialItems_RevisionId_MaterialId",
                table: "BillOfMaterialItems",
                columns: new[] { "RevisionId", "MaterialId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BillOfMaterialItems_Products_ProductId",
                table: "BillOfMaterialItems",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
