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
[Route("api/projects/{projectId:guid}/files")]
public sealed class FilesController(ClientHubDbContext db, IFileStorage storage) : ControllerBase
{
    private const long MaxFileSize = 10 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".txt"] = "text/plain"
    };

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, CancellationToken cancellationToken = default)
    {
        if (!await db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) return NotFound();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.ProjectFiles.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(file => file.FileName.ToLower().Contains(term));
        }
        var total = await query.CountAsync(cancellationToken);
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var items = await query.OrderByDescending(x => x.UploadedAt).ThenBy(x => x.Id)
            .Skip(skip).Take(pageSize)
            .Select(x => new { x.Id, x.FileName, x.ContentType, x.Size, x.UploadedAt, x.UploadedById }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(items.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload(Guid projectId, IFormFile file, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return NotFound();
        if (file is null || file.Length == 0 || file.Length > MaxFileSize)
            return BadRequest(new { message = "Choose a file up to 10 MB." });
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (!AllowedTypes.TryGetValue(extension, out var expectedType) ||
            !string.Equals(file.ContentType, expectedType, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "This file type is not allowed." });

        await using var input = file.OpenReadStream();
        var header = new byte[8];
        var read = await input.ReadAsync(header, cancellationToken);
        input.Position = 0;
        if (!HasValidSignature(extension, header, read))
            return BadRequest(new { message = "The file contents do not match the selected type." });

        var storedPath = await storage.SaveAsync(input, cancellationToken);
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await storage.DeleteAsync(storedPath, cancellationToken);
            return Unauthorized();
        }
        var entity = new ProjectFile
        {
            ProjectId = projectId, ClientId = project.ClientId, FileName = Path.GetFileName(file.FileName),
            StoredPath = storedPath, ContentType = expectedType, Size = file.Length, UploadedById = userId
        };
        db.ProjectFiles.Add(entity);
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = projectId, ClientId = project.ClientId, ActorId = userId,
            Action = "File uploaded", Metadata = entity.FileName
        });
        await AddWorkspaceNotificationsAsync(project.ClientId, userId, $"File shared: {entity.FileName}", cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(storedPath, cancellationToken);
            throw;
        }
        return CreatedAtAction(nameof(List), new { projectId }, new { entity.Id, entity.FileName });
    }

    [HttpPatch("{fileId:guid}")]
    public async Task<IActionResult> Rename(Guid projectId, Guid fileId, RenameFileRequest request, CancellationToken cancellationToken)
    {
        var file = await db.ProjectFiles.SingleOrDefaultAsync(
            x => x.Id == fileId && x.ProjectId == projectId, cancellationToken);
        if (file is null) return NotFound();
        if (!CanManageFile(file.UploadedById)) return Forbid();
        var fileName = Path.GetFileName(request.FileName?.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
            !AllowedTypes.TryGetValue(Path.GetExtension(fileName), out var expectedType) ||
            !string.Equals(expectedType, file.ContentType, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Provide a valid file name with its original file type." });

        var previousName = file.FileName;
        file.FileName = fileName;
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = projectId, ClientId = file.ClientId, ActorId = GetActorId(),
            Action = "File renamed", Metadata = $"{previousName} to {fileName}"
        });
        var actorId = GetActorId();
        await AddWorkspaceNotificationsAsync(file.ClientId, actorId, $"File renamed: {fileName}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.ProjectFiles.SingleOrDefaultAsync(
            x => x.Id == fileId && x.ProjectId == projectId, cancellationToken);
        if (file is null) return NotFound();
        if (!CanManageFile(file.UploadedById)) return Forbid();
        var storedPath = file.StoredPath;
        db.ProjectFiles.Remove(file);
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = projectId, ClientId = file.ClientId, ActorId = GetActorId(),
            Action = "File deleted", Metadata = file.FileName
        });
        var actorId = GetActorId();
        await AddWorkspaceNotificationsAsync(file.ClientId, actorId, $"File removed: {file.FileName}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(storedPath, cancellationToken);
        return NoContent();
    }

    [HttpGet("{fileId:guid}")]
    public async Task<IActionResult> Download(Guid projectId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == fileId && x.ProjectId == projectId, cancellationToken);
        if (file is null) return NotFound();
        try
        {
            var stream = await storage.OpenReadAsync(file.StoredPath, cancellationToken);
            return File(stream, file.ContentType, file.FileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    private static bool HasValidSignature(string extension, byte[] header, int read) => extension.ToLowerInvariant() switch
    {
        ".pdf" => read >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
        ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
        ".txt" => read >= 0,
        _ => false
    };

    private bool CanManageFile(Guid uploadedById) =>
        User.IsInRole("Admin") || Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) && userId == uploadedById;

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated user identifier is missing.");

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

    public sealed record RenameFileRequest(string FileName);
}
