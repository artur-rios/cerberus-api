using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class Profiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "server_sequence",
                schema: "cerberus",
                maxValue: 9007199254740991L);

            migrationBuilder.CreateTable(
                name: "profile",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_wrappers = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    server_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('cerberus.server_sequence')"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purge_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile", x => x.id);
                    table.ForeignKey(
                        name: "fk_profile_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_profile_account_id_server_sequence",
                schema: "cerberus",
                table: "profile",
                columns: new[] { "account_id", "server_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_profile_public_id",
                schema: "cerberus",
                table: "profile",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile",
                schema: "cerberus");

            migrationBuilder.DropSequence(
                name: "server_sequence",
                schema: "cerberus");
        }
    }
}
