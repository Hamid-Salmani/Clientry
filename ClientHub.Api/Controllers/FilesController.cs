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
    public async Task<IActionResult> List(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) return NotFound();
        return Ok(await db.ProjectFiles.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.UploadedAt)
            .Select(x => new { x.Id, x.FileName, x.ContentType, x.Size, x.UploadedAt }).ToListAsync(cancellationToken));
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
        try { await db.SaveChangesAsync(cancellationToken); }
        catch
        {
            await storage.DeleteAsync(storedPath, cancellationToken);
            throw;
        }
        return CreatedAtAction(nameof(List), new { projectId }, new { entity.Id, entity.FileName });
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
}
