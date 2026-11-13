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
