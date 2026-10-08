using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileTrash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trash_operation",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    root_resource_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    root_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    purge_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trash_operation", x => x.id);
                    table.ForeignKey(
                        name: "fk_trash_operation_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trash_entry",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    operation_id = table.Column<long>(type: "bigint", nullable: false),
                    resource_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    association_snapshot = table.Column<byte[]>(type: "bytea", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trash_entry", x => x.id);
                    table.ForeignKey(
                        name: "fk_trash_entry_trash_operation_operation_id",
                        column: x => x.operation_id,
                        principalSchema: "cerberus",
                        principalTable: "trash_operation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_trash_entry_operation_id",
                schema: "cerberus",
                table: "trash_entry",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_trash_entry_resource_kind_resource_id",
                schema: "cerberus",
                table: "trash_entry",
                columns: new[] { "resource_kind", "resource_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trash_operation_account_id_purge_at",
                schema: "cerberus",
                table: "trash_operation",
                columns: new[] { "account_id", "purge_at" });

            migrationBuilder.CreateIndex(
                name: "ix_trash_operation_public_id",
                schema: "cerberus",
                table: "trash_operation",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trash_entry",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "trash_operation",
                schema: "cerberus");
        }
    }
}
