using System.Security.Claims;
using ClientHub.Application;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/requests")]
public sealed class ProjectRequestsController(
    ClientHubDbContext db,
    IFileStorage storage,
    IValidator<ProjectRequestsController.CreateRequest> validator) : ControllerBase
{
    private const long MaxAttachmentSize = 10 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".txt"] = "text/plain"
    };

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status = null, [FromQuery] Guid? clientId = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.ProjectRequests.AsNoTracking();
        if (User.IsInRole("Admin"))
        {
            if (clientId.HasValue) query = query.Where(item => item.ClientId == clientId.Value);
        }
        else
        {
            query = query.Where(item => item.CreatedById == GetUserId());
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProjectRequestStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                return BadRequest(new { message = "Unknown request status." });
            query = query.Where(item => item.Status == parsed);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.CreatedAt)
            .Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue)).Take(pageSize)
            .Select(item => new
            {
                item.Id, item.ClientId, item.CreatedById, item.Title, item.Description,
                priority = item.Priority.ToString(), status = item.Status.ToString(),
                item.AdminNote, item.ConvertedProjectId, item.CreatedAt, item.UpdatedAt,
                clientName = item.Client!.CompanyName,
                attachments = item.Attachments.OrderBy(file => file.UploadedAt).Select(file => new
                {
                    file.Id, file.FileName, file.ContentType, file.Size, file.UploadedAt
                })
            }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(items.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost]
    [Authorize(Roles = "Client")]
    [EnableRateLimiting("project-requests")]
    public async Task<IActionResult> Create(CreateRequest request, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(request, cancellationToken);
        if (!result.IsValid)
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>(result.ToDictionary())));
        if (!Guid.TryParse(User.FindFirstValue("client_id"), out var clientId)) return NotFound();
        var entity = new ProjectRequest
        {
            ClientId = clientId,
            CreatedById = GetUserId(),
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Priority = Enum.Parse<ProjectRequestPriority>(request.Priority, true)
        };
        db.ProjectRequests.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { requestId = entity.Id }, new { entity.Id });
    }

    [HttpGet("{requestId:guid}")]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken)
    {
        var item = await db.ProjectRequests.AsNoTracking().Where(request => request.Id == requestId)
            .Where(request => User.IsInRole("Admin") || request.CreatedById == GetUserId())
            .Select(request => new
            {
                request.Id, request.ClientId, request.CreatedById, request.Title, request.Description,
                priority = request.Priority.ToString(), status = request.Status.ToString(),
                request.AdminNote, request.ConvertedProjectId, request.CreatedAt, request.UpdatedAt,
                clientName = request.Client!.CompanyName,
                attachments = request.Attachments.OrderBy(file => file.UploadedAt).Select(file => new
                {
                    file.Id, file.FileName, file.ContentType, file.Size, file.UploadedAt
                })
            }).SingleOrDefaultAsync(cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("{requestId:guid}/attachments")]
    [Authorize(Roles = "Client")]
    [EnableRateLimiting("project-requests")]
    [RequestSizeLimit(MaxAttachmentSize)]
    public async Task<IActionResult> UploadAttachment(Guid requestId, IFormFile file, CancellationToken cancellationToken)
    {
        var request = await db.ProjectRequests.Include(item => item.Attachments)
            .SingleOrDefaultAsync(item => item.Id == requestId && item.CreatedById == GetUserId(), cancellationToken);
        if (request is null) return NotFound();
        if (request.Status != ProjectRequestStatus.New) return Conflict(new { message = "Attachments can only be added to new requests." });
        if (request.Attachments.Count >= 3) return Conflict(new { message = "A request can have at most three attachments." });
        if (file is null || file.Length == 0 || file.Length > MaxAttachmentSize)
            return BadRequest(new { message = "Choose a file up to 10 MB." });
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (!AllowedTypes.TryGetValue(extension, out var contentType) ||
            !string.Equals(file.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "This file type is not allowed." });
        await using var input = file.OpenReadStream();
        var header = new byte[8];
        var read = await input.ReadAsync(header, cancellationToken);
        input.Position = 0;
        if (!HasValidSignature(extension, header, read))
            return BadRequest(new { message = "The file contents do not match the selected type." });

        var storedPath = await storage.SaveAsync(input, cancellationToken);
        var attachment = new ProjectRequestAttachment
        {
            ProjectRequestId = requestId, ClientId = request.ClientId,
            FileName = Path.GetFileName(file.FileName), StoredPath = storedPath,
            ContentType = contentType, Size = file.Length
        };
        db.ProjectRequestAttachments.Add(attachment);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(storedPath, cancellationToken);
            throw;
        }
        return CreatedAtAction(nameof(Get), new { requestId }, new { attachment.Id, attachment.FileName });
    }

    [HttpGet("{requestId:guid}/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> DownloadAttachment(Guid requestId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await db.ProjectRequestAttachments.AsNoTracking()
            .SingleOrDefaultAsync(file => file.Id == attachmentId && file.ProjectRequestId == requestId, cancellationToken);
        if (attachment is null) return NotFound();
        try
        {
            var stream = await storage.OpenReadAsync(attachment.StoredPath, cancellationToken);
            return File(stream, attachment.ContentType, attachment.FileName);
        }
        catch (FileNotFoundException) { return NotFound(); }
    }

    [HttpDelete("{requestId:guid}")]
    [Authorize(Roles = "Client")]
    public async Task<IActionResult> Cancel(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await db.ProjectRequests.SingleOrDefaultAsync(
            item => item.Id == requestId && item.CreatedById == GetUserId(), cancellationToken);
        if (request is null) return NotFound();
        if (request.Status != ProjectRequestStatus.New)
            return Conflict(new { message = "Only new requests can be cancelled." });
        request.Status = ProjectRequestStatus.Cancelled;
        request.AdminNote = "Cancelled by client.";
        request.UpdatedAt = DateTime.UtcNow;
        await NotifyAdminsAsync(request, "Request cancelled", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("/api/admin/requests/{requestId:guid}/decision")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Decide(Guid requestId, AdminDecisionRequest decision, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.Note) || decision.Note.Trim().Length > 2000 ||
            !Enum.TryParse<ProjectRequestStatus>(decision.Status, true, out var next) ||
            next is not (ProjectRequestStatus.Accepted or ProjectRequestStatus.Declined))
            return BadRequest(new { message = "Choose Accepted or Declined and provide a note of at most 2,000 characters." });
        var request = await db.ProjectRequests.SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null) return NotFound();
        if (request.Status is not (ProjectRequestStatus.New or ProjectRequestStatus.InReview))
            return Conflict(new { message = "This request can no longer be reviewed." });
        request.Status = next;
        request.AdminNote = decision.Note.Trim();
        request.UpdatedAt = DateTime.UtcNow;
        await NotifyClientAsync(request, $"Your request “{request.Title}” was {next}. {request.AdminNote}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("/api/admin/requests/{requestId:guid}/review")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> StartReview(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await db.ProjectRequests.SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null) return NotFound();
        if (request.Status != ProjectRequestStatus.New)
            return Conflict(new { message = "Only new requests can be moved into review." });
        request.Status = ProjectRequestStatus.InReview;
        request.UpdatedAt = DateTime.UtcNow;
        await NotifyClientAsync(request, $"Your request “{request.Title}” is now in review.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("/api/admin/requests/{requestId:guid}/convert")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Convert(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await db.ProjectRequests.SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null) return NotFound();
        if (request.Status is not (ProjectRequestStatus.Accepted or ProjectRequestStatus.InReview))
            return Conflict(new { message = "Only accepted or in-review requests can be converted." });
        var project = new Project
        {
            ClientId = request.ClientId, Name = request.Title, Description = request.Description,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        db.Projects.Add(project);
        request.Status = ProjectRequestStatus.Converted;
        request.ConvertedProjectId = project.Id;
        request.UpdatedAt = DateTime.UtcNow;
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = project.Id, ClientId = project.ClientId, ActorId = GetUserId(),
            Action = "Project created from request", Metadata = request.Title
        });
        await NotifyClientAsync(request, $"Your request “{request.Title}” is now a project.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Created($"/api/projects/{project.Id}", new { project.Id });
    }

    private async Task NotifyClientAsync(ProjectRequest request, string message, CancellationToken cancellationToken)
    {
        var clientUsers = await db.Users.Where(user => user.ClientId == request.ClientId && user.IsActive)
            .Select(user => user.Id).ToListAsync(cancellationToken);
        foreach (var userId in clientUsers)
            db.Notifications.Add(new Notification { UserId = userId, Type = "Request update", Message = message });
    }

    private async Task NotifyAdminsAsync(ProjectRequest request, string message, CancellationToken cancellationToken)
    {
        var adminUsers = await db.Users.IgnoreQueryFilters().Where(user => user.Role == UserRole.Admin && user.IsActive)
            .Select(user => user.Id).ToListAsync(cancellationToken);
        foreach (var userId in adminUsers)
            db.Notifications.Add(new Notification { UserId = userId, Type = "Request update", Message = $"{message}: {request.Title}" });
    }

    private Guid GetUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated user identifier is missing.");

    private static bool HasValidSignature(string extension, byte[] header, int read) => extension.ToLowerInvariant() switch
    {
        ".pdf" => read >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
        ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
        ".txt" => read >= 0,
        _ => false
    };

    public sealed record CreateRequest(string Title, string Description, string Priority);
    public sealed record AdminDecisionRequest(string Status, string? Note);
}
