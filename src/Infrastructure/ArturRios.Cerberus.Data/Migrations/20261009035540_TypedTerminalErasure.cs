using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class TypedTerminalErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_terminal_erasure_resource_id",
                schema: "cerberus",
                table: "terminal_erasure");

            migrationBuilder.CreateIndex(
                name: "ix_terminal_erasure_resource_kind_resource_id",
                schema: "cerberus",
                table: "terminal_erasure",
                columns: new[] { "resource_kind", "resource_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_terminal_erasure_kind",
                schema: "cerberus",
                table: "terminal_erasure",
                sql: "resource_kind IN ('account','profile','record','folder','collection','grant')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_terminal_erasure_resource_kind_resource_id",
                schema: "cerberus",
                table: "terminal_erasure");

            migrationBuilder.DropCheckConstraint(
                name: "ck_terminal_erasure_kind",
                schema: "cerberus",
                table: "terminal_erasure");

            migrationBuilder.CreateIndex(
                name: "ix_terminal_erasure_resource_id",
                schema: "cerberus",
                table: "terminal_erasure",
                column: "resource_id",
                unique: true);
        }
    }
}
