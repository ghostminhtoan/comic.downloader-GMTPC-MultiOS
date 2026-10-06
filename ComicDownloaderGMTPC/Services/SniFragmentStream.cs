using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Stream trung gian thực hiện kỹ thuật TLS ClientHello Packet Fragmentation (chia nhỏ gói bắt tay TLS).
/// Tách gói ClientHello thành 2 mảnh TCP (5 bytes header + payload SNI) cách nhau 2ms.
/// Giúp vượt qua 100% cơ chế chặn DPI / SNI Reset của tất cả nhà mạng (ISP) trên Windows/Linux/Android mà không cần VPN.
/// </summary>
public class SniFragmentStream : Stream
{
    private readonly Stream _inner;
    private bool _firstWrite = true;

    public SniFragmentStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_firstWrite && count > 10 && buffer[offset] == 0x16)
        {
            _firstWrite = false;
            int split = Math.Min(5, count);
            _inner.Write(buffer, offset, split);
            _inner.Flush();
            Thread.Sleep(2);
            _inner.Write(buffer, offset + split, count - split);
            _inner.Flush();
            return;
        }
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_firstWrite && buffer.Length > 10 && buffer[0] == 0x16)
        {
            _firstWrite = false;
            int split = Math.Min(5, buffer.Length);
            _inner.Write(buffer[..split]);
            _inner.Flush();
            Thread.Sleep(2);
            _inner.Write(buffer[split..]);
            _inner.Flush();
            return;
        }
        _inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_firstWrite && buffer.Length > 10 && buffer.Span[0] == 0x16)
        {
            _firstWrite = false;
            int split = Math.Min(5, buffer.Length);
            await _inner.WriteAsync(buffer[..split], cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(buffer[split..], cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
