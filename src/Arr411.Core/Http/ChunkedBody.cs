using System.Buffers;
using System.Text;

namespace Arr411.Core.Http;

/// <summary>
/// The bytes of a response as they arrive, kept in pooled blocks of 64 KB and turned into text once the end is there. A buffer that doubles as it fills
/// (a MemoryStream) leaves a trail of ever bigger arrays, each of them in the large object heap: for the 30 MB of a big qBittorrent library that is
/// over 100 MB of garbage, which stays until the next full collection.
/// </summary>
internal sealed class ChunkedBody : IDisposable
{
    public const int BlockSize = 64 * 1024;

    // Characters the decoder writes at a time while it only counts; room for a surrogate pair is all that progress needs.
    private const int ScratchLength = 1024;

    private readonly List<byte[]> _blocks = [];
    private int _lastLength = BlockSize;

    public long Length { get; private set; }

    /// <summary>Room for the next bytes to arrive; <see cref="Advance"/> tells how many did.</summary>
    public Memory<byte> Free()
    {
        if (_lastLength == BlockSize)
        {
            _blocks.Add(ArrayPool<byte>.Shared.Rent(BlockSize));
            _lastLength = 0;
        }

        return _blocks[^1].AsMemory(_lastLength, BlockSize - _lastLength);
    }

    public void Advance(int count)
    {
        _lastLength += count;
        Length += count;
    }

    /// <summary>The body as UTF-8 text: a character cut between two blocks is put back together, and invalid bytes become U+FFFD.</summary>
    public string Decode()
    {
        // The block that Free handed out for a read that found the end of the response is empty.
        var blocks = _lastLength == 0 ? _blocks.Count - 1 : _blocks.Count;
        if (blocks == 0) return "";

        // One pass to size the text and one to fill it, so that the text is the only big thing allocated: the framework's way of decoding several
        // segments stores the pieces in arrays rented from the shared pool first, which is as much again.
        var length = Convert(blocks, default);
        return string.Create(length, (this, blocks), static (to, state) => state.Item1.Convert(state.blocks, to));
    }

    public void Dispose()
    {
        foreach (var block in _blocks) ArrayPool<byte>.Shared.Return(block);
        _blocks.Clear();
    }

    /// <summary>Decodes the blocks into <paramref name="to"/>, or only counts the characters when it is empty.</summary>
    private int Convert(int blocks, Span<char> to)
    {
        var fill = !to.IsEmpty;
        Span<char> scratch = stackalloc char[ScratchLength];
        var decoder = Encoding.UTF8.GetDecoder();
        var total = 0;
        for (var i = 0; i < blocks; i++)
        {
            var last = i == blocks - 1;
            var bytes = _blocks[i].AsSpan(0, last ? _lastLength : BlockSize);
            bool completed;
            do
            {
                decoder.Convert(bytes, fill ? to[total..] : scratch, last, out var used, out var written, out completed);
                bytes = bytes[used..];
                total += written;
            }
            while (!completed);
        }

        return total;
    }
}
