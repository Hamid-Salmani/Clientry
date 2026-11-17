using System.Security.Claims;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Client")]
[Route("api/me")]
public sealed class ClientOverviewController(ClientHubDbContext db) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user?.ClientId is null) return NotFound();

        var now = DateTime.UtcNow;
        var lastVisit = user.LastVisitedAt ?? now.AddDays(-7);
        var today = DateOnly.FromDateTime(now);
        var pendingApprovals = await db.Milestones.AsNoTracking()
            .Where(item => item.ApprovalStatus == MilestoneApprovalStatus.Pending)
            .OrderBy(item => item.RequestedAt)
            .Select(item => new
            {
                item.Id, item.ProjectId, item.Title, item.DueDate, item.RequestedAt,
                projectName = item.Project!.Name
            }).ToListAsync(cancellationToken);
        var unpaidInvoiceRows = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.Status == InvoiceStatus.Sent || invoice.Status == InvoiceStatus.Overdue)
            .OrderBy(invoice => invoice.DueDate)
            .Select(invoice => new
            {
                invoice.Id, invoice.Number, invoice.DueDate, status = invoice.Status.ToString(),
                invoice.Currency,
                amount = invoice.Lines.Sum(line => (double?)((double)line.Quantity * (double)line.UnitPrice)) ?? 0d
            }).ToListAsync(cancellationToken);
        var unpaidInvoices = unpaidInvoiceRows.Select(invoice => new
        {
            invoice.Id, invoice.Number, invoice.DueDate, invoice.status, invoice.Currency,
            amount = decimal.Round((decimal)invoice.amount, 2)
        }).ToArray();
        var unreadNotifications = await db.Notifications.CountAsync(
            item => item.UserId == userId && !item.IsRead, cancellationToken);
        var recentProjectActivity = await (
            from activity in db.ActivityLogs.AsNoTracking()
            join project in db.Projects.AsNoTracking() on activity.ProjectId equals project.Id
            where activity.CreatedAt > lastVisit
            orderby activity.CreatedAt descending
            select new
            {
                activity.ProjectId, projectName = project.Name, activity.Action, activity.Metadata,
                activity.CreatedAt
            }).Take(10).ToListAsync(cancellationToken);
        var nextMilestones = await db.Milestones.AsNoTracking()
            .Where(item => !item.IsCompleted && item.DueDate.HasValue && item.DueDate.Value >= today)
            .Where(item => !db.Milestones.Any(next => next.ProjectId == item.ProjectId && !next.IsCompleted &&
                next.DueDate.HasValue && next.DueDate.Value >= today && next.DueDate.Value < item.DueDate!.Value))
            .OrderBy(item => item.DueDate)
            .Select(item => new
            {
                item.Id, item.ProjectId, item.Title, item.DueDate, projectName = item.Project!.Name
            }).Take(10).ToListAsync(cancellationToken);

        user.LastVisitedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            lastVisitAt = lastVisit,
            pendingApprovals,
            unpaidInvoices,
            unreadNotificationCount = unreadNotifications,
            recentProjectActivity,
            nextMilestones
        });
    }
}
