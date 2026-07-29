using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileExplorer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPermanentlyDeletedNamesToJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PermanentlyDeletedNamesJson",
                table: "FileOperationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PermanentlyDeletedNamesJson",
                table: "FileOperationJobs");
        }
    }
}
