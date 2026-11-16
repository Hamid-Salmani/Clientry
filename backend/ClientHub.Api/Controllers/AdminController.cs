using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public sealed class AdminController(ClientHubDbContext db) : ControllerBase
{
    [HttpGet("clients")]
    public async Task<IActionResult> Clients(CancellationToken cancellationToken)
    {
        var clients = await db.Clients.AsNoTracking().Include(x => x.Users).Include(x => x.Projects)
            .OrderBy(x => x.CompanyName).ToListAsync(cancellationToken);
        return Ok(clients.Select(x => new
        {
            x.Id, x.CompanyName,
            contact = x.Users.Where(u => u.IsActive).Select(u => u.FullName).FirstOrDefault(),
            activeProjects = x.Projects.Count(p => p.Status != ProjectStatus.Archived)
        }));
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstMonth = currentMonth.AddMonths(-5);
        var invoices = await db.Invoices.AsNoTracking().Include(x => x.Lines).ToListAsync(cancellationToken);
        var projects = await db.Projects.AsNoTracking().ToListAsync(cancellationToken);
        var payments = await db.Payments.AsNoTracking().Where(x => x.PaidAt >= firstMonth)
            .Select(x => new { x.PaidAt, x.Amount }).ToListAsync(cancellationToken);
        var revenueByMonth = Enumerable.Range(0, 6).Select(offset =>
        {
            var month = firstMonth.AddMonths(offset);
            return new
            {
                month = month.ToString("MMM"),
                revenue = payments.Where(x => x.PaidAt.HasValue &&
                    x.PaidAt.Value.Year == month.Year && x.PaidAt.Value.Month == month.Month).Sum(x => x.Amount)
            };
        }).ToArray();
        return Ok(new
        {
            activeProjects = projects.Count(x => x.Status is ProjectStatus.InProgress or ProjectStatus.InReview),
            clientCount = await db.Clients.CountAsync(cancellationToken),
            revenueThisMonth = payments.Where(x => x.PaidAt.HasValue &&
                x.PaidAt.Value.Year == today.Year && x.PaidAt.Value.Month == today.Month).Sum(x => x.Amount),
            unpaidInvoices = invoices.Count(x => x.Status == InvoiceStatus.Sent),
            overdueInvoices = invoices.Count(x => x.Status == InvoiceStatus.Overdue),
            projectStatus = projects.GroupBy(x => x.Status.ToString()).Select(x => new { status = x.Key, count = x.Count() }),
            revenueByMonth,
            recentActivity = await db.ActivityLogs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(6)
                .Select(x => new { x.Action, x.Metadata, x.CreatedAt }).ToListAsync(cancellationToken)
        });
    }
}
