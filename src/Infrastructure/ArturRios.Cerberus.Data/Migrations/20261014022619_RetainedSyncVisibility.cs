using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetainedSyncVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "terminal",
                schema: "cerberus",
                table: "sync_inventory",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "visible",
                schema: "cerberus",
                table: "sync_inventory",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "terminal",
                schema: "cerberus",
                table: "sync_inventory");

            migrationBuilder.DropColumn(
                name: "visible",
                schema: "cerberus",
                table: "sync_inventory");
        }
    }
}
