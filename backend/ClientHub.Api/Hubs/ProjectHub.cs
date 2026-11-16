using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Hubs;

[Authorize]
public sealed class ProjectHub(ClientHubDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var projectValue = Context.GetHttpContext()?.Request.RouteValues["projectId"]?.ToString();
        if (!Guid.TryParse(projectValue, out var projectId) ||
            !await db.Projects.AnyAsync(project => project.Id == projectId))
            throw new HubException("Project not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");
        await base.OnConnectedAsync();
    }

    public async Task PostComment(string body)
    {
        var projectId = Guid.Parse(Context.GetHttpContext()!.Request.RouteValues["projectId"]!.ToString()!);
        var userId = Guid.Parse(Context.UserIdentifier!);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId);
        if (project is null) throw new HubException("Project not found.");
        if (string.IsNullOrWhiteSpace(body) || body.Length > 5000) throw new HubException("Invalid comment.");
        var comment = new Domain.Comment
        {
            ProjectId = project.Id, ClientId = project.ClientId, AuthorId = userId, Body = body.Trim()
        };
        db.Comments.Add(comment);
        db.ActivityLogs.Add(new Domain.ActivityLog
        {
            ProjectId = project.Id, ClientId = project.ClientId, ActorId = userId,
            Action = "Comment added", Metadata = "A project update was posted."
        });
        await db.SaveChangesAsync();
        await Clients.Group($"project:{projectId}").SendAsync("CommentAdded", new
        {
            comment.Id, comment.Body, comment.CreatedAt, author = Context.User?.Identity?.Name
        });
    }
}
