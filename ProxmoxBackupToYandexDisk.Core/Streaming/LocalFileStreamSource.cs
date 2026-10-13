namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// IStreamSource implementation for local files.
/// </summary>
public sealed class LocalFileStreamSource : IStreamSource
{
    private readonly string _filePath;
    private readonly FileInfo _fileInfo;

    public LocalFileStreamSource(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _fileInfo = new FileInfo(filePath);

        if (!_fileInfo.Exists)
            throw new FileNotFoundException("File not found", filePath);
    }

    public long TotalLength => _fileInfo.Length;

    public string FileName => _fileInfo.Name;

    public Task<Stream> OpenAsync(long offset = 0, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920, // 80 KB buffer
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream>(stream);
    }
}
