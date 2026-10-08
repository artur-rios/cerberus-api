using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class VaultAccessSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vault_access_session",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    handle_verifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<long>(type: "bigint", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    revocation_generation = table.Column<long>(type: "bigint", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vault_access_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_vault_access_session_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vault_access_session_account_id",
                schema: "cerberus",
                table: "vault_access_session",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_vault_access_session_handle_verifier",
                schema: "cerberus",
                table: "vault_access_session",
                column: "handle_verifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vault_access_session",
                schema: "cerberus");
        }
    }
}
