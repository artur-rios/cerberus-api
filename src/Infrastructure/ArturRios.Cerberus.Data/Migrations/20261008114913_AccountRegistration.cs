using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    heimdall_public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    details_envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    closure_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closure_purge_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    renewal_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    renewal_interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    revocation_generation = table.Column<long>(type: "bigint", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registration_operation",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    completed_identity_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registration_operation", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_heimdall_public_id",
                schema: "cerberus",
                table: "account",
                column: "heimdall_public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_public_id",
                schema: "cerberus",
                table: "account",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_registration_operation_operation_id",
                schema: "cerberus",
                table: "registration_operation",
                column: "operation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "registration_operation",
                schema: "cerberus");
        }
    }
}
