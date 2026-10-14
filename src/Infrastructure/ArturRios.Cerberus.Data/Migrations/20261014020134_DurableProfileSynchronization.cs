using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableProfileSynchronization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_stream",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    page_size = table.Column<int>(type: "integer", nullable: false),
                    window_version = table.Column<long>(type: "bigint", nullable: false),
                    boundary = table.Column<long>(type: "bigint", nullable: false),
                    last_after = table.Column<long>(type: "bigint", nullable: false),
                    window_complete = table.Column<bool>(type: "boolean", nullable: false),
                    visibility_stamp = table.Column<byte[]>(type: "bytea", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_stream", x => x.id);
                    table.CheckConstraint("ck_sync_stream_checkpoint", "window_version BETWEEN 1 AND 9007199254740991 AND last_after>=0 AND boundary>=last_after AND boundary<=9007199254740991 AND page_size>0 AND expires_at>created_at");
                    table.ForeignKey(
                        name: "fk_sync_stream_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sync_stream_profiles_account_id_profile_id",
                        columns: x => new { x.account_id, x.profile_id },
                        principalSchema: "cerberus",
                        principalTable: "profile",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_change",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stream_id = table.Column<long>(type: "bigint", nullable: false),
                    window_version = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('cerberus.server_sequence')"),
                    resource_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_change", x => x.id);
                    table.CheckConstraint("ck_sync_change_descriptor", "resource_kind IN('profile','record','folder','collection','grant') AND window_version BETWEEN 1 AND 9007199254740991 AND sequence BETWEEN 1 AND 9007199254740991 AND event_type IN('upsert','removed','permanently-deleted') AND (event_type='permanently-deleted')=(deleted_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_sync_change_sync_stream_stream_id",
                        column: x => x.stream_id,
                        principalSchema: "cerberus",
                        principalTable: "sync_stream",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_inventory",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stream_id = table.Column<long>(type: "bigint", nullable: false),
                    resource_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_inventory", x => x.id);
                    table.CheckConstraint("ck_sync_inventory_metadata", "resource_kind IN('profile','record','folder','collection','grant') AND octet_length(fingerprint)=32");
                    table.ForeignKey(
                        name: "fk_sync_inventory_sync_stream_stream_id",
                        column: x => x.stream_id,
                        principalSchema: "cerberus",
                        principalTable: "sync_stream",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sync_change_sequence",
                schema: "cerberus",
                table: "sync_change",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_change_stream_id_window_version_resource_kind_resource",
                schema: "cerberus",
                table: "sync_change",
                columns: new[] { "stream_id", "window_version", "resource_kind", "resource_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_change_stream_id_window_version_sequence",
                schema: "cerberus",
                table: "sync_change",
                columns: new[] { "stream_id", "window_version", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_inventory_stream_id_resource_kind_resource_id",
                schema: "cerberus",
                table: "sync_inventory",
                columns: new[] { "stream_id", "resource_kind", "resource_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_stream_account_id_profile_id",
                schema: "cerberus",
                table: "sync_stream",
                columns: new[] { "account_id", "profile_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_stream_expires_at",
                schema: "cerberus",
                table: "sync_stream",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_sync_stream_public_id",
                schema: "cerberus",
                table: "sync_stream",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_change",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "sync_inventory",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "sync_stream",
                schema: "cerberus");
        }
    }
}
