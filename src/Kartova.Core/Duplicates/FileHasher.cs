using System.Buffers;
using System.IO.Hashing;

namespace Kartova.Core.Duplicates;

/// <summary>A 128-bit content signature.</summary>
public readonly record struct ContentHash(ulong Low, ulong High)
{
    public static readonly ContentHash Zero = default;

    public bool IsZero => Low == 0 && High == 0;

    public override string ToString() => $"{High:x16}{Low:x16}";
}

/// <summary>
/// Reads file content and reduces it to a comparable signature.
/// </summary>
/// <remarks>
/// XxHash128 rather than a cryptographic hash: the job here is telling files apart, not
/// resisting an adversary, and it runs several times faster over the same bytes — which is
/// the whole cost of duplicate detection. At 128 bits an accidental collision is not a
/// practical concern, and callers that want certainty can verify byte for byte afterwards.
/// </remarks>
public static class FileHasher
{
    /// <summary>
    /// Bytes read for the cheap screening pass. Files that differ usually differ early —
    /// headers, magic numbers, embedded metadata — so a small prefix eliminates most
    /// same-size candidates without reading them whole.
    /// </summary>
    public const int ScreenLength = 16 * 1024;

    private const int BufferSize = 1 << 20;

    /// <summary>Hashes the first <see cref="ScreenLength"/> bytes.</summary>
    public static ContentHash HashPrefix(string path, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ScreenLength);
        try
        {
            using var stream = OpenRead(path);
            var read = ReadAtLeast(stream, buffer.AsSpan(0, ScreenLength), cancellationToken);

            var hash = new XxHash128();
            hash.Append(buffer.AsSpan(0, read));
            return Finish(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hashes the whole file, reporting bytes read as it goes.</summary>
    public static ContentHash HashFile(
        string path, Action<int>? onBytesRead = null, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var stream = OpenRead(path);
            var hash = new XxHash128();

            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.Append(buffer.AsSpan(0, read));
                onBytesRead?.Invoke(read);
            }

            return Finish(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Combines a directory's child signatures into one, order-sensitively.</summary>
    /// <remarks>
    /// Callers feed children in a fixed order — sorted by name — so two directories holding
    /// the same entries hash alike regardless of the order the filesystem reported them.
    /// Names are folded in as well as content, since two folders with identical bytes under
    /// different filenames are not interchangeable.
    /// </remarks>
    public static ContentHash CombineDirectory(IEnumerable<(string Name, ContentHash Hash)> children)
    {
        var hash = new XxHash128();
        Span<byte> scratch = stackalloc byte[16];

        foreach (var (name, child) in children)
        {
            hash.Append(System.Text.Encoding.UTF8.GetBytes(name));

            BitConverter.TryWriteBytes(scratch[..8], child.Low);
            BitConverter.TryWriteBytes(scratch[8..], child.High);
            hash.Append(scratch);
        }

        return Finish(hash);
    }

    /// <summary>Compares two files byte for byte.</summary>
    public static bool ContentsEqual(string left, string right, CancellationToken cancellationToken = default)
    {
        var a = ArrayPool<byte>.Shared.Rent(BufferSize);
        var b = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var leftStream = OpenRead(left);
            using var rightStream = OpenRead(right);

            if (leftStream.Length != rightStream.Length) return false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var readA = ReadAtLeast(leftStream, a.AsSpan(0, BufferSize), cancellationToken);
                var readB = ReadAtLeast(rightStream, b.AsSpan(0, BufferSize), cancellationToken);

                if (readA != readB) return false;
                if (readA == 0) return true;
                if (!a.AsSpan(0, readA).SequenceEqual(b.AsSpan(0, readB))) return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Share everything: a file being written elsewhere should not fail the scan.
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 0, // we buffer ourselves
        });

    /// <summary>Fills the span unless the stream ends first, since Read may return short.</summary>
    private static int ReadAtLeast(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static ContentHash Finish(XxHash128 hash)
    {
        Span<byte> digest = stackalloc byte[16];
        hash.GetCurrentHash(digest);
        return new ContentHash(BitConverter.ToUInt64(digest[..8]), BitConverter.ToUInt64(digest[8..]));
    }
}
