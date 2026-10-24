using TradingBot.Core;

namespace TradingBot.Infrastructure;

public sealed class MultiSourceDatasetManifestStore
{
    private readonly string manifestPath;

    public MultiSourceDatasetManifestStore(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        this.manifestPath = Path.GetFullPath(manifestPath);
    }

    public async Task PersistAsync(MultiSourceDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string? directory = Path.GetDirectoryName(manifestPath);
        if (directory is null)
        {
            throw new InvalidOperationException("Manifest path must name a directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                CoreJsonSerializer.Serialize(manifest));
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<MultiSourceDatasetManifest> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        return CoreJsonSerializer.Deserialize<MultiSourceDatasetManifest>(json);
    }
}
