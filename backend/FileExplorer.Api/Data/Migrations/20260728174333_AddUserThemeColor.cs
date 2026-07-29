using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileExplorer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserThemeColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThemeColor",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "green");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThemeColor",
                table: "Users");
        }
    }
}
