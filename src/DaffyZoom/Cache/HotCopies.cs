using System.Collections.Concurrent;

namespace DaffyZoom.Cache;

/// <summary>
/// The answers being hit, kept decrypted in memory and shared by every hit on them, so that a popular
/// answer is read, decrypted and decompressed once rather than on every hit.
/// A copy is made by the first hit on an entry, with one read of its file that every hit
/// arriving meanwhile waits for, and it is checked to its last byte like any hit's read. It goes when
/// its entry does, or when room is needed for another: the least recently used go first.
///
/// Copies and the reads making them never hold more than --memory-cache-mb between them. An answer
/// larger than a quarter of that, or one that finds no room (the reads in progress have it all), is
/// read for its hit alone, as it would be with no copies at all.
/// </summary>
internal sealed class HotCopies(CacheStore store, long capacityBytes, long privateCopyBytes)
{
    /// <summary>The longest one read of a file may take; a hit gives up on it sooner, at its own deadline.</summary>
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(30);

    private sealed class Copy(CacheEntry entry)
    {
        private readonly TaskCompletionSource<EntryContent> _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _lastUsed = Environment.TickCount64;

        public CacheEntry Entry { get; } = entry;
        public Task<EntryContent> Content => _content.Task;

        /// <summary>What it holds of the capacity: reserved when its read starts, so reads in progress count too.</summary>
        public long Bytes { get; } = entry.Length;

        public long LastUsed => Interlocked.Read(ref _lastUsed);
        public void Touch() => Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);

        public void Loaded(EntryContent content) => _content.TrySetResult(content);
        public void Failed(Exception ex) => _content.TrySetException(ex);
    }

    private readonly ConcurrentDictionary<CacheEntry, Copy> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();
    private long _bytes;

    public long Capacity { get; } = capacityBytes;

    /// <summary>The largest answer kept: a quarter of the capacity, so that one answer cannot push out all the rest.</summary>
    public long MaxCopyBytes => Capacity / 4;

    /// <summary>What the copies, and the reads making them, hold now.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    public int Count => _copies.Count;

    /// <summary>
    /// An entry's content, checked and ready to send: its copy, waited for while it is being made, or
    /// a read of its own when it cannot have one. Throws as a read does (<see cref="CacheCorruptException"/>,
    /// <see cref="IOException"/>), and the entry should go.
    /// </summary>
    public Task<EntryContent> OpenAsync(CacheEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length <= MaxCopyBytes && Share(entry) is { } copy)
        {
            copy.Touch();
            return copy.Content.WaitAsync(cancellationToken);
        }
        return store.OpenVerifiedAsync(entry.Key, entry.Path, entry.Length, privateCopyBytes, cancellationToken);
    }

    /// <summary>The entry's copy, or one started now; null when there is no room for it.</summary>
    private Copy? Share(CacheEntry entry)
    {
        if (_copies.TryGetValue(entry, out var copy)) return copy;
        lock (_gate)
        {
            if (_copies.TryGetValue(entry, out copy)) return copy;
            if (!MakeRoom(entry.Length)) return null;
            copy = new Copy(entry);
            _copies[entry] = copy;
            _bytes += copy.Bytes;
        }
        _ = Task.Run(() => LoadAsync(copy));
        return copy;
    }

    /// <summary>Drops the least recently used finished copies until <paramref name="bytes"/> more fit. Under the lock.</summary>
    private bool MakeRoom(long bytes)
    {
        if (_bytes + bytes <= Capacity) return true;
        foreach (var copy in _copies.Values.Where(c => c.Content.IsCompletedSuccessfully).OrderBy(c => c.LastUsed))
        {
            Remove(copy);
            if (_bytes + bytes <= Capacity) return true;
        }
        return false;
    }

    private async Task LoadAsync(Copy copy)
    {
        try
        {
            using var limit = new CancellationTokenSource(ReadLimit);
            var entry = copy.Entry;
            copy.Loaded(await store.OpenVerifiedAsync(entry.Key, entry.Path, entry.Length, long.MaxValue, limit.Token).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // Whoever waits is told; the next hit, if the entry is still there, reads it again.
            lock (_gate) Remove(copy);
            copy.Failed(ex);
            _ = copy.Content.Exception;
        }
    }

    /// <summary>The entry has gone from the index: so does its copy. A hit already sending it finishes first.</summary>
    public void Drop(CacheEntry entry)
    {
        lock (_gate)
            if (_copies.TryGetValue(entry, out var copy)) Remove(copy);
    }

    public void Clear()
    {
        lock (_gate)
            foreach (var copy in _copies.Values) Remove(copy);
    }

    /// <summary>Under the lock.</summary>
    private void Remove(Copy copy)
    {
        if (_copies.TryRemove(new KeyValuePair<CacheEntry, Copy>(copy.Entry, copy))) _bytes -= copy.Bytes;
    }
}
