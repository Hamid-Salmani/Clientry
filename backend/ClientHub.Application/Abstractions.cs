namespace ClientHub.Application;

public interface IRequestScope
{
    Guid? ClientId { get; }
    Guid? UserId { get; }
    bool IsClient { get; }
}

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken);
    Task DeleteAsync(string storedPath, CancellationToken cancellationToken);
}

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string htmlBody, IReadOnlyDictionary<string, byte[]>? attachments, CancellationToken cancellationToken);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
