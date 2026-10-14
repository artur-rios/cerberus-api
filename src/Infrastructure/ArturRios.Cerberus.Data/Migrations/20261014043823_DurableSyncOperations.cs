using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableSyncOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_operation",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    resource_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: true),
                    server_sequence = table.Column<long>(type: "bigint", nullable: true),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_operation", x => x.id);
                    table.CheckConstraint("ck_sync_operation_metadata", "operation_id<>'00000000-0000-0000-0000-000000000000'::uuid\nAND resource_id<>'00000000-0000-0000-0000-000000000000'::uuid\nAND resource_kind IN('profile','record','folder','collection') AND action IN('create','update')\nAND octet_length(request_fingerprint)=32\nAND ((status IN('applied','superseded') AND revision IS NOT NULL AND server_sequence IS NOT NULL\n  AND revision BETWEEN 1 AND 9007199254740991 AND server_sequence BETWEEN 1 AND 9007199254740991 AND edited_at IS NOT NULL)\n  OR (status IN('denied','permanently-deleted') AND revision IS NULL AND server_sequence IS NULL AND edited_at IS NULL))");
                    table.ForeignKey(
                        name: "fk_sync_operation_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sync_operation_account_id_operation_id",
                schema: "cerberus",
                table: "sync_operation",
                columns: new[] { "account_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_operation_account_id_scope_profile_id_resource_kind_re",
                schema: "cerberus",
                table: "sync_operation",
                columns: new[] { "account_id", "scope_profile_id", "resource_kind", "resource_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_operation",
                schema: "cerberus");
        }
    }
}
