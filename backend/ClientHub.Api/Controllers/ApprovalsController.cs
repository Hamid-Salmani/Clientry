using System.Net;
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
public sealed class ApprovalsController(
    ClientHubDbContext db,
    IEmailSender emailSender,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("/api/approvals")]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Milestones.AsNoTracking()
            .Where(item => item.ApprovalStatus == MilestoneApprovalStatus.Pending);
        var total = await query.CountAsync(cancellationToken);
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var approvals = await query.OrderBy(item => item.RequestedAt).ThenBy(item => item.Id)
            .Skip(skip).Take(pageSize)
            .Select(item => new
            {
                item.Id, item.ProjectId, item.ClientId, item.Title, item.DueDate,
                item.RequestedAt, approvalStatus = item.ApprovalStatus.ToString(),
                projectName = item.Project!.Name
            }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(approvals.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost("/api/projects/{projectId:guid}/milestones/{milestoneId:guid}/approval-request")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RequestApproval(Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
    {
        var milestone = await db.Milestones.Include(item => item.Project)
            .SingleOrDefaultAsync(item => item.Id == milestoneId && item.ProjectId == projectId, cancellationToken);
        if (milestone is null) return NotFound();
        if (!MilestoneApprovalRules.CanRequest(milestone.ApprovalStatus, milestone.IsCompleted))
            return Conflict(new { message = "This milestone cannot be sent for approval in its current state." });

        var actorId = GetUserId();
        var now = DateTime.UtcNow;
        milestone.ApprovalStatus = MilestoneApprovalStatus.Pending;
        milestone.RequestedAt = now;
        milestone.DecidedById = null;
        milestone.DecidedAt = null;
        milestone.DecisionNote = null;
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = projectId, ClientId = milestone.ClientId, ActorId = actorId,
            Action = "Approval requested", Metadata = milestone.Title, CreatedAt = now
        });

        var recipients = await db.Users.Where(user => user.ClientId == milestone.ClientId && user.IsActive)
            .Select(user => new { user.Id, user.Email }).ToListAsync(cancellationToken);
        foreach (var recipient in recipients)
            db.Notifications.Add(new Notification
            {
                UserId = recipient.Id, Type = "Approval requested",
                Message = $"Approval requested for {milestone.Title} in {milestone.Project!.Name}."
            });
        await db.SaveChangesAsync(cancellationToken);

        var approvalUrl = $"{GetFrontendBaseUrl()}/approvals";
        var html = $"<p>Approval is requested for <strong>{WebUtility.HtmlEncode(milestone.Title)}</strong> " +
            $"in {WebUtility.HtmlEncode(milestone.Project!.Name)}.</p><p><a href=\"{WebUtility.HtmlEncode(approvalUrl)}\">Review approval</a></p>";
        foreach (var recipient in recipients)
            await emailSender.SendAsync(recipient.Email, "Milestone approval requested", html, null, cancellationToken);
        return NoContent();
    }

    [HttpPost("/api/projects/{projectId:guid}/milestones/{milestoneId:guid}/approval-decision")]
    [Authorize(Roles = "Client")]
    public async Task<IActionResult> Decide(Guid projectId, Guid milestoneId, ApprovalDecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<MilestoneApprovalStatus>(request.Decision, true, out var decision) ||
            decision is not (MilestoneApprovalStatus.Approved or MilestoneApprovalStatus.ChangesRequested))
            return BadRequest(new { message = "Choose Approved or ChangesRequested." });
        var note = request.Note?.Trim();
        if (decision == MilestoneApprovalStatus.ChangesRequested && string.IsNullOrWhiteSpace(note))
            return BadRequest(new { message = "A note is required when requesting changes." });
        if (note?.Length > 2000)
            return BadRequest(new { message = "The decision note must be at most 2,000 characters." });

        var milestone = await db.Milestones.Include(item => item.Project)
            .SingleOrDefaultAsync(item => item.Id == milestoneId && item.ProjectId == projectId, cancellationToken);
        if (milestone is null) return NotFound();
        if (!MilestoneApprovalRules.CanDecide(milestone.ApprovalStatus))
            return Conflict(new { message = "This approval has already been decided or is not pending." });

        var actorId = GetUserId();
        var now = DateTime.UtcNow;
        milestone.ApprovalStatus = decision;
        milestone.DecidedById = actorId;
        milestone.DecidedAt = now;
        milestone.DecisionNote = note;
        if (decision == MilestoneApprovalStatus.Approved)
            milestone.IsCompleted = true;

        var action = decision == MilestoneApprovalStatus.Approved ? "Milestone approved" : "Changes requested";
        var projectName = milestone.Project!.Name;
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = projectId, ClientId = milestone.ClientId, ActorId = actorId,
            Action = action,
            Metadata = string.IsNullOrWhiteSpace(note) ? milestone.Title : $"{milestone.Title}: {note}",
            CreatedAt = now
        });

        var admins = await db.Users.IgnoreQueryFilters()
            .Where(user => user.Role == UserRole.Admin && user.IsActive)
            .Select(user => new { user.Id, user.Email }).ToListAsync(cancellationToken);
        var message = decision == MilestoneApprovalStatus.Approved
            ? $"{milestone.Title} in {projectName} was approved."
            : $"Changes were requested for {milestone.Title} in {projectName}: {note}";
        foreach (var admin in admins)
            db.Notifications.Add(new Notification { UserId = admin.Id, Type = "Approval decision", Message = message });
        await db.SaveChangesAsync(cancellationToken);

        var projectUrl = $"{GetFrontendBaseUrl()}/projects/{projectId}";
        var html = $"<p>{WebUtility.HtmlEncode(message)}</p><p><a href=\"{WebUtility.HtmlEncode(projectUrl)}\">View project</a></p>";
        foreach (var admin in admins)
            await emailSender.SendAsync(admin.Email, "Milestone approval decision", html, null, cancellationToken);
        return NoContent();
    }

    private Guid GetUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated user identifier is missing.");

    private string GetFrontendBaseUrl() =>
        (configuration["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');

    public sealed record ApprovalDecisionRequest(string Decision, string? Note);
}
