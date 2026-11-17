using System.Security.Claims;
using System.Net;
using System.Net.Mail;
using ClientHub.Application;
using ClientHub.Api.Services;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Client")]
[Route("api/me/team")]
public sealed class ClientTeamController(
    ClientHubDbContext db,
    IEmailSender emailSender,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!await IsOwnerAsync(cancellationToken)) return Forbid();

        var members = await db.Users.AsNoTracking()
            .Where(user => user.ClientId == client.Id && user.Role == UserRole.Client)
            .OrderBy(user => user.FullName)
            .Select(user => new { user.Id, user.FullName, user.Email, user.IsActive, clientRole = user.ClientRole.ToString() })
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var invitations = await db.Invitations.AsNoTracking()
            .Where(invitation => invitation.ClientId == client.Id && !invitation.AcceptedAt.HasValue &&
                !invitation.RevokedAt.HasValue && invitation.ExpiresAt > now)
            .OrderBy(invitation => invitation.Email)
            .Select(invitation => new { invitation.Id, invitation.FullName, invitation.Email, invitation.ExpiresAt })
            .ToListAsync(cancellationToken);
        return Ok(new { members, invitations });
    }

    [HttpPost("invitations")]
    public async Task<IActionResult> Invite(InvitationRequest request, CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!await IsOwnerAsync(cancellationToken)) return Forbid();
        if (!client.IsActive) return Conflict(new { message = "Invitations cannot be created for an inactive client." });
        if (!TryNormalizeEmail(request.Email, out var email) ||
            string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 120)
            return BadRequest(new { message = "A valid email and name of at most 120 characters are required." });
        if (await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Email.ToLower() == email, cancellationToken))
            return Conflict(new { message = "An account with this email already exists." });

        var now = DateTime.UtcNow;
        if (await db.Invitations.AnyAsync(invitation =>
                invitation.ClientId == client.Id && invitation.Email.ToLower() == email &&
                !invitation.AcceptedAt.HasValue && !invitation.RevokedAt.HasValue && invitation.ExpiresAt > now,
                cancellationToken))
            return Conflict(new { message = "A pending invitation already exists for this email." });

        var token = TokenSecurity.CreateToken();
        var invitation = new Invitation
        {
            Email = email,
            FullName = request.FullName.Trim(),
            TokenHash = TokenSecurity.HashToken(token),
            ClientId = client.Id,
            ExpiresAt = now.AddDays(7)
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        var baseUrl = (configuration["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
        var inviteUrl = $"{baseUrl}/accept-invite/{Uri.EscapeDataString(token)}";
        await emailSender.SendAsync(email, $"You're invited to {client.CompanyName}",
            $"<p>Hello {WebUtility.HtmlEncode(invitation.FullName)},</p><p>You have been invited to join {WebUtility.HtmlEncode(client.CompanyName)} on Clientry.</p><p><a href=\"{WebUtility.HtmlEncode(inviteUrl)}\">Accept invitation</a></p><p>This link expires in seven days.</p>",
            null, cancellationToken);
        return CreatedAtAction(nameof(List), new { }, new { invitation.Id, invitation.Email, invitation.FullName, invitation.ExpiresAt });
    }

    [HttpPatch("{userId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid userId, CancellationToken cancellationToken)
    {
        var client = await GetOwnedClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!await IsOwnerAsync(cancellationToken)) return Forbid();
        var member = await db.Users.SingleOrDefaultAsync(user =>
            user.Id == userId && user.ClientId == client.Id && user.Role == UserRole.Client, cancellationToken);
        if (member is null) return NotFound();
        if (member.ClientRole == ClientRole.Owner)
            return Conflict(new { message = "The client owner cannot be deactivated from the team page." });
        if (!member.IsActive) return NoContent();

        member.IsActive = false;
        var refreshTokens = await db.RefreshTokens
            .Where(token => token.UserId == member.Id && !token.RevokedAt.HasValue)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var refreshToken in refreshTokens) refreshToken.RevokedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<Client?> GetOwnedClientAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("client_id"), out var clientId) ||
            !Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        if (!await db.Users.AnyAsync(user => user.Id == userId && user.ClientId == clientId, cancellationToken))
            return null;
        return await db.Clients.SingleOrDefaultAsync(client => client.Id == clientId, cancellationToken);
    }

    private async Task<bool> IsOwnerAsync(CancellationToken cancellationToken) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) &&
        await db.Users.AnyAsync(user => user.Id == userId && user.ClientRole == ClientRole.Owner, cancellationToken);

    private static bool TryNormalizeEmail(string? value, out string email)
    {
        email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return email.Length <= 254 && MailAddress.TryCreate(email, out var address) &&
            string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record InvitationRequest(string Email, string FullName);
}
