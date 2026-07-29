using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileExplorer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobsAndTrash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileOperationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePathsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    TotalItems = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrentItem = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileOperationJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrashItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", nullable: false),
                    TrashPath = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsDirectory = table.Column<bool>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrashItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileOperationJobs_Status",
                table: "FileOperationJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TrashItems_DeletedAt",
                table: "TrashItems",
                column: "DeletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileOperationJobs");

            migrationBuilder.DropTable(
                name: "TrashItems");
        }
    }
}
