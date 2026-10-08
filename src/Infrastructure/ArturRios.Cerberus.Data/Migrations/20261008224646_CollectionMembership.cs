using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Cerberus.Data.Migrations
{
    /// <inheritdoc />
    public partial class CollectionMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_folder",
                schema: "cerberus",
                columns: table => new
                {
                    collection_id = table.Column<long>(type: "bigint", nullable: false),
                    folder_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_folder", x => new { x.collection_id, x.folder_id });
                    table.ForeignKey(
                        name: "fk_collection_folder_collection_account_id_collection_id",
                        columns: x => new { x.account_id, x.collection_id },
                        principalSchema: "cerberus",
                        principalTable: "collection",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_folder_folder_account_id_folder_id",
                        columns: x => new { x.account_id, x.folder_id },
                        principalSchema: "cerberus",
                        principalTable: "folder",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "collection_record",
                schema: "cerberus",
                columns: table => new
                {
                    collection_id = table.Column<long>(type: "bigint", nullable: false),
                    record_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_record", x => new { x.collection_id, x.record_id });
                    table.ForeignKey(
                        name: "fk_collection_record_collection_account_id_collection_id",
                        columns: x => new { x.account_id, x.collection_id },
                        principalSchema: "cerberus",
                        principalTable: "collection",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_record_record_account_id_record_id",
                        columns: x => new { x.account_id, x.record_id },
                        principalSchema: "cerberus",
                        principalTable: "record",
                        principalColumns: new[] { "account_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_folder_account_id_collection_id",
                schema: "cerberus",
                table: "collection_folder",
                columns: new[] { "account_id", "collection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_folder_account_id_folder_id",
                schema: "cerberus",
                table: "collection_folder",
                columns: new[] { "account_id", "folder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_record_account_id_collection_id",
                schema: "cerberus",
                table: "collection_record",
                columns: new[] { "account_id", "collection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_record_account_id_record_id",
                schema: "cerberus",
                table: "collection_record",
                columns: new[] { "account_id", "record_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_folder",
                schema: "cerberus");

            migrationBuilder.DropTable(
                name: "collection_record",
                schema: "cerberus");
        }
    }
}
