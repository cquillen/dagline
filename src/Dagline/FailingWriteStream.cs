using System.Text;

namespace Dagline;

/// <summary>
/// The one internal test hook: a journal's stream that passes every write and flush through to the real file, except the first
/// write whose text <c>picksWrite</c> picks, which throws an <see cref="IOException"/> with <c>message</c> and writes nothing.
/// <see cref="Journal.WithFailingWrite"/> puts it under one journal, for the shared test support alone, and nothing else uses it.
/// </summary>
/// <remarks>
/// <see cref="JournalFile.Append"/> makes one write per call, carrying whole lines, so the predicate sees each append's lines
/// together. Writes after the failed one pass through again, so that a test reading the file back sees any write the journal's
/// guard on writing nothing after a failure let through.
/// </remarks>
internal sealed class FailingWriteStream(Stream inner, Func<string, bool> picksWrite, string message) : Stream
{
    private bool _failed;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_failed && picksWrite(Encoding.UTF8.GetString(buffer)))
        {
            _failed = true;
            throw new IOException(message);
        }

        inner.Write(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
