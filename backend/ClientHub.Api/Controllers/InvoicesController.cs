using ClientHub.Api.Services;
using ClientHub.Application;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using System.Text;
using DomainInvoice = ClientHub.Domain.Invoice;
using DomainInvoiceLineItem = ClientHub.Domain.InvoiceLineItem;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/invoices")]
public sealed class InvoicesController(ClientHubDbContext db, InvoicePdfService pdf) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] Guid? clientId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = "issueDate",
        [FromQuery] bool descending = true,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Invoices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(invoice => invoice.Number.ToLower().Contains(term) ||
                invoice.Client!.CompanyName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<InvoiceStatus>(status, true, out var invoiceStatus) || !Enum.IsDefined(invoiceStatus))
                return BadRequest(new { message = "Unknown invoice status." });
            query = query.Where(invoice => invoice.Status == invoiceStatus);
        }
        if (clientId.HasValue && User.IsInRole("Admin"))
            query = query.Where(invoice => invoice.ClientId == clientId.Value);

        var total = await query.CountAsync(cancellationToken);
        var ordered = (sortBy?.ToLowerInvariant(), descending) switch
        {
            ("number", false) => query.OrderBy(invoice => invoice.Number),
            ("number", true) => query.OrderByDescending(invoice => invoice.Number),
            ("duedate", false) => query.OrderBy(invoice => invoice.DueDate),
            ("duedate", true) => query.OrderByDescending(invoice => invoice.DueDate),
            (_, false) => query.OrderBy(invoice => invoice.IssueDate),
            _ => query.OrderByDescending(invoice => invoice.IssueDate)
        };
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var invoices = await ordered.Skip(skip).Take(pageSize).Include(invoice => invoice.Lines)
            .ToListAsync(cancellationToken);
        var items = invoices.Select(invoice => new
        {
            invoice.Id, invoice.ClientId, invoice.Number, invoice.IssueDate, invoice.DueDate, status = invoice.Status.ToString(),
            invoice.Currency, total = invoice.Total,
            lines = invoice.Lines.Select(line => new { line.Description, line.Quantity, line.UnitPrice })
        }).ToArray();
        return Ok(new PagedResult<object>(items.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpGet("{invoiceId:guid}/pdf")]
    public async Task<IActionResult> DownloadPdf(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(x => x.Lines)
            .Include(x => x.Client)
            .FirstOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        var content = pdf.Generate(invoice, invoice.Client?.CompanyName ?? "Client");
        return File(content, "application/pdf", $"{invoice.Number}.pdf");
    }

    [HttpGet("export.csv")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ExportCsv(CancellationToken cancellationToken)
    {
        var invoices = await db.Invoices.AsNoTracking().Include(x => x.Client).Include(x => x.Lines)
            .OrderBy(x => x.Number).ToListAsync(cancellationToken);
        var csv = new StringBuilder("Number,Client,Issue date,Due date,Status,Currency,Total\r\n");
        foreach (var invoice in invoices)
        {
            csv.AppendJoin(',', new[]
            {
                Csv(invoice.Number),
                Csv(invoice.Client?.CompanyName ?? string.Empty),
                Csv(invoice.IssueDate.ToString("yyyy-MM-dd")),
                Csv(invoice.DueDate.ToString("yyyy-MM-dd")),
                Csv(invoice.Status.ToString()),
                Csv(invoice.Currency),
                Csv(invoice.Total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            }).Append("\r\n");
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "invoices.csv");
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidInvoice(request)) return InvalidInvoice();
        if (!await db.Clients.AnyAsync(x => x.Id == request.ClientId, cancellationToken)) return NotFound();
        if (request.ProjectId.HasValue &&
            !await db.Projects.AnyAsync(x => x.Id == request.ProjectId && x.ClientId == request.ClientId, cancellationToken))
            return NotFound();

        if (!string.Equals(request.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only USD invoices are currently supported." });
        if (request.DueDate < DateOnly.FromDateTime(DateTime.UtcNow))
            return BadRequest(new { message = "Due date cannot be before the issue date." });
        var invoice = new DomainInvoice
        {
            ClientId = request.ClientId,
            ProjectId = request.ProjectId,
            Number = $"INV-{DateTime.UtcNow:yyyy}-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            IssueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate = request.DueDate,
            Currency = request.Currency.ToUpperInvariant(),
            Lines = CreateLines(request.Lines)
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(List), new { id = invoice.Id }, new { invoice.Id, invoice.Number });
    }

    [HttpPut("{invoiceId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid invoiceId, CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidInvoice(request)) return InvalidInvoice();
        var invoice = await db.Invoices.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatus.Draft)
            return Conflict(new { message = "Only draft invoices can be edited." });
        if (!await db.Clients.AnyAsync(x => x.Id == request.ClientId, cancellationToken)) return NotFound();
        if (request.ProjectId.HasValue &&
            !await db.Projects.AnyAsync(x => x.Id == request.ProjectId && x.ClientId == request.ClientId, cancellationToken))
            return NotFound();
        if (!string.Equals(request.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only USD invoices are currently supported." });
        if (request.DueDate < invoice.IssueDate)
            return BadRequest(new { message = "Due date cannot be before the issue date." });

        db.InvoiceLineItems.RemoveRange(invoice.Lines);
        invoice.ClientId = request.ClientId;
        invoice.ProjectId = request.ProjectId;
        invoice.DueDate = request.DueDate;
        invoice.Currency = request.Currency.ToUpperInvariant();
        invoice.Lines = CreateLines(request.Lines);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{invoiceId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatus.Draft)
            return Conflict(new { message = "Only draft invoices can be deleted." });
        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{invoiceId:guid}/send")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Send(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (!InvoiceRules.CanTransition(invoice.Status, InvoiceStatus.Sent))
            return Conflict(new { message = "This invoice cannot be sent in its current state." });
        invoice.Status = InvoiceStatus.Sent;
        await AddInvoiceNotificationsAsync(invoice.ClientId, $"Invoice {invoice.Number} is ready for payment.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("{invoiceId:guid}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateStatus(Guid invoiceId, UpdateInvoiceStatusRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<InvoiceStatus>(request.Status, true, out var nextStatus) ||
            !Enum.IsDefined(nextStatus) || nextStatus == InvoiceStatus.Paid)
            return BadRequest(new { message = "Choose a valid invoice status; Paid is set only by verified payment." });
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (!InvoiceRules.CanTransition(invoice.Status, nextStatus))
            return Conflict(new { message = $"An invoice cannot move from {invoice.Status} to {nextStatus}." });
        if (invoice.Status == nextStatus) return NoContent();
        invoice.Status = nextStatus;
        if (nextStatus == InvoiceStatus.Overdue)
            await AddInvoiceNotificationsAsync(invoice.ClientId, $"Invoice {invoice.Number} is overdue.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{invoiceId:guid}/payment-intent")]
    public async Task<IActionResult> CreatePaymentIntent(Guid invoiceId, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (invoice.Status is not (InvoiceStatus.Sent or InvoiceStatus.Overdue))
            return Conflict(new { message = "This invoice is not payable." });
        var secret = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret)) return Problem(statusCode: 503, title: "Payments are not configured.");
        var service = new PaymentIntentService(new StripeClient(secret));
        var intent = await service.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)decimal.Round(invoice.Total * 100, 0, MidpointRounding.AwayFromZero),
            Currency = invoice.Currency.ToLowerInvariant(),
            Metadata = new Dictionary<string, string> { ["invoiceId"] = invoice.Id.ToString() },
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true }
        }, new RequestOptions { IdempotencyKey = $"clienthub-invoice-{invoice.Id}" }, cancellationToken);
        return Ok(new { intent.ClientSecret });
    }

    private static bool IsValidInvoice(CreateInvoiceRequest request) =>
        request.Lines is { Count: > 0 and <= 50 } &&
        request.Lines.All(x => x is not null && !string.IsNullOrWhiteSpace(x.Description) && x.Description.Length <= 240 &&
            x.Quantity > 0 && x.Quantity <= 1_000_000 && x.UnitPrice >= 0 && x.UnitPrice <= 1_000_000_000) &&
        !string.IsNullOrWhiteSpace(request.Currency) && request.Currency.Length == 3;

    private static List<DomainInvoiceLineItem> CreateLines(IEnumerable<InvoiceLineRequest> lines) =>
        lines.Select(x => new DomainInvoiceLineItem
        {
            Description = x.Description.Trim(), Quantity = x.Quantity, UnitPrice = x.UnitPrice
        }).ToList();

    private IActionResult InvalidInvoice() =>
        ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["invoice"] = ["Add valid invoice line items and a three-letter currency code."]
        }));

    private static string Csv(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
            value = $"'{value}";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private async Task AddInvoiceNotificationsAsync(Guid clientId, string message, CancellationToken cancellationToken)
    {
        var recipientIds = await db.Users
            .Where(user => user.ClientId == clientId && user.IsActive &&
                (user.NotificationPreference == null || user.NotificationPreference.InvoiceReminders))
            .Select(user => user.Id).ToListAsync(cancellationToken);
        foreach (var recipientId in recipientIds)
            db.Notifications.Add(new Notification { UserId = recipientId, Type = "Invoice reminder", Message = message });
    }

    public sealed record CreateInvoiceRequest(Guid ClientId, Guid? ProjectId, DateOnly DueDate, string Currency, List<InvoiceLineRequest> Lines);
    public sealed record UpdateInvoiceStatusRequest(string Status);
    public sealed record InvoiceLineRequest(string Description, decimal Quantity, decimal UnitPrice);
}
