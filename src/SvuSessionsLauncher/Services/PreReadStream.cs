using System;
using System.IO;

namespace SvuSessionsLauncher.Services;

internal class PreReadStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _buffer;
    private int _pos;
    private int _count;

    public PreReadStream(Stream inner, byte[] buffer, int count)
    {
        _inner = inner;
        _buffer = buffer;
        _count = count;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos < _count)
        {
            int take = Math.Min(count, _count - _pos);
            Buffer.BlockCopy(_buffer, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }

        return _inner.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_pos < _count)
        {
            int take = Math.Min(count, _count - _pos);
            Buffer.BlockCopy(_buffer, _pos, buffer, offset, take);
            _pos += take;
            return Task.FromResult(take);
        }

        return _inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
