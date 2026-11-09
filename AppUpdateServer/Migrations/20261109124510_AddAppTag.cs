using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppUpdateServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAppTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tag",
                table: "Apps",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Tag",
                table: "Apps",
                column: "Tag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Apps_Tag",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Tag",
                table: "Apps");
        }
    }
}
