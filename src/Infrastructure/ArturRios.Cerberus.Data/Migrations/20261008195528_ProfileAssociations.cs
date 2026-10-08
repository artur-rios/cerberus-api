using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_profiles_account_id_id",
                schema: "cerberus",
                table: "profile",
                columns: new[] { "account_id", "id" });

            migrationBuilder.CreateTable(
                name: "collection",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    server_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('cerberus.server_sequence')"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purge_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    key_epoch = table.Column<long>(type: "bigint", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection", x => x.id);
                    table.UniqueConstraint("ak_collection_account_id_id", x => new { x.account_id, x.id });
                    table.ForeignKey(
                        name: "fk_collection_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "folder",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    server_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('cerberus.server_sequence')"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purge_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    parent_folder_id = table.Column<long>(type: "bigint", nullable: true),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folder", x => x.id);
                    table.UniqueConstraint("ak_folder_account_id_id", x => new { x.account_id, x.id });
                    table.ForeignKey(
                        name: "fk_folder_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_folder_folder_account_id_parent_folder_id",
                        columns: x => new { x.account_id, x.parent_folder_id },
                        principalSchema: "cerberus",
                        principalTable: "folder",
                        principalColumns: new[] { "account_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "collection_grant",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient_account_id = table.Column<long>(type: "bigint", nullable: false),
                    access = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    recipient_key_envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_grant", x => x.id);
                    table.ForeignKey(
                        name: "fk_collection_grant_accounts_recipient_account_id",
                        column: x => x.recipient_account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_grant_collection_collection_id",
                        column: x => x.collection_id,
                        principalSchema: "cerberus",
                        principalTable: "collection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_collection",
                schema: "cerberus",
                columns: table => new
                {
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    collection_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_collection", x => new { x.profile_id, x.collection_id });
                    table.ForeignKey(
                        name: "fk_profile_collection_collection_collection_id",
                        column: x => x.collection_id,
                        principalSchema: "cerberus",
                        principalTable: "collection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_profile_collection_profiles_profile_id",
                        column: x => x.profile_id,
                        principalSchema: "cerberus",
                        principalTable: "profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_folder",
                schema: "cerberus",
                columns: table => new
                {
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    folder_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_folder", x => new { x.profile_id, x.folder_id });
                    table.ForeignKey(
                        name: "fk_profile_folder_folder_account_id_folder_id",
                        columns: x => new { x.account_id, x.folder_id },
                        principalSchema: "cerberus",
                        principalTable: "folder",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_profile_folder_profiles_account_id_profile_id",
                        columns: x => new { x.account_id, x.profile_id },
                        principalSchema: "cerberus",
                        principalTable: "profile",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "record",
                schema: "cerberus",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    envelope = table.Column<byte[]>(type: "bytea", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    server_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('cerberus.server_sequence')"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purge_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    folder_id = table.Column<long>(type: "bigint", nullable: true),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record", x => x.id);
                    table.UniqueConstraint("ak_record_account_id_id", x => new { x.account_id, x.id });
                    table.ForeignKey(
                        name: "fk_record_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "cerberus",
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_record_folder_account_id_folder_id",
                        columns: x => new { x.account_id, x.folder_id },
                        principalSchema: "cerberus",
                        principalTable: "folder",
                        principalColumns: new[] { "account_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "profile_record",
                schema: "cerberus",
                columns: table => new
                {
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    record_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_record", x => new { x.profile_id, x.record_id });
                    table.ForeignKey(
                        name: "fk_profile_record_profiles_account_id_profile_id",
                        columns: x => new { x.account_id, x.profile_id },
                        principalSchema: "cerberus",
                        principalTable: "profile",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_profile_record_record_account_id_record_id",
                        columns: x => new { x.account_id, x.record_id },
                        principalSchema: "cerberus",
                        principalTable: "record",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_account_id_server_sequence",
                schema: "cerberus",
                table: "collection",
                columns: new[] { "account_id", "server_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_public_id",
                schema: "cerberus",
                table: "collection",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_grant_collection_id_recipient_account_id",
                schema: "cerberus",
                table: "collection_grant",
                columns: new[] { "collection_id", "recipient_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_grant_public_id",
                schema: "cerberus",
                table: "collection_grant",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_grant_recipient_account_id",
                schema: "cerberus",
                table: "collection_grant",
                column: "recipient_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_folder_account_id_parent_folder_id",
                schema: "cerberus",
                table: "folder",
                columns: new[] { "account_id", "parent_folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_folder_account_id_server_sequence",
                schema: "cerberus",
                table: "folder",
                columns: new[] { "account_id", "server_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_folder_public_id",
                schema: "cerberus",
                table: "folder",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_profile_collection_collection_id",
                schema: "cerberus",
                table: "profile_collection",
                column: "collection_id");

            migrationBuilder.CreateIndex(
                name: "ix_profile_folder_account_id_folder_id",
                schema: "cerberus",
                table: "profile_folder",
                columns: new[] { "account_id", "folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_profile_folder_account_id_profile_id",
                schema: "cerberus",
                table: "profile_folder",
                columns: new[] { "account_id", "profile_id" });

            migrationBuilder.CreateIndex(
                name: "ix_profile_record_account_id_profile_id",
                schema: "cerberus",
                table: "profile_record",
                columns: new[] { "account_id", "profile_id" });

            migrationBuilder.CreateIndex(
                name: "ix_profile_record_account_id_record_id",
                schema: "cerberus",
                table: "profile_record",
                columns: new[] { "account_id", "record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_record_account_id_folder_id",
                schema: "cerberus",
                table: "record",
                columns: new[] { "account_id", "folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_record_account_id_server_sequence",
                schema: "cerberus",
                table: "record",
                columns: new[] { "account_id", "server_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_record_public_id",
                schema: "cerberus",
                table: "record",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_grant",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "profile_collection",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "profile_folder",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "profile_record",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "collection",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "record",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "folder",
                schema: "cerberus");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_profiles_account_id_id",
                schema: "cerberus",
                table: "profile");
        }
    }
}
