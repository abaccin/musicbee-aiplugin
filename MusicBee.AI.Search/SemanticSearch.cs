using Microsoft.Extensions.AI;
using MusicBee.AI.Search.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search
{
    public class SemanticSearch
    {
        // Common stopwords (English + Italian + a few French/Spanish/German
        // short particles for good measure). Anything in here is dropped from
        // the token list before matching against track fields, otherwise
        // single-letter / very common words would boost large numbers of
        // unrelated rows and drown out the signal from rare tokens like an
        // artist name.
        private static readonly HashSet<string> Stopwords = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // EN
            "a","an","the","and","or","of","in","on","at","to","for","by","from","with","without",
            "song","songs","track","tracks","music","tune","tunes","album","albums","artist","artists",
            "play","listen","want","like","some","any","please","find","show","give","me","my","i","you","we","us",
            "early","late","old","new","best","top","good","great","favourite","favorite","nice","cool",
            "day","days","year","years","era","period","time","times","morning","evening","night","afternoon",
            "this","that","these","those","there","here","when","where","what","which","who","why","how",
            // IT
            "il","lo","la","i","gli","le","un","uno","una","del","dello","della","dei","degli","delle",
            "al","allo","alla","ai","agli","alle","dal","dallo","dalla","dai","dagli","dalle",
            "nel","nello","nella","nei","negli","nelle","sul","sullo","sulla","sui","sugli","sulle",
            "di","da","a","in","con","su","per","tra","fra","e","ed","o","od","ma","se","che","chi","cui",
            "questo","questa","questi","queste","quel","quello","quella","quei","quegli","quelle",
            "mi","ti","si","ci","vi","ne","lo","la","li","le",
            "canzone","canzoni","brano","brani","musica","traccia","tracce","artista","artisti","album",
            "voglio","vorrei","ascoltare","sentire","mettere","suonare","trovare","cerca","cercami","mostra","dammi","proponi","prononimi","suggerisci",
            "qualche","qualcosa","alcuni","alcune","tutto","tutti","tutta","tutte","ogni","ognuno","sempre","mai","ora","adesso","subito",
            "mattino","mattina","sera","notte","pomeriggio","giorno","giorni","anno","anni","epoca","periodo","tempo",
            "primo","prima","secondo","terzo","ultimo","ultima","nuovo","nuova","vecchio","vecchia","grande","grandi","piccolo","piccola",
            "buono","buoni","buona","buone","bello","bella","belli","belle","migliore","migliori","preferito","preferita","preferiti","preferite",
            // FR/ES/DE shortlist
            "le","la","les","un","une","des","du","de","et","ou","pour","avec","sans","dans","sur",
            "el","los","las","y","o","con","sin","para","por",
            "der","die","das","und","oder","ein","eine","mit","ohne","fur","für",
        };

        private TrackStore _store;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;

        public SemanticSearch(TrackStore store, IEmbeddingGenerator<string, Embedding<float>> embeddings)
        {
            _store = store;
            _embeddings = embeddings;
        }

        /// <summary>Replace the underlying store (used after a rebuild).</summary>
        public void SetStore(TrackStore store) => _store = store;

        public async Task<IReadOnlyList<DbTrackRow>> SearchAsync(string text, int maxResults, CancellationToken cancellationToken = default)
            => await SearchAsync(text, maxResults, applyLexicalBoost: true, cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// When <paramref name="applyLexicalBoost"/> is false, ranking is pure
        /// cosine similarity — use this for mood/vibe/era queries where
        /// matching the literal query words against artist/title would just
        /// surface coincidental string matches (e.g. "spring" → Springsteen,
        /// "cloudy" → S&G "Cloudy"). When true, query tokens that match a
        /// whole word in artist/title/album/genre receive a small boost — use
        /// this when the query is a specific named entity (artist, album,
        /// song title).
        /// </summary>
        public async Task<IReadOnlyList<DbTrackRow>> SearchAsync(string text, int maxResults, bool applyLexicalBoost, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text) || maxResults <= 0) return new List<DbTrackRow>();
            var qEmbedding = await _embeddings.GenerateAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
            var tokens = applyLexicalBoost ? ExtractTokens(text) : System.Array.Empty<string>();
            var hits = _store.SearchHybrid(qEmbedding.Vector.ToArray(), tokens, maxResults);
            return hits.Select(h => h.Row).ToList();
        }

        // Splits on non-alphanumeric, lower-cases, drops short/stopword tokens,
        // and de-duplicates. The output is what TrackStore.SearchHybrid uses to
        // boost rows whose normalised text bag contains any of these tokens.
        internal static IReadOnlyList<string> ExtractTokens(string text)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i <= text.Length; i++)
            {
                var c = i < text.Length ? text[i] : ' ';
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); }
                else if (sb.Length > 0)
                {
                    var t = sb.ToString();
                    sb.Clear();
                    if (t.Length < 3) continue;
                    if (Stopwords.Contains(t)) continue;
                    if (seen.Add(t)) result.Add(t);
                }
            }
            return result;
        }
    }
}
