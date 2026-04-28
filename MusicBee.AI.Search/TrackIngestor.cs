using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MusicBee.AI.Search.Storage;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search
{
    public class TrackIngestor
    {
        private readonly TrackStore _store;
        private readonly ILogger<TrackIngestor> _logger;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

        public TrackIngestor(
            IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
            TrackStore store,
            ILogger<TrackIngestor> logger)
        {
            _embeddingGenerator = embeddingGenerator;
            _store = store;
            _logger = logger;
        }

        public async Task IngestTrackAsync(DbTrackRow track, CancellationToken cancellationToken = default)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            if (string.IsNullOrEmpty(track.Path)) return;

            var fingerprint = FingerprintHelper.ComputeFingerprint(track);

            try
            {
                var existing = await _store.GetAsync(track.Path, cancellationToken).ConfigureAwait(false);
                if (existing != null && string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _logger?.LogDebug("Skipping unchanged track {Path}", track.Path);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Lookup failed for {Path}; will re-embed", track.Path);
            }

            var textToEmbed = $"{track.Title} {track.Artist} {track.Album} {track.Genre} {track.Year} {track.Comment}".Trim();
            if (string.IsNullOrWhiteSpace(textToEmbed))
            {
                _logger?.LogDebug("Skipping {Path} — no embeddable text", track.Path);
                return;
            }

            var embedding = await _embeddingGenerator.GenerateAsync(textToEmbed, cancellationToken: cancellationToken).ConfigureAwait(false);
            track.Embedding = embedding.Vector.ToArray();
            track.Fingerprint = fingerprint;

            await _store.UpsertAsync(track, cancellationToken).ConfigureAwait(false);
            _logger?.LogDebug("Ingested {Path}", track.Path);
        }

        public Task<DbTrackRow> GetTrackAsync(string path, CancellationToken cancellationToken = default)
            => _store.GetAsync(path, cancellationToken);

        public Task DeleteTrackAsync(string path, CancellationToken cancellationToken = default)
            => _store.DeleteAsync(path, cancellationToken);
    }
}
