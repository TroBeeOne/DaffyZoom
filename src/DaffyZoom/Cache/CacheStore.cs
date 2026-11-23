using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DaffyZoom.Cache;

/// <summary>An entry's file was not what was written: tampered with, truncated, or from another run.</summary>
internal sealed class CacheCorruptException(string message) : Exception(message);

/// <summary>
/// Cached answers on disk: one file per entry, in chunks of up to 1 MB, each compressed and then
/// encrypted with AES-256-GCM under a key that exists only in this process's memory.
/// A restart therefore leaves nothing readable behind, and the
/// directory is emptied when the store opens.
///
/// GCM authenticates as well as encrypts, and each chunk's associated data names its entry, its
/// position and whether it is the last, so a chunk cannot be altered, swapped, reordered or cut off
/// without failing to decrypt. That is the integrity check a hit makes before its first byte.
///
/// File: "DZC1", the entry key (16), the plaintext length (8), the chunk count (4); then per chunk:
/// compressed flag (1), plaintext length (4), ciphertext length (4), nonce (12), tag (16), ciphertext.
/// </summary>
internal sealed class CacheStore
{
    public const int ChunkSize = 1024 * 1024;
    private const int HeaderLength = 4 + 16 + 8 + 4;
    private const int ChunkHeaderLength = 1 + 4 + 4 + 12 + 16;
    private static readonly byte[] Magic = "DZC1"u8.ToArray();

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private long _sequence;

    private CacheStore(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>Opens the cache directory, emptying it first: whatever an earlier run left cannot be read anyway.</summary>
    public static CacheStore Open(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        foreach (string file in System.IO.Directory.EnumerateFiles(directory))
        {
            if (!file.EndsWith(".dzc", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new CacheStore(directory);
    }

    /// <summary>
    /// Writes an entry and returns its path and size on disk. The name is unique per write, so a new
    /// answer for a key never has to replace a file that a hit may still be reading.
    /// </summary>
    public (string Path, long Bytes) Write(CacheKey key, ReadOnlySpan<byte> plaintext)
    {
        string path = Path.Combine(Directory, $"{key}-{Interlocked.Increment(ref _sequence)}.dzc");
        string temporary = path + ".tmp";
        int chunks = Math.Max(1, (plaintext.Length + ChunkSize - 1) / ChunkSize);

        byte[] keyBytes = KeyBytes(key);
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        keyBytes.CopyTo(header, 4);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20), plaintext.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), chunks);

        using var aes = new AesGcm(_key, 16);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                file.Write(header);
                for (int i = 0; i < chunks; i++)
                {
                    var chunk = plaintext.Slice(i * ChunkSize, Math.Min(ChunkSize, plaintext.Length - i * ChunkSize));
                    WriteChunk(file, aes, keyBytes, i, last: i == chunks - 1, chunk);
                }
                file.Flush();
            }
            File.Move(temporary, path);
            return (path, new FileInfo(path).Length);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static void WriteChunk(FileStream file, AesGcm aes, byte[] keyBytes, int index, bool last, ReadOnlySpan<byte> chunk)
    {
        // Rows compress well, often five to ten times; when a chunk does not, it is stored as it is.
        byte[] compressed = Compress(chunk);
        bool useCompressed = compressed.Length < chunk.Length;
        ReadOnlySpan<byte> body = useCompressed ? compressed : chunk;

        byte[] record = ArrayPool<byte>.Shared.Rent(ChunkHeaderLength + body.Length);
        try
        {
            var span = record.AsSpan(0, ChunkHeaderLength + body.Length);
            span[0] = useCompressed ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(span[1..], chunk.Length);
            BinaryPrimitives.WriteInt32LittleEndian(span[5..], body.Length);
            var nonce = span.Slice(9, 12);
            var tag = span.Slice(21, 16);
            RandomNumberGenerator.Fill(nonce);
            aes.Encrypt(nonce, body, span[ChunkHeaderLength..], tag, Associated(keyBytes, index, last, chunk.Length, span[0]));
            file.Write(span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(record);
        }
    }

    /// <summary>
    /// Opens an entry for a hit and checks every chunk before anything is sent - the one read that
    /// may fail without consequence. Entries up to <paramref name="inMemoryLimit"/> are kept
    /// decrypted from that read; larger ones are decrypted again as they are sent.
    /// </summary>
    public async Task<EntryContent> OpenVerifiedAsync(CacheKey key, string path, long expectedLength, long inMemoryLimit,
                                                      CancellationToken cancellationToken)
    {
        bool keep = expectedLength <= inMemoryLimit;
        var kept = keep ? new List<byte[]>() : null;
        long total = 0;

        await foreach (var chunk in ReadAsync(key, path, cancellationToken))
        {
            total += chunk.Length;
            kept?.Add(chunk.ToArray());
        }

        if (total != expectedLength) throw new CacheCorruptException($"the entry holds {total} bytes, not the {expectedLength} written");
        return new EntryContent(this, key, path, total, kept);
    }

    /// <summary>Every chunk of an entry, decrypted and checked, in order.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        CacheKey key, string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        byte[] keyBytes = KeyBytes(key);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true);

        byte[] header = new byte[HeaderLength];
        await ReadExactAsync(file, header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) || !header.AsSpan(4, 16).SequenceEqual(keyBytes))
            throw new CacheCorruptException("not this entry's file");

        long plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20));
        int chunks = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28));
        if (chunks <= 0 || plaintextLength < 0 || chunks > plaintextLength / ChunkSize + 1)
            throw new CacheCorruptException("the entry's header does not add up");

        using var aes = new AesGcm(_key, 16);
        byte[] chunkHeader = new byte[ChunkHeaderLength];
        byte[] body = ArrayPool<byte>.Shared.Rent(ChunkSize + 1024);
        byte[] plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            for (int i = 0; i < chunks; i++)
            {
                await ReadExactAsync(file, chunkHeader, cancellationToken);
                byte flag = chunkHeader[0];
                int plainLength = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(1));
                int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(5));
                if (flag > 1 || plainLength is < 0 or > ChunkSize || bodyLength < 0 || bodyLength > ChunkSize + 1024)
                    throw new CacheCorruptException($"chunk {i}'s header does not add up");

                await ReadExactAsync(file, body.AsMemory(0, bodyLength), cancellationToken);
                byte[] decrypted = ArrayPool<byte>.Shared.Rent(bodyLength);
                try
                {
                    try
                    {
                        aes.Decrypt(chunkHeader.AsSpan(9, 12), body.AsSpan(0, bodyLength), chunkHeader.AsSpan(21, 16),
                                    decrypted.AsSpan(0, bodyLength), Associated(keyBytes, i, i == chunks - 1, plainLength, flag));
                    }
                    catch (AuthenticationTagMismatchException)
                    {
                        throw new CacheCorruptException($"chunk {i} does not authenticate");
                    }

                    if (flag == 1) Decompress(decrypted.AsSpan(0, bodyLength), plain.AsSpan(0, plainLength));
                    else decrypted.AsSpan(0, bodyLength).CopyTo(plain);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(decrypted);
                }

                yield return plain.AsMemory(0, flag == 1 ? plainLength : bodyLength);
            }

            if (file.Position != file.Length) throw new CacheCorruptException("the entry's file is longer than its chunks");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
            ArrayPool<byte>.Shared.Return(plain);
        }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] KeyBytes(CacheKey key)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, key.Value);
        return bytes;
    }

    private static byte[] Associated(byte[] keyBytes, int index, bool last, int plainLength, byte flag)
    {
        byte[] data = new byte[16 + 4 + 1 + 4 + 1];
        keyBytes.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), index);
        data[20] = last ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(21), plainLength);
        data[25] = flag;
        return data;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    private static void Decompress(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = zlib.Read(destination[filled..]);
            if (read == 0) throw new CacheCorruptException("a chunk decompressed short");
            filled += read;
        }
        if (zlib.ReadByte() != -1) throw new CacheCorruptException("a chunk decompressed long");
    }

    private static async ValueTask ReadExactAsync(FileStream file, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await file.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new CacheCorruptException("the entry's file ends early");
        }
    }
}

/// <summary>An entry checked and ready to send: its chunks kept in memory, or read again from the file as it goes.</summary>
internal sealed class EntryContent(CacheStore store, CacheKey key, string path, long length, List<byte[]>? kept)
{
    public long Length { get; } = length;

    public bool InMemory => kept is not null;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ChunksAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (kept is not null)
        {
            foreach (var chunk in kept) yield return chunk;
            yield break;
        }

        await foreach (var chunk in store.ReadAsync(key, path, cancellationToken)) yield return chunk;
    }
}
