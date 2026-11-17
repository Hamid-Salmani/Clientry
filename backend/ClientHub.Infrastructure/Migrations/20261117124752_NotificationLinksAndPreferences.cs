using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClientHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationLinksAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Link",
                table: "Notifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Approvals",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "Comments",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailApprovals",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailComments",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailInvoiceReminders",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailRequestUpdates",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailWorkspaceUpdates",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequestUpdates",
                table: "NotificationPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Link",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Approvals",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "Comments",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailApprovals",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailComments",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailInvoiceReminders",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailRequestUpdates",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailWorkspaceUpdates",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "RequestUpdates",
                table: "NotificationPreferences");
        }
    }
}
