using System.Net.Mail;
using System.Security.Claims;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Client")]
[Route("api/me/billing")]
public sealed class ClientBillingController(ClientHubDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return NotFound();
        var canEdit = await db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == userId && user.ClientRole == ClientRole.Owner, cancellationToken);
        return Ok(new { client.LegalName, client.TaxId, client.BillingEmails, client.BillingAddress, canEdit });
    }

    [HttpPut]
    public async Task<IActionResult> Update(BillingDetailsRequest request, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return NotFound();
        var isOwner = await db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == userId && user.ClientRole == ClientRole.Owner, cancellationToken);
        if (!isOwner) return Forbid();
        if (!IsValid(request))
            return BadRequest(new { message = "Check the billing name, tax ID, email addresses, and address lengths." });
        client.LegalName = Clean(request.LegalName);
        client.TaxId = Clean(request.TaxId);
        client.BillingEmails = Clean(request.BillingEmails);
        client.BillingAddress = Clean(request.BillingAddress);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<Client?> GetClientAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(User.FindFirstValue("client_id"), out var clientId))
            return null;
        if (!await db.Users.AnyAsync(user => user.Id == userId && user.ClientId == clientId, cancellationToken))
            return null;
        return await db.Clients.SingleOrDefaultAsync(client => client.Id == clientId, cancellationToken);
    }

    private static bool IsValid(BillingDetailsRequest request)
    {
        if (request.LegalName?.Length > 160 || request.TaxId?.Length > 80 ||
            request.BillingEmails?.Length > 500 || request.BillingAddress?.Length > 1000)
            return false;
        if (string.IsNullOrWhiteSpace(request.BillingEmails)) return true;
        var emails = request.BillingEmails.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return emails.Length <= 5 && emails.All(email =>
            MailAddress.TryCreate(email, out var address) && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed record BillingDetailsRequest(string? LegalName, string? TaxId, string? BillingEmails, string? BillingAddress);
}
