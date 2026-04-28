# MusicBee AI Library Search

A MusicBee plugin that embeds your library's tag metadata into a local
vector store and lets you chat with your library using natural language.

The chat / embeddings calls go through [GitHub Models](https://github.com/marketplace/models)
using your existing GitHub account, so no OpenAI key or local Ollama install is
needed -- only a GitHub personal access token (or a `gh auth token`) with the
`read:models` scope.

The vector store is a single binary file (`musicbee_ai_search.bin`) under
MusicBee's persistent storage path -- no SQLite, no native dependencies, no
extra DLLs. Brute-force cosine search runs in pure managed code via
`System.Numerics.Tensors`.

## Projects

| Project | Purpose |
| --- | --- |
| `MusicBee.AI.Search` | The MusicBee plugin (`mb_AISearch.dll`, .NET Framework 4.8). Hosts the WPF chat UI inside a MusicBee dock panel and runs the ingestor + GitHub Models clients. |
| `MusicBee.AI.Console` | Headless host for testing the chat / ingest pipeline outside of MusicBee. |
| `MusicBee.AI.Search.Tests` | xUnit tests for the parts that don't need a network. |

## Plugin install

The simplest path is `.\deploy.ps1 -Force`, which builds the plugin, copies
`mb_AISearch.dll` into MusicBee's `Plugins` folder (auto-elevating if needed),
seeds `settings.json` with `gh auth token`, and starts MusicBee.

Manual install:

1. `dotnet build MusicBee.AI.Search.sln -c Release`
2. Copy `MusicBee.AI.Search\bin\Release\net48\mb_AISearch.dll` into MusicBee's
   `Plugins` folder.
3. Start MusicBee, open *Edit -> Preferences -> Plugins -> AI Library Search*
   and pick a dock location for the panel.
4. Open the Settings section in the panel and paste a GitHub PAT (or run
   `deploy.ps1` once to seed it from `gh auth token`).

The plugin starts indexing the library on a background worker the first time
it loads and reuses cached embeddings whenever a track's metadata fingerprint
is unchanged. The panel shows live progress (X / Y indexed) while ingest is
running.

## Settings

Persisted as JSON at
`<MusicBee persistent storage>\musicbee_ai_search\settings.json`:

```json
{
  "Endpoint": "https://models.github.ai/inference",
  "ChatModel": "openai/gpt-4o-mini",
  "EmbeddingModel": "openai/text-embedding-3-small",
  "EmbeddingDimensions": 1536,
  "Token": ""
}
```

If `Token` is empty the plugin / console will fall back to the `GITHUB_TOKEN`
environment variable.

If you change the embedding model or dimension the local vector store is
recreated automatically on next start.

