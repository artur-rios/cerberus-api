using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class VaultProtectionAndChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vault_protection",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    key_epoch = table.Column<long>(type: "bigint", nullable: false),
                    recovery_generation = table.Column<long>(type: "bigint", nullable: false),
                    material = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vault_protection", x => x.id);
                    table.ForeignKey(
                        name: "fk_vault_protection_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vault_unlock_challenge",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge = table.Column<byte[]>(type: "bytea", nullable: false),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    revocation_generation = table.Column<long>(type: "bigint", nullable: false),
                    consumed = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vault_unlock_challenge", x => x.id);
                    table.ForeignKey(
                        name: "fk_vault_unlock_challenge_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vault_protection_account_id",
                schema: "cerberus",
                table: "vault_protection",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vault_unlock_challenge_account_id_expires_at",
                schema: "cerberus",
                table: "vault_unlock_challenge",
                columns: new[] { "account_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vault_unlock_challenge_public_id",
                schema: "cerberus",
                table: "vault_unlock_challenge",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vault_protection",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "vault_unlock_challenge",
                schema: "cerberus");
        }
    }
}
