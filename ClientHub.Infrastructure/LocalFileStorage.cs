using ClientHub.Application;
using Microsoft.Extensions.Configuration;

namespace ClientHub.Infrastructure;

public sealed class LocalFileStorage(IConfiguration configuration) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(configuration["Storage:Path"] ?? "App_Data/files");

    public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var id = $"{Guid.NewGuid():N}.bin";
        await using var target = new FileStream(
            Path.Combine(_root, id), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await content.CopyToAsync(target, cancellationToken);
        return id;
    }

    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken)
    {
        var path = Resolve(storedPath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storedPath, CancellationToken cancellationToken)
    {
        File.Delete(Resolve(storedPath));
        return Task.CompletedTask;
    }

    private string Resolve(string storedPath)
    {
        if (Path.GetFileName(storedPath) != storedPath)
            throw new InvalidOperationException("Invalid storage identifier.");
        var path = Path.GetFullPath(Path.Combine(_root, storedPath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid storage identifier.");
        return path;
    }
}
