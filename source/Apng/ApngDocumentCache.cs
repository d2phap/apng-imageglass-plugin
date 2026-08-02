/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
*/
using ImageGlass.SDK.Plugins;
using System.Threading;

namespace ApngCodec.Apng;

/// <summary>
/// Small reference-counted LRU cache of parsed <see cref="ApngDocument"/>s. Parsing and
/// canvas state are reused across the metadata, static-raster and animation entry points,
/// which the host may call for the same file in any order and from any thread.
/// </summary>
internal static class ApngDocumentCache
{
    private const int MAX_DOCUMENTS = 4;

    private sealed class Entry
    {
        public required ApngDocument Document { get; init; }
        public required long Stamp { get; init; }
        public required long Size { get; init; }
        public int RefCount;
    }

    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly List<string> _lruKeys = [];

    // Evicted while still in use; disposed by the last Release.
    private static readonly List<Entry> _retired = [];


    /// <summary>
    /// Returns the parsed document for <paramref name="filePath"/>, parsing it on a cache
    /// miss or when the file changed on disk. The caller MUST pass the result to
    /// <see cref="Release"/> when done. Parsing runs under the cache lock so two threads
    /// never parse the same file twice.
    /// </summary>
    public static ApngDocument? Acquire(string filePath, out IGStatus status)
    {
        long stamp;
        long size;
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                status = IGStatus.IoError;
                return null;
            }
            stamp = fileInfo.LastWriteTimeUtc.Ticks;
            size = fileInfo.Length;
        }
        catch
        {
            status = IGStatus.IoError;
            return null;
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(filePath, out var cached))
            {
                if (cached.Stamp == stamp && cached.Size == size)
                {
                    cached.RefCount++;
                    Touch(filePath);
                    status = IGStatus.OK;
                    return cached.Document;
                }

                // Stale: drop it so the load below re-parses the new bytes.
                Evict(filePath, cached);
            }

            var document = ApngDocument.Load(filePath, out status);
            if (document is null) return null;

            _entries[filePath] = new Entry
            {
                Document = document,
                Stamp = stamp,
                Size = size,
                RefCount = 1,
            };
            Touch(filePath);
            TrimToCapacity();

            return document;
        }
    }


    /// <summary>
    /// Drops one reference taken by <see cref="Acquire"/>, disposing the document when it
    /// has been evicted and nobody is using it any more.
    /// </summary>
    public static void Release(ApngDocument? document)
    {
        if (document is null) return;

        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                if (!ReferenceEquals(entry.Document, document)) continue;

                entry.RefCount--;
                return;
            }

            for (var i = 0; i < _retired.Count; i++)
            {
                var entry = _retired[i];
                if (!ReferenceEquals(entry.Document, document)) continue;

                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    _retired.RemoveAt(i);
                    entry.Document.Dispose();
                }
                return;
            }
        }
    }


    private static void Touch(string key)
    {
        _lruKeys.Remove(key);
        _lruKeys.Add(key);
    }


    /// <summary>
    /// Evicts the oldest idle documents. In-use documents are skipped, so the cache can
    /// briefly hold more than <see cref="MAX_DOCUMENTS"/> entries.
    /// </summary>
    private static void TrimToCapacity()
    {
        var index = 0;
        while (index < _lruKeys.Count && _lruKeys.Count > MAX_DOCUMENTS)
        {
            var key = _lruKeys[index];
            if (!_entries.TryGetValue(key, out var entry))
            {
                _lruKeys.RemoveAt(index);
                continue;
            }

            if (entry.RefCount > 0)
            {
                index++;
                continue;
            }

            Evict(key, entry);
        }
    }


    private static void Evict(string key, Entry entry)
    {
        _entries.Remove(key);
        _lruKeys.Remove(key);

        if (entry.RefCount <= 0) entry.Document.Dispose();
        else _retired.Add(entry);
    }
}
