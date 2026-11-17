using System.Security.Claims;
using ClientHub.Application;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController(ClientHubDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] bool? unreadOnly = null, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var userId = GetUserId();
        var query = db.Notifications.AsNoTracking().Where(notification => notification.UserId == userId);
        if (unreadOnly == true) query = query.Where(notification => !notification.IsRead);
        var total = await query.CountAsync(cancellationToken);
        var unreadCount = await db.Notifications.CountAsync(
            notification => notification.UserId == userId && !notification.IsRead, cancellationToken);
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var items = await query.OrderByDescending(notification => notification.CreatedAt)
            .ThenBy(notification => notification.Id).Skip(skip).Take(pageSize)
            .Select(notification => new
            {
                notification.Id, notification.Type, notification.Message, notification.IsRead, notification.CreatedAt
            }).ToListAsync(cancellationToken);
        return Ok(new NotificationsPage(items.Cast<object>().ToArray(), total, page, pageSize, unreadCount));
    }

    [HttpPatch("{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var notification = await db.Notifications.SingleOrDefaultAsync(
            item => item.Id == notificationId && item.UserId == userId, cancellationToken);
        if (notification is null) return NotFound();
        if (!notification.IsRead)
        {
            notification.IsRead = true;
            await db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        await db.Notifications.Where(item => item.UserId == userId && !item.IsRead)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsRead, true), cancellationToken);
        return NoContent();
    }

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        return Ok(new NotificationPreferencesResponse(
            preferences?.WorkspaceUpdates ?? true,
            preferences?.InvoiceReminders ?? true));
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences(NotificationPreferencesRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var preferences = await db.NotificationPreferences.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);
        if (preferences is null)
        {
            preferences = new UserNotificationPreference { UserId = userId };
            db.NotificationPreferences.Add(preferences);
        }
        preferences.WorkspaceUpdates = request.WorkspaceUpdates;
        preferences.InvoiceReminders = request.InvoiceReminders;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private Guid GetUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated user identifier is missing.");

    public sealed record NotificationPreferencesRequest(bool WorkspaceUpdates, bool InvoiceReminders);
    public sealed record NotificationPreferencesResponse(bool WorkspaceUpdates, bool InvoiceReminders);
    public sealed record NotificationsPage(IReadOnlyList<object> Items, int Total, int Page, int PageSize, int UnreadCount);
}
