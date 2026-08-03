using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileExplorer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPermanentToFileOperationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Permanent",
                table: "FileOperationJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Permanent",
                table: "FileOperationJobs");
        }
    }
}
