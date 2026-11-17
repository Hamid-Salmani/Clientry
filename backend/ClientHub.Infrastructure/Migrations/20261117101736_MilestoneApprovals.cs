using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClientHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MilestoneApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "Milestones",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecidedAt",
                table: "Milestones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DecidedById",
                table: "Milestones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionNote",
                table: "Milestones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequestedAt",
                table: "Milestones",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "Milestones");

            migrationBuilder.DropColumn(
                name: "DecidedAt",
                table: "Milestones");

            migrationBuilder.DropColumn(
                name: "DecidedById",
                table: "Milestones");

            migrationBuilder.DropColumn(
                name: "DecisionNote",
                table: "Milestones");

            migrationBuilder.DropColumn(
                name: "RequestedAt",
                table: "Milestones");
        }
    }
}
