using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics.Tensors;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search.Storage
{
    /// <summary>
    /// Pure-managed vector store with **zero native dependencies**. Persists
    /// every track row + its embedding to a single binary file and keeps an
    /// in-memory copy so similarity search is a brute-force cosine over all
    /// rows. One <see cref="TensorPrimitives.Dot"/> per row is fast enough
    /// even for tens of thousands of tracks.
    /// </summary>
    public sealed class TrackStore : IDisposable
    {
        // "MBAI" little-endian
        private const int FileMagic = 0x4941424D;
        private const int FileVersion = 1;
        private const int AutoFlushIntervalMs = 2000;

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool _dirty;
        private bool _disposed;
        private Timer _flushTimer;

        /// <summary>
        /// Full path to the binary store file.
        /// </summary>
        public string FilePath { get; }

        public TrackStore(string filePath)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public Task InitialiseAsync(CancellationToken cancellationToken = default)
        {
            LoadFromDisk();
            _flushTimer = new Timer(_ => TryAutoFlush(), null, AutoFlushIntervalMs, AutoFlushIntervalMs);
            return Task.CompletedTask;
        }

        public Task<DbTrackRow> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path)) return Task.FromResult<DbTrackRow>(null);
            Entry e;
            lock (_gate) _byPath.TryGetValue(path, out e);
            return Task.FromResult(e?.Row);
        }

        public Task UpsertAsync(DbTrackRow row, CancellationToken cancellationToken = default)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            if (string.IsNullOrEmpty(row.Path)) throw new ArgumentException("Path is required.", nameof(row));
            if (row.Embedding == null || row.Embedding.Length == 0) throw new ArgumentException("Embedding is required.", nameof(row));

            var entry = new Entry { Row = row, Vector = row.Embedding, Norm = Norm(row.Embedding) };
            lock (_gate) { _byPath[row.Path] = entry; _dirty = true; }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
            lock (_gate) { if (_byPath.Remove(path)) _dirty = true; }
            return Task.CompletedTask;
        }

        public IReadOnlyList<(DbTrackRow Row, float Score)> Search(float[] queryVector, int maxResults)
            => SearchHybrid(queryVector, null, maxResults);

        /// <summary>
        /// Cosine similarity ranking, optionally boosted by literal token hits in
        /// any of the row's text fields (Artist/Title/Album/Genre). Each token
        /// found contributes <c>+0.25</c> (capped at <c>+1.0</c>) on top of the
        /// cosine score. Tokens are matched against an alphanumeric-lowercase
        /// normalisation of each field, so query "REM" matches artist "R.E.M.".
        /// </summary>
        public IReadOnlyList<(DbTrackRow Row, float Score)> SearchHybrid(float[] queryVector, IReadOnlyList<string> normalizedTokens, int maxResults)
        {
            if (queryVector == null || queryVector.Length == 0) return Array.Empty<(DbTrackRow, float)>();
            if (maxResults <= 0) return Array.Empty<(DbTrackRow, float)>();

            var qNorm = Norm(queryVector);
            if (qNorm == 0) return Array.Empty<(DbTrackRow, float)>();

            Entry[] snapshot;
            lock (_gate)
            {
                snapshot = new Entry[_byPath.Count];
                int i = 0;
                foreach (var v in _byPath.Values) snapshot[i++] = v;
            }

            var hasTokens = normalizedTokens != null && normalizedTokens.Count > 0;
            var heap = new TopK(maxResults);
            for (int i = 0; i < snapshot.Length; i++)
            {
                var e = snapshot[i];
                if (e.Vector.Length != queryVector.Length || e.Norm == 0) continue;
                var dot = TensorPrimitives.Dot(e.Vector, queryVector);
                var sim = (float)(dot / (qNorm * e.Norm));

                if (hasTokens)
                {
                    var bag = e.NormalizedBag ?? (e.NormalizedBag = NormalizeBag(e.Row));
                    float boost = 0f;
                    for (int t = 0; t < normalizedTokens.Count; t++)
                    {
                        var tok = normalizedTokens[t];
                        if (bag.Contains(tok))
                        {
                            // Longer tokens are more likely to be proper nouns
                            // (artist / album / song). Boost grows with length
                            // so a single artist-name hit (e.g. "battiato")
                            // dominates the cosine variance and beats unrelated
                            // tracks that happen to embed close to the prompt.
                            boost += 0.15f + 0.10f * System.Math.Min(8, tok.Length);
                        }
                    }
                    if (boost > 0f) sim += System.Math.Min(2.0f, boost);
                }

                heap.Push(sim, e.Row);
            }
            return heap.ToList();
        }

        private static HashSet<string> NormalizeBag(DbTrackRow r)
        {
            var bag = new HashSet<string>(StringComparer.Ordinal);
            AddFieldTokens(bag, r.Artist);
            AddFieldTokens(bag, r.Title);
            AddFieldTokens(bag, r.Album);
            AddFieldTokens(bag, r.Genre);
            return bag;
        }

        // Splits a field on whitespace then collapses each word to its
        // alphanumeric-lowercase form. So "Bruce Springsteen" → {"bruce",
        // "springsteen"} and "R.E.M." → {"rem"}. Whole-word matching prevents
        // query "spring" from spuriously matching "Springsteen".
        private static void AddFieldTokens(HashSet<string> bag, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var sb = new StringBuilder();
            for (int i = 0; i <= s.Length; i++)
            {
                var c = i < s.Length ? s[i] : ' ';
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '/' || c == '\\' || c == '|' || c == ',' || c == ';' || c == ':')
                {
                    if (sb.Length > 0) { bag.Add(sb.ToString()); sb.Clear(); }
                }
                // Other punctuation (apostrophes, dots inside words like R.E.M.) is silently dropped:
                // we treat it as part of the same word, so "R.E.M." becomes "rem" not three separate tokens.
            }
            if (sb.Length > 0) bag.Add(sb.ToString());
        }

        public int Count
        {
            get { lock (_gate) return _byPath.Count; }
        }

        /// <summary>Force-flush pending writes to disk.</summary>
        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushIfDirty();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _flushTimer?.Dispose(); } catch { }
            _flushTimer = null;
            try { FlushIfDirty(); } catch { /* best effort */ }
        }

        // --- internals ---

        private void LoadFromDisk()
        {
            lock (_gate) _byPath.Clear();
            if (!File.Exists(FilePath)) return;

            try
            {
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    var magic = br.ReadInt32();
                    var ver = br.ReadInt32();
                    if (magic != FileMagic || ver != FileVersion) return;

                    var count = br.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var row = new DbTrackRow
                        {
                            Path = br.ReadString(),
                            Title = br.ReadString(),
                            Artist = br.ReadString(),
                            Album = br.ReadString(),
                            Genre = br.ReadString(),
                            Year = br.ReadString(),
                            Comment = br.ReadString(),
                            Rating = br.ReadString(),
                            Fingerprint = br.ReadString(),
                        };
                        var dim = br.ReadInt32();
                        var bytes = br.ReadBytes(dim * sizeof(float));
                        var vec = new float[dim];
                        Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
                        row.Embedding = vec;

                        var entry = new Entry { Row = row, Vector = vec, Norm = Norm(vec) };
                        lock (_gate) _byPath[row.Path] = entry;
                    }
                }
            }
            catch
            {
                // corrupt or unreadable file → start over with an empty store
                lock (_gate) _byPath.Clear();
            }
        }

        private void TryAutoFlush()
        {
            try { FlushIfDirty(); } catch { /* swallowed in timer */ }
        }

        private void FlushIfDirty()
        {
            Entry[] snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                snapshot = new Entry[_byPath.Count];
                int i = 0;
                foreach (var v in _byPath.Values) snapshot[i++] = v;
                _dirty = false;
            }

            var tmp = FilePath + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs, Encoding.UTF8))
                {
                    bw.Write(FileMagic);
                    bw.Write(FileVersion);
                    bw.Write(snapshot.Length);
                    foreach (var e in snapshot)
                    {
                        var r = e.Row;
                        bw.Write(r.Path ?? "");
                        bw.Write(r.Title ?? "");
                        bw.Write(r.Artist ?? "");
                        bw.Write(r.Album ?? "");
                        bw.Write(r.Genre ?? "");
                        bw.Write(r.Year ?? "");
                        bw.Write(r.Comment ?? "");
                        bw.Write(r.Rating ?? "");
                        bw.Write(r.Fingerprint ?? "");
                        bw.Write(e.Vector.Length);
                        var bytes = new byte[e.Vector.Length * sizeof(float)];
                        Buffer.BlockCopy(e.Vector, 0, bytes, 0, bytes.Length);
                        bw.Write(bytes);
                    }
                }
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
                else File.Move(tmp, FilePath);
            }
            catch
            {
                lock (_gate) _dirty = true;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        private static double Norm(float[] v)
        {
            double s = 0;
            for (int i = 0; i < v.Length; i++) s += (double)v[i] * v[i];
            return Math.Sqrt(s);
        }

        private sealed class Entry
        {
            public DbTrackRow Row;
            public float[] Vector;
            public double Norm;
            public HashSet<string> NormalizedBag; // lazily-computed set of per-word tokens (alphanumeric, lowercased)
        }

        /// <summary>Min-heap top-K (keeps the K largest scores).</summary>
        private sealed class TopK
        {
            private readonly int _k;
            private readonly List<(float Score, DbTrackRow Row)> _heap;

            public TopK(int k) { _k = k; _heap = new List<(float, DbTrackRow)>(k + 1); }

            public void Push(float score, DbTrackRow row)
            {
                if (_heap.Count < _k) { _heap.Add((score, row)); SiftUp(_heap.Count - 1); }
                else if (score > _heap[0].Score) { _heap[0] = (score, row); SiftDown(0); }
            }

            public List<(DbTrackRow Row, float Score)> ToList()
            {
                var copy = new List<(DbTrackRow, float)>(_heap.Count);
                foreach (var t in _heap) copy.Add((t.Row, t.Score));
                copy.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                return copy;
            }

            private void SiftUp(int i)
            {
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_heap[parent].Score <= _heap[i].Score) break;
                    var tmp = _heap[parent]; _heap[parent] = _heap[i]; _heap[i] = tmp;
                    i = parent;
                }
            }

            private void SiftDown(int i)
            {
                int n = _heap.Count;
                while (true)
                {
                    int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                    if (l < n && _heap[l].Score < _heap[smallest].Score) smallest = l;
                    if (r < n && _heap[r].Score < _heap[smallest].Score) smallest = r;
                    if (smallest == i) break;
                    var tmp = _heap[i]; _heap[i] = _heap[smallest]; _heap[smallest] = tmp;
                    i = smallest;
                }
            }
        }
    }
}
