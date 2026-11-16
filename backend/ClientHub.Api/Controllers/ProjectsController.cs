using System.Security.Claims;
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
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var projects = await db.Projects.AsNoTracking().Include(x => x.Milestones)
            .OrderByDescending(x => x.StartDate).ToListAsync(cancellationToken);
        return Ok(projects.Select(x => new
        {
            x.Id, x.Name, x.Description, status = x.Status.ToString(), x.StartDate, x.DueDate,
            completedMilestones = x.Milestones.Count(m => m.IsCompleted),
            totalMilestones = x.Milestones.Count,
            progress = x.Milestones.Count == 0 ? 0 :
                (int)Math.Round(x.Milestones.Count(m => m.IsCompleted) * 100d / x.Milestones.Count),
            milestones = x.Milestones.OrderBy(m => m.Order).Select(m => new
            {
                m.Id, m.Title, m.DueDate, m.IsCompleted, m.Order
            })
        }));
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
        if (!await db.Clients.AnyAsync(x => x.Id == request.ClientId, cancellationToken))
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
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(List), new { id = project.Id }, new { project.Id });
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
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.Group($"project:{projectId}").SendAsync("CommentAdded", new
        {
            comment.Id, comment.Body, comment.CreatedAt, author = User.Identity?.Name
        }, cancellationToken);
        return CreatedAtAction(nameof(Comments), new { projectId }, new { comment.Id });
    }

    public sealed record CreateProjectRequest(Guid ClientId, string Name, string Description, DateOnly StartDate, DateOnly? DueDate);
    public sealed record AddCommentRequest(string Body);
}
