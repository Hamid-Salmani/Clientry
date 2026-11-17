using ClientHub.Domain;
using ClientHub.Application;
using ClientHub.Api.Services;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public sealed class AdminController(ClientHubDbContext db, IEmailSender emailSender, IConfiguration configuration) : ControllerBase
{
    [HttpPost("clients/{clientId:guid}/invitations")]
    public async Task<IActionResult> InviteClientUser(Guid clientId, InvitationRequest request, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        if (!client.IsActive) return Conflict(new { message = "Invitations cannot be created for an inactive client." });
        if (!TryNormalizeEmail(request.Email, out var email) ||
            string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 120)
            return BadRequest(new { message = "A valid email and name of at most 120 characters are required." });
        if (await db.Users.AnyAsync(x => x.Email.ToLower() == email, cancellationToken))
            return Conflict(new { message = "An account with this email already exists." });

        var now = DateTime.UtcNow;
        var pending = await db.Invitations.SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.Email.ToLower() == email && !x.AcceptedAt.HasValue &&
            !x.RevokedAt.HasValue && x.ExpiresAt > now, cancellationToken);
        if (pending is not null)
            return Conflict(new { message = "A pending invitation already exists for this email." });

        var token = TokenSecurity.CreateToken();
        var invitation = new Invitation
        {
            Email = email,
            FullName = request.FullName.Trim(),
            TokenHash = TokenSecurity.HashToken(token),
            ClientId = clientId,
            ExpiresAt = now.AddDays(7)
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        await SendInvitationAsync(invitation, client.CompanyName, token, cancellationToken);
        return CreatedAtAction(nameof(ListInvitations), new { clientId }, new { invitation.Id, invitation.Email, invitation.FullName, invitation.ExpiresAt });
    }

    [HttpGet("clients/{clientId:guid}/invitations")]
    public async Task<IActionResult> ListInvitations(Guid clientId, CancellationToken cancellationToken)
    {
        if (!await db.Clients.AnyAsync(x => x.Id == clientId, cancellationToken)) return NotFound();
        var now = DateTime.UtcNow;
        var invitations = await db.Invitations.AsNoTracking()
            .Where(x => x.ClientId == clientId && !x.AcceptedAt.HasValue && !x.RevokedAt.HasValue && x.ExpiresAt > now)
            .OrderBy(x => x.Email)
            .Select(x => new { x.Id, x.Email, x.FullName, x.ExpiresAt })
            .ToListAsync(cancellationToken);
        return Ok(invitations);
    }

    [HttpPost("clients/{clientId:guid}/invitations/{invitationId:guid}/resend")]
    public async Task<IActionResult> ResendInvitation(Guid clientId, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(x =>
            x.Id == invitationId && x.ClientId == clientId, cancellationToken);
        if (invitation is null) return NotFound();
        if (invitation.AcceptedAt.HasValue || invitation.RevokedAt.HasValue)
            return Conflict(new { message = "Only pending invitations can be resent." });

        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        if (!client.IsActive) return Conflict(new { message = "Invitations cannot be resent for an inactive client." });
        var token = TokenSecurity.CreateToken();
        invitation.TokenHash = TokenSecurity.HashToken(token);
        invitation.ExpiresAt = DateTime.UtcNow.AddDays(7);
        await db.SaveChangesAsync(cancellationToken);
        await SendInvitationAsync(invitation, client.CompanyName, token, cancellationToken);
        return NoContent();
    }

    [HttpDelete("clients/{clientId:guid}/invitations/{invitationId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid clientId, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(x =>
            x.Id == invitationId && x.ClientId == clientId, cancellationToken);
        if (invitation is null) return NotFound();
        if (invitation.AcceptedAt.HasValue || invitation.RevokedAt.HasValue)
            return Conflict(new { message = "Only pending invitations can be revoked." });
        invitation.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("clients")]
    public async Task<IActionResult> Clients(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = "companyName",
        [FromQuery] bool descending = false,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Clients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(client =>
                client.CompanyName.ToLower().Contains(term) ||
                client.Notes.ToLower().Contains(term) ||
                client.Users.Any(user => user.FullName.ToLower().Contains(term) || user.Email.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var ordered = (sortBy?.ToLowerInvariant(), descending) switch
        {
            ("projectcount", false) => query.OrderBy(client => client.Projects.Count(project => project.Status != ProjectStatus.Archived)),
            ("projectcount", true) => query.OrderByDescending(client => client.Projects.Count(project => project.Status != ProjectStatus.Archived)),
            ("status", false) => query.OrderBy(client => client.IsActive),
            ("status", true) => query.OrderByDescending(client => client.IsActive),
            (_, true) => query.OrderByDescending(client => client.CompanyName),
            _ => query.OrderBy(client => client.CompanyName)
        };
        var clients = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(client => new
            {
                client.Id,
                client.CompanyName,
                contact = client.Users.Where(user => user.IsActive).OrderBy(user => user.FullName)
                    .Select(user => user.FullName).FirstOrDefault(),
                activeProjects = client.Projects.Count(project => project.Status != ProjectStatus.Archived),
                client.IsActive
            }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(clients.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost("clients")]
    public async Task<IActionResult> CreateClient(ClientRequest request, CancellationToken cancellationToken)
    {
        if (!TryValidateClient(request, out var companyName, out var notes, out var error))
            return BadRequest(new { message = error });
        var client = new Client { CompanyName = companyName, Notes = notes };
        db.Clients.Add(client);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(ClientDetails), new { clientId = client.Id }, new { client.Id });
    }

    [HttpGet("clients/{clientId:guid}")]
    public async Task<IActionResult> ClientDetails(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().Include(x => x.Users)
            .SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        var outstandingBalance = await db.InvoiceLineItems
            .Where(line => line.Invoice!.ClientId == clientId &&
                (line.Invoice.Status == InvoiceStatus.Sent || line.Invoice.Status == InvoiceStatus.Overdue))
            .SumAsync(line => (decimal?)(line.Quantity * line.UnitPrice), cancellationToken) ?? 0m;
        var lifetimeRevenue = await db.Payments
            .Where(payment => payment.Invoice!.ClientId == clientId && payment.Status == "Succeeded")
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
        return Ok(new
        {
            client.Id,
            client.CompanyName,
            client.Notes,
            client.IsActive,
            projectCount = await db.Projects.CountAsync(project => project.ClientId == clientId, cancellationToken),
            outstandingBalance,
            lifetimeRevenue,
            users = client.Users.OrderBy(user => user.FullName).Select(user => new
            {
                user.Id, user.FullName, user.Email, user.IsActive
            })
        });
    }

    [HttpPut("clients/{clientId:guid}")]
    public async Task<IActionResult> UpdateClient(Guid clientId, ClientRequest request, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        if (!TryValidateClient(request, out var companyName, out var notes, out var error))
            return BadRequest(new { message = error });
        client.CompanyName = companyName;
        client.Notes = notes;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("clients/{clientId:guid}/status")]
    public async Task<IActionResult> SetClientStatus(Guid clientId, SetActiveRequest request, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        client.IsActive = request.IsActive;
        if (!client.IsActive)
        {
            var userIds = db.Users.Where(user => user.ClientId == clientId).Select(user => user.Id);
            var refreshTokens = await db.RefreshTokens
                .Where(token => userIds.Contains(token.UserId) && !token.RevokedAt.HasValue)
                .ToListAsync(cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var refreshToken in refreshTokens) refreshToken.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("clients/{clientId:guid}/users/{userId:guid}/status")]
    public async Task<IActionResult> SetClientUserStatus(Guid clientId, Guid userId, SetActiveRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x =>
            x.Id == userId && x.ClientId == clientId && x.Role == UserRole.Client, cancellationToken);
        if (user is null) return NotFound();
        user.IsActive = request.IsActive;
        if (!user.IsActive)
        {
            var now = DateTime.UtcNow;
            var refreshTokens = await db.RefreshTokens.Where(x => x.UserId == userId && !x.RevokedAt.HasValue)
                .ToListAsync(cancellationToken);
            foreach (var refreshToken in refreshTokens) refreshToken.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
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

    private async Task SendInvitationAsync(Invitation invitation, string clientName, string token, CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
        var inviteUrl = $"{baseUrl}/accept-invite/{Uri.EscapeDataString(token)}";
        await emailSender.SendAsync(invitation.Email, $"You're invited to {clientName}",
            $"<p>Hello {WebUtility.HtmlEncode(invitation.FullName)},</p><p>You have been invited to join {WebUtility.HtmlEncode(clientName)} on Clientry.</p><p><a href=\"{WebUtility.HtmlEncode(inviteUrl)}\">Accept invitation</a></p><p>This link expires in seven days.</p>",
            null, cancellationToken);
    }

    private static bool TryNormalizeEmail(string? value, out string email)
    {
        email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
            string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateClient(ClientRequest request, out string companyName, out string notes, out string error)
    {
        companyName = request.CompanyName?.Trim() ?? string.Empty;
        notes = request.Notes?.Trim() ?? string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(companyName) || companyName.Length > 160 || notes.Length > 4000)
        {
            error = "Company name is required (max 160 characters) and notes must be at most 4,000 characters.";
            return false;
        }
        return true;
    }

    public sealed record ClientRequest(string CompanyName, string Notes);
    public sealed record SetActiveRequest(bool IsActive);
    public sealed record InvitationRequest(string Email, string FullName);
}
