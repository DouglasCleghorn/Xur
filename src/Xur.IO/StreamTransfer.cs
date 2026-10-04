using TeeForge.Broadcasting;
using TeeForge.Hashing;

namespace Xur.IO;

public sealed class SourceReadException(string message, Exception inner) : IOException(message, inner);

public sealed record TransferReceipt(long Bytes, string Sha256);

/// <summary>Copy and hash the same bytes in one pass, without taking ownership of caller streams.</summary>
public static class StreamTransfer
{
    public static async Task<TransferReceipt> Copy(Stream source, Stream destination, long limit,
        TimeSpan? idleTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        using var bounded = new BoundedReadStream(source, limit, idleTimeout);
        TeeHashResults hashes;
        try { hashes = await bounded.CopyToAsync(TeeHashAlgorithm.SHA256, destination, cancellationToken: cancellationToken); }
        catch (AggregateException error)
        {
            // TeeForge reports broadcast failures as aggregates. Preserve the
            // source/destination distinction so only interrupted reads retry.
            cancellationToken.ThrowIfCancellationRequested();
            var failures = error.Flatten().InnerExceptions.Where(e => e is not OperationCanceledException).Distinct().ToArray();
            if (failures.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new IOException("Transfer failed", error);
        }
        if (!hashes.IsComplete) throw new IOException("Transfer did not reach source EOF");
        return new(bounded.BytesRead, hashes[TeeHashAlgorithm.SHA256].Hex.ToLowerInvariant());
    }

    sealed class BoundedReadStream(Stream inner, long limit, TimeSpan? idleTimeout) : Stream
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            // Read one extra byte to detect overlong chunked responses instead of silently truncating.
            var remaining = limit - BytesRead;
            var allowed = remaining < buffer.Length ? (int)remaining + 1 : buffer.Length;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (idleTimeout is { } timeout) deadline.CancelAfter(timeout);
            int count;
            try { count = await inner.ReadAsync(buffer[..allowed], deadline.Token); }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            { throw new SourceReadException("Source read timed out", error); }
            catch (Exception error) when (error is IOException or HttpRequestException)
            { throw new SourceReadException("Source read failed", error); }
            BytesRead += count;
            if (BytesRead > limit) throw new InvalidDataException("Download exceeds allowed size");
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
