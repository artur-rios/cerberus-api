using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class SoftwareGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "software_grant",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    heimdall_software_public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_grant", x => x.id);
                    table.UniqueConstraint("ak_software_grant_account_id_id", x => new { x.account_id, x.id });
                    table.ForeignKey(
                        name: "fk_software_grant_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "software_grant_collection",
                schema: "cerberus",
                columns: table => new
                {
                    grant_id = table.Column<long>(type: "bigint", nullable: false),
                    collection_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient_key_envelope = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_grant_collection", x => new { x.grant_id, x.collection_id });
                    table.ForeignKey(
                        name: "fk_software_grant_collection_collection_account_id_collection_",
                        columns: x => new { x.account_id, x.collection_id },
                        principalSchema: "cerberus",
                        principalTable: "collection",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_software_grant_collection_software_grant_account_id_grant_id",
                        columns: x => new { x.account_id, x.grant_id },
                        principalSchema: "cerberus",
                        principalTable: "software_grant",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "software_grant_record",
                schema: "cerberus",
                columns: table => new
                {
                    grant_id = table.Column<long>(type: "bigint", nullable: false),
                    record_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient_key_envelope = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_grant_record", x => new { x.grant_id, x.record_id });
                    table.ForeignKey(
                        name: "fk_software_grant_record_record_account_id_record_id",
                        columns: x => new { x.account_id, x.record_id },
                        principalSchema: "cerberus",
                        principalTable: "record",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_software_grant_record_software_grant_account_id_grant_id",
                        columns: x => new { x.account_id, x.grant_id },
                        principalSchema: "cerberus",
                        principalTable: "software_grant",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_software_grant_account_id_heimdall_software_public_id",
                schema: "cerberus",
                table: "software_grant",
                columns: new[] { "account_id", "heimdall_software_public_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_grant_public_id",
                schema: "cerberus",
                table: "software_grant",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_grant_collection_account_id_collection_id",
                schema: "cerberus",
                table: "software_grant_collection",
                columns: new[] { "account_id", "collection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_grant_collection_account_id_grant_id",
                schema: "cerberus",
                table: "software_grant_collection",
                columns: new[] { "account_id", "grant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_grant_record_account_id_grant_id",
                schema: "cerberus",
                table: "software_grant_record",
                columns: new[] { "account_id", "grant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_software_grant_record_account_id_record_id",
                schema: "cerberus",
                table: "software_grant_record",
                columns: new[] { "account_id", "record_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "software_grant_collection",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "software_grant_record",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "software_grant",
                schema: "cerberus");
        }
    }
}
