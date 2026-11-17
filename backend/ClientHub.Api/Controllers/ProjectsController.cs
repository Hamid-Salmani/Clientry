using System.Security.Claims;
using ClientHub.Application;
using ClientHub.Domain;
using ClientHub.Api.Hubs;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController(ClientHubDbContext db, IHubContext<ProjectHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] Guid? clientId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = "startDate",
        [FromQuery] bool descending = true,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Projects.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(project => project.Name.ToLower().Contains(term) ||
                project.Description.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProjectStatus>(status, true, out var projectStatus) ||
                !Enum.IsDefined(projectStatus))
                return BadRequest(new { message = "Unknown project status." });
            query = query.Where(project => project.Status == projectStatus);
        }
        if (clientId.HasValue && User.IsInRole("Admin"))
            query = query.Where(project => project.ClientId == clientId.Value);

        var total = await query.CountAsync(cancellationToken);
        var ordered = (sortBy?.ToLowerInvariant(), descending) switch
        {
            ("name", false) => query.OrderBy(project => project.Name),
            ("name", true) => query.OrderByDescending(project => project.Name),
            ("status", false) => query.OrderBy(project => project.Status),
            ("status", true) => query.OrderByDescending(project => project.Status),
            ("duedate", false) => query.OrderBy(project => project.DueDate),
            ("duedate", true) => query.OrderByDescending(project => project.DueDate),
            (_, false) => query.OrderBy(project => project.StartDate),
            _ => query.OrderByDescending(project => project.StartDate)
        };
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var projects = await ordered.Skip(skip).Take(pageSize).Select(project => new
        {
            project.Id,
            project.Name,
            project.Description,
            status = project.Status.ToString(),
            project.StartDate,
            project.DueDate,
            completedMilestones = project.Milestones.Count(milestone => milestone.IsCompleted),
            totalMilestones = project.Milestones.Count,
            progress = project.Milestones.Count == 0 ? 0 :
                (int)Math.Round(project.Milestones.Count(milestone => milestone.IsCompleted) * 100d / project.Milestones.Count),
            milestones = project.Milestones.OrderBy(milestone => milestone.Order).Select(milestone => new
            {
                milestone.Id, milestone.Title, milestone.DueDate, milestone.IsCompleted, milestone.Order
            })
        }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(projects.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 ||
            string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 4000)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["project"] = ["Name and description are required and must be within the supported length."]
            }));
        if (request.DueDate.HasValue && request.DueDate.Value < request.StartDate)
            return BadRequest(new { message = "Due date cannot be before the start date." });
        if (!await db.Clients.AnyAsync(x => x.Id == request.ClientId && x.IsActive, cancellationToken))
            return NotFound();
        var project = new Project
        {
            ClientId = request.ClientId,
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            StartDate = request.StartDate,
            DueDate = request.DueDate
        };
        db.Projects.Add(project);
        var actorId = GetActorId();
        AddActivity(project, actorId, "Project created", project.Name);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Project created: {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId = project.Id }, new { project.Id });
    }

    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().Where(x => x.Id == projectId)
            .Select(project => new
            {
                project.Id, project.ClientId, project.Name, project.Description, status = project.Status.ToString(),
                project.StartDate, project.DueDate,
                progress = project.Milestones.Count == 0 ? 0 :
                    (int)Math.Round(project.Milestones.Count(milestone => milestone.IsCompleted) * 100d / project.Milestones.Count),
                milestones = project.Milestones.OrderBy(milestone => milestone.Order).Select(milestone => new
                {
                    milestone.Id, milestone.Title, milestone.DueDate, milestone.IsCompleted, milestone.Order
                })
            }).SingleOrDefaultAsync(cancellationToken);
        return project is null ? NotFound() : Ok(project);
    }

    [HttpPut("{projectId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        if (!IsValidProjectText(request.Name, request.Description))
            return BadRequest(new { message = "Name and description are required and must be within the supported length." });
        if (request.DueDate.HasValue && request.DueDate.Value < request.StartDate)
            return BadRequest(new { message = "Due date cannot be before the start date." });

        project.Name = request.Name.Trim();
        project.Description = request.Description.Trim();
        project.StartDate = request.StartDate;
        project.DueDate = request.DueDate;
        var actorId = GetActorId();
        AddActivity(project, actorId, "Project updated", project.Name);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Project updated: {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("{projectId:guid}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateStatus(Guid projectId, UpdateProjectStatusRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ProjectStatus>(request.Status, true, out var nextStatus) || !Enum.IsDefined(nextStatus))
            return BadRequest(new { message = "Unknown project status." });
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        if (!ProjectRules.CanTransition(project.Status, nextStatus))
            return Conflict(new { message = $"A project cannot move from {project.Status} to {nextStatus}." });
        if (project.Status == nextStatus) return NoContent();

        var previousStatus = project.Status;
        project.Status = nextStatus;
        var actorId = GetActorId();
        AddActivity(project, actorId, "Project status changed", $"{previousStatus} to {nextStatus}");
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"{project.Name} is now {nextStatus}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{projectId:guid}/archive")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> Archive(Guid projectId, CancellationToken cancellationToken) =>
        SetArchivedStatusAsync(projectId, cancellationToken);

    [HttpPost("{projectId:guid}/milestones")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateMilestone(Guid projectId, MilestoneRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        if (!IsValidMilestoneTitle(request.Title))
            return BadRequest(new { message = "Milestone title is required and must be at most 160 characters." });
        var order = await db.Milestones.Where(x => x.ProjectId == projectId).Select(x => (int?)x.Order)
            .MaxAsync(cancellationToken) ?? 0;
        var milestone = new Milestone
        {
            ProjectId = projectId, ClientId = project.ClientId, Title = request.Title.Trim(),
            DueDate = request.DueDate, Order = order + 1
        };
        db.Milestones.Add(milestone);
        var actorId = GetActorId();
        AddActivity(project, actorId, "Milestone added", milestone.Title);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Milestone added: {milestone.Title}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId }, new { milestone.Id, milestone.Title, milestone.Order });
    }

    [HttpPut("{projectId:guid}/milestones/{milestoneId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateMilestone(Guid projectId, Guid milestoneId, MilestoneRequest request, CancellationToken cancellationToken)
    {
        var milestone = await db.Milestones.SingleOrDefaultAsync(x =>
            x.Id == milestoneId && x.ProjectId == projectId, cancellationToken);
        if (milestone is null) return NotFound();
        if (!IsValidMilestoneTitle(request.Title))
            return BadRequest(new { message = "Milestone title is required and must be at most 160 characters." });
        var project = await db.Projects.SingleAsync(x => x.Id == projectId, cancellationToken);
        milestone.Title = request.Title.Trim();
        milestone.DueDate = request.DueDate;
        var actorId = GetActorId();
        AddActivity(project, actorId, "Milestone updated", milestone.Title);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Milestone updated: {milestone.Title}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{projectId:guid}/milestones/{milestoneId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteMilestone(Guid projectId, Guid milestoneId, CancellationToken cancellationToken)
    {
        var milestone = await db.Milestones.SingleOrDefaultAsync(x =>
            x.Id == milestoneId && x.ProjectId == projectId, cancellationToken);
        if (milestone is null) return NotFound();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId, cancellationToken);
        db.Milestones.Remove(milestone);
        var actorId = GetActorId();
        AddActivity(project, actorId, "Milestone deleted", milestone.Title);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Milestone removed: {milestone.Title}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("{projectId:guid}/milestones/{milestoneId:guid}/completion")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetMilestoneCompletion(Guid projectId, Guid milestoneId, SetMilestoneCompletionRequest request, CancellationToken cancellationToken)
    {
        var milestone = await db.Milestones.SingleOrDefaultAsync(x =>
            x.Id == milestoneId && x.ProjectId == projectId, cancellationToken);
        if (milestone is null) return NotFound();
        if (milestone.IsCompleted == request.IsCompleted) return NoContent();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId, cancellationToken);
        milestone.IsCompleted = request.IsCompleted;
        var actorId = GetActorId();
        AddActivity(project, actorId, request.IsCompleted ? "Milestone completed" : "Milestone reopened", milestone.Title);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId,
            request.IsCompleted ? $"Milestone completed: {milestone.Title}" : $"Milestone reopened: {milestone.Title}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("{projectId:guid}/milestones/reorder")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReorderMilestones(Guid projectId, ReorderMilestonesRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        var milestones = await db.Milestones.Where(x => x.ProjectId == projectId).ToListAsync(cancellationToken);
        if (request.MilestoneIds is null || request.MilestoneIds.Count != milestones.Count ||
            request.MilestoneIds.Distinct().Count() != milestones.Count ||
            request.MilestoneIds.Any(id => milestones.All(milestone => milestone.Id != id)))
            return BadRequest(new { message = "Reorder must include every project milestone exactly once." });

        var byId = milestones.ToDictionary(x => x.Id);
        for (var index = 0; index < request.MilestoneIds.Count; index++)
            byId[request.MilestoneIds[index]].Order = index + 1;
        var actorId = GetActorId();
        AddActivity(project, actorId, "Milestones reordered", $"{milestones.Count} milestones");
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Milestones reordered for {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{projectId:guid}/activity")]
    public async Task<IActionResult> Activity(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) return NotFound();
        var activity = await db.ActivityLogs.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(item => new
            {
                item.Id, item.Action, item.Metadata, item.CreatedAt,
                actor = db.Users.Where(user => user.Id == item.ActorId).Select(user => user.FullName).FirstOrDefault() ?? "Team member"
            }).ToListAsync(cancellationToken);
        return Ok(activity);
    }

    [HttpGet("{projectId:guid}/comments")]
    public async Task<IActionResult> Comments(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) return NotFound();
        var comments = await db.Comments.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.CreatedAt).Select(x => new
            {
                x.Id, x.Body, x.CreatedAt, author = x.Author == null ? "Team member" : x.Author.FullName
            }).ToListAsync(cancellationToken);
        return Ok(comments);
    }

    [HttpPost("{projectId:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid projectId, AddCommentRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 5000)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["body"] = ["Comment is required (max 5,000 characters)."]
            }));
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var authorId)) return Unauthorized();
        var comment = new Comment
        {
            ProjectId = project.Id, ClientId = project.ClientId, AuthorId = authorId, Body = request.Body.Trim()
        };
        db.Comments.Add(comment);
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = project.Id, ClientId = project.ClientId, ActorId = authorId,
            Action = "Comment added", Metadata = "A project update was posted."
        });
        await AddWorkspaceNotificationsAsync(project.ClientId, authorId, $"New project update: {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.Group($"project:{projectId}").SendAsync("CommentAdded", new
        {
            comment.Id, comment.Body, comment.CreatedAt, author = User.Identity?.Name
        }, cancellationToken);
        return CreatedAtAction(nameof(Comments), new { projectId }, new { comment.Id });
    }

    [HttpPut("{projectId:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> UpdateComment(Guid projectId, Guid commentId, AddCommentRequest request, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(
            x => x.Id == commentId && x.ProjectId == projectId, cancellationToken);
        if (comment is null) return NotFound();
        if (!CanManageComment(comment.AuthorId)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 5000)
            return BadRequest(new { message = "Comment is required (max 5,000 characters)." });
        var project = await db.Projects.SingleAsync(x => x.Id == projectId, cancellationToken);
        comment.Body = request.Body.Trim();
        var actorId = GetActorId();
        AddActivity(project, actorId, "Comment updated", comment.Body);
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Project update edited: {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.Group($"project:{projectId}").SendAsync("CommentUpdated", new
        {
            comment.Id, comment.Body, comment.CreatedAt, author = User.Identity?.Name
        }, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{projectId:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid projectId, Guid commentId, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(
            x => x.Id == commentId && x.ProjectId == projectId, cancellationToken);
        if (comment is null) return NotFound();
        if (!CanManageComment(comment.AuthorId)) return Forbid();
        var project = await db.Projects.SingleAsync(x => x.Id == projectId, cancellationToken);
        db.Comments.Remove(comment);
        var actorId = GetActorId();
        AddActivity(project, actorId, "Comment deleted", "A project update was removed.");
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Project update removed: {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.Group($"project:{projectId}").SendAsync("CommentDeleted", new { comment.Id }, cancellationToken);
        return NoContent();
    }

    private async Task<IActionResult> SetArchivedStatusAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        if (!ProjectRules.CanTransition(project.Status, ProjectStatus.Archived))
            return Conflict(new { message = $"A project cannot move from {project.Status} to Archived." });
        if (project.Status == ProjectStatus.Archived) return NoContent();
        var previousStatus = project.Status;
        project.Status = ProjectStatus.Archived;
        var actorId = GetActorId();
        AddActivity(project, actorId, "Project archived", previousStatus.ToString());
        await AddWorkspaceNotificationsAsync(project.ClientId, actorId, $"Project archived: {project.Name}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task AddWorkspaceNotificationsAsync(Guid clientId, Guid actorId, string message, CancellationToken cancellationToken)
    {
        var recipientIds = await db.Users.IgnoreQueryFilters()
            .Where(user => user.ClientId == clientId && user.IsActive && user.Id != actorId)
            .Select(user => user.Id).ToListAsync(cancellationToken);
        var optedOutIds = await db.NotificationPreferences.IgnoreQueryFilters()
            .Where(preference => recipientIds.Contains(preference.UserId) && !preference.WorkspaceUpdates)
            .Select(preference => preference.UserId).ToListAsync(cancellationToken);
        foreach (var recipientId in recipientIds.Except(optedOutIds))
            db.Notifications.Add(new Notification { UserId = recipientId, Type = "Project update", Message = message });
    }

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId
            : throw new InvalidOperationException("The authenticated user identifier is missing.");

    private bool CanManageComment(Guid authorId) =>
        User.IsInRole("Admin") || Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) && userId == authorId;

    private void AddActivity(Project project, Guid actorId, string action, string metadata) =>
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = project.Id,
            ClientId = project.ClientId,
            ActorId = actorId,
            Action = action,
            Metadata = metadata
        });

    private static bool IsValidProjectText(string name, string description) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 160 &&
        !string.IsNullOrWhiteSpace(description) && description.Length <= 4000;

    private static bool IsValidMilestoneTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) && title.Length <= 160;

    public sealed record CreateProjectRequest(Guid ClientId, string Name, string Description, DateOnly StartDate, DateOnly? DueDate);
    public sealed record UpdateProjectRequest(string Name, string Description, DateOnly StartDate, DateOnly? DueDate);
    public sealed record UpdateProjectStatusRequest(string Status);
    public sealed record MilestoneRequest(string Title, DateOnly? DueDate);
    public sealed record SetMilestoneCompletionRequest(bool IsCompleted);
    public sealed record ReorderMilestonesRequest(List<Guid> MilestoneIds);
    public sealed record AddCommentRequest(string Body);
}
