using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Infrastructure;

/// <summary>
/// Write-only, non-seekable stream for a response written by someone else (the guider's archive writer). It
/// collects the data in blocks and opens the response (status and headers) only with the first full block or on
/// <see cref="CompleteAsync"/>, so that a writer that finds nothing to write or fails early still allows a JSON
/// error. Flush and Dispose leave the pending block and the response alone; CompleteAsync sends the rest, the web
/// server closes the response.
/// </summary>
internal sealed class DeferredResponseStream : Stream
{
    private readonly Func<Stream> open;
    private readonly byte[] block;
    private int pending;
    private Stream inner;

    public DeferredResponseStream(Func<Stream> open, int blockSize)
    {
        this.open = open;
        block = new byte[blockSize];
    }

    /// <summary>True once the response was opened: its status and headers can no longer change.</summary>
    public bool Started => inner != null;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int n = Append(buffer);
            buffer = buffer[n..];
            if (pending == block.Length) SendBlock();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0)
        {
            int n = Append(buffer.Span);
            buffer = buffer[n..];
            if (pending == block.Length) await SendBlockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends the pending data, opening the response if nothing was sent yet; does nothing without any data.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (pending > 0) await SendBlockAsync(cancellationToken).ConfigureAwait(false);
        if (inner != null) await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private int Append(ReadOnlySpan<byte> data)
    {
        int n = Math.Min(data.Length, block.Length - pending);
        data[..n].CopyTo(block.AsSpan(pending));
        pending += n;
        return n;
    }

    private void SendBlock()
    {
        inner ??= open();
        inner.Write(block, 0, pending);
        pending = 0;
    }

    private async Task SendBlockAsync(CancellationToken cancellationToken)
    {
        inner ??= open();
        await inner.WriteAsync(block.AsMemory(0, pending), cancellationToken).ConfigureAwait(false);
        pending = 0;
    }

    // The pending block goes out when it is full or on CompleteAsync.
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
