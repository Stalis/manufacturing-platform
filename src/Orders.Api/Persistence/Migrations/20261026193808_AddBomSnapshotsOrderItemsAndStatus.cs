using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBomSnapshotsOrderItemsAndStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "error_message",
                table: "Orders",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "items",
                table: "Orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "Orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "BomSnapshots",
                columns: table => new
                {
                    hash = table.Column<string>(type: "varchar", maxLength: 128, nullable: false),
                    json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomSnapshots", x => x.hash);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BomSnapshots");

            migrationBuilder.DropColumn(
                name: "error_message",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "items",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "status",
                table: "Orders");
        }
    }
}
