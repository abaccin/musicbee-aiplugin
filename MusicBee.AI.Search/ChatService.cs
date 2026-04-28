using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search
{
    public class ChatService
    {
        private const string SystemPrompt = @"You are the AI assistant inside the user's MusicBee music player. Your only job is to surface tracks from the user's LOCAL library that match the user's request. You have NO knowledge of which tracks the user owns — you must always discover them via the SearchLibrary tool.

HARD RULES (no exceptions):
1. For ANY user message that could plausibly resolve to one or more tracks (by song, artist, album, genre, era, mood, activity, language, tempo, instrument, lyrical theme, or anything else music-related), you MUST call SearchLibrary BEFORE composing your reply. Do not answer from memory. Do not list tracks, artists, or albums that you have not seen returned by the tool in the current turn.
2. Pick the right searchMode for each call:
   - 'named' — REQUIRED whenever the user's message contains ANY proper noun that could be an artist, album, song, or band, even if it's surrounded by descriptors. Examples that MUST use 'named' (with the proper noun alone as the query): 'play some R.E.M.' → query='R.E.M.'; 'do I have anything by Battiato' → query='Battiato'; 'pick some famous early days REM songs' → query='R.E.M.'; 'find Wish You Were Here by Floyd' → query='Wish You Were Here'; 'some Beatles for the morning' → query='Beatles'. Strip the descriptors and pass JUST the proper noun. Acronyms like REM, U2, INXS, AC/DC are proper nouns — try them with and without punctuation (e.g. 'R.E.M.' AND 'REM') if the first call returns nothing relevant.
   - 'vibe' — for purely descriptive queries with NO proper noun: moods, eras, activities, weather, seasons, instruments, languages, lyrical themes. Examples: 'music for a cloudy day', 'spring songs', 'sad italian ballads from the 70s', 'something energetic for a workout'.
   When the user mixes a proper noun with descriptors (e.g. 'famous early days REM songs', 'Beatles for the morning'), use 'named' on the proper noun. Lexical matching on a vibe query produces silly results (e.g. 'spring' matching Springsteen, 'cloudy' matching the song titled Cloudy), so never put descriptors in a 'named' query.
3. Calling SearchLibrary 2-4 times per turn is normal and expected. If the first call returns few or unrelated results, try a different phrasing, the artist's name without punctuation, an album, a genre, or an era before giving up.
4. Only mention tracks that the tool actually returned in the CURRENT turn. Never invent paths, titles, artists, or albums. Never reuse tracks from earlier turns unless the tool returns them again now. If the tool returns tracks by an artist the user did NOT ask for, do NOT present them as matches — search again with a tighter query, or tell the user honestly that you couldn't find what they asked for.
5. If, after several searches, the library has nothing relevant, say so honestly and suggest a different phrasing the user could try. Do NOT fall back to listing tracks you remember from training data.

Response style:
- Reply in the user's language.
- Be brief: a single short sentence introducing the picks is enough. The UI already shows the full list of returned tracks next to the chat, so do NOT re-list every track in your reply.
- The user does not need file paths or technical details — they will click to play or enqueue tracks directly from the UI.";

        private readonly IChatClient _chatClient;
        private readonly SemanticSearch _semanticSearch;
        private readonly ChatOptions _chatOptions;
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();

        /// <summary>
        /// Raised every time the model invokes the SearchLibrary tool. The list contains
        /// the tracks returned to the model so the UI can present them for queueing.
        /// </summary>
        public event Action<IReadOnlyList<DbTrackRow>> TracksSuggested;

        public ChatService(IChatClient chatClient, SemanticSearch semanticSearch)
        {
            _chatClient = chatClient;
            _semanticSearch = semanticSearch;
            _chatOptions = new ChatOptions
            {
                Tools = new List<AITool> { AIFunctionFactory.Create(SearchLibrary) }
            };
            _messages.Add(new ChatMessage(ChatRole.System, SystemPrompt));
        }

        public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();

        public void AddMessage(ChatMessage message) => _messages.Add(message);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(CancellationToken cancellationToken = default)
            => _chatClient.GetStreamingResponseAsync(_messages, _chatOptions, cancellationToken);

        public Task<ChatResponse> GetResponseAsync(CancellationToken cancellationToken = default)
            => _chatClient.GetResponseAsync(_messages, _chatOptions, cancellationToken);

        public void Reset()
        {
            _messages.Clear();
            _messages.Add(new ChatMessage(ChatRole.System, SystemPrompt));
        }

        [Description("Searches the user's local music library and returns the most relevant tracks. Use 'named' mode for queries about specific artists/albums/songs and 'vibe' mode (default) for moods, eras, activities, weather, seasons, or any descriptive query.")]
        private async Task<IEnumerable<string>> SearchLibrary(
            [Description("For 'vibe' mode: a descriptive phrase like 'melancholic acoustic ballads' or 'upbeat 80s synthpop'. For 'named' mode: the artist/album/song name itself, e.g. 'Battiato' or 'Wish You Were Here'.")] string query,
            [Description("'vibe' (default) for moods/eras/activities/descriptive queries — pure semantic matching. 'named' for specific artist/album/song lookups — adds a small lexical boost for exact word matches.")] string searchMode = "vibe",
            [Description("Maximum number of matches to return (default 8)")] int maxResults = 8)
        {
            var applyLexical = string.Equals(searchMode, "named", System.StringComparison.OrdinalIgnoreCase);
            var results = await _semanticSearch.SearchAsync(query, maxResults, applyLexical).ConfigureAwait(false);
            try { TracksSuggested?.Invoke(results); } catch { /* never crash the tool loop on UI errors */ }

            if (results.Count == 0)
            {
                return new[] { "<no_results/>" };
            }
            return results.Select(r =>
                $"<result path=\"{Escape(r.Path)}\" artist=\"{Escape(r.Artist)}\" title=\"{Escape(r.Title)}\" album=\"{Escape(r.Album)}\" year=\"{Escape(r.Year)}\" genre=\"{Escape(r.Genre)}\"/>");
        }

        private static string Escape(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
