namespace AppUpdateServer.Services;

/// <summary>
/// Read-only pass-through stream that reports how many bytes have been read.
/// FluentInputFile only reports 50% before handing over the stream and 100%
/// after, so the actual transfer progress has to be measured here.
/// </summary>
public sealed class ProgressReportingStream(Stream inner, Action<long> onBytesRead) : Stream
{
    private long _totalRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => _totalRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Report(read);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        Report(read);
        return read;
    }

    private void Report(int read)
    {
        if (read <= 0)
            return;

        _totalRead += read;
        onBytesRead(_totalRead);
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
