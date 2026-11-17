using ClientHub.Application;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace ClientHub.Infrastructure;

public sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendAsync(
        string recipient,
        string subject,
        string htmlBody,
        IReadOnlyDictionary<string, byte[]>? attachments,
        CancellationToken cancellationToken)
    {
        var host = configuration["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("SMTP host is not configured.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(configuration["Smtp:From"] ?? "hello@clientry.local"));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        var body = new BodyBuilder { HtmlBody = htmlBody };
        if (attachments is not null)
        {
            foreach (var (fileName, content) in attachments)
                body.Attachments.Add(fileName, content);
        }
        message.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        var port = configuration.GetValue("Smtp:Port", 25);
        await client.ConnectAsync(host, port, SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);
        var username = configuration["Smtp:Username"];
        var password = configuration["Smtp:Password"];
        if (!string.IsNullOrWhiteSpace(username))
            await client.AuthenticateAsync(username, password ?? string.Empty, cancellationToken);
        await client.SendAsync(FormatOptions.Default, new SmtpSendRequest(message), cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
