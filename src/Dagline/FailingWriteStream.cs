using System.Text;

namespace Dagline;

/// <summary>
/// The one internal test hook: a journal's stream that passes every write through to the real file, except the first write for
/// whose text <c>failureOf</c> gives an <see cref="IOException"/>, which throws it and writes nothing.
/// <see cref="Journal.WithFailingWrite"/> puts it under one journal, for the shared test support alone, and nothing else uses it.
/// </summary>
/// <remarks>
/// <see cref="JournalFile.Append"/> makes one write per call, carrying whole lines, so <c>failureOf</c> sees each append's lines
/// together. Writes after the failed one pass through again, so that a test reading the file back sees any write the journal's
/// guard on writing nothing after a failure let through. The flush to the disk does not come through here: the journal file makes
/// it on the file itself.
/// </remarks>
internal sealed class FailingWriteStream(Stream inner, Func<string, IOException?> failureOf) : Stream
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
        if (!_failed && failureOf(Encoding.UTF8.GetString(buffer)) is { } failure)
        {
            _failed = true;
            throw failure;
        }

        inner.Write(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
