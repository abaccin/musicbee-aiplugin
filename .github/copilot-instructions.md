# MusicBee AI Library Search — Copilot Instructions

A MusicBee plugin that embeds the user's library tag metadata into a local vector store and exposes a chat UI hosted inside MusicBee. All LLM/embedding calls go through GitHub Models (no OpenAI key, no Ollama, no SQLite, no native deps).

## Solution layout

| Project | TFM | Purpose |
| --- | --- | --- |
| `MusicBee.AI.Search` | `net48` (WindowsDesktop SDK, WinForms) | The plugin DLL `mb_AISearch.dll`. Hosts `ChatPanel` in a MusicBee dock slot, runs `TrackIngestor` + GitHub Models clients. |
| `MusicBee.AI.Console` | (host, references plugin) | Headless REPL for testing chat/ingest outside MusicBee. Uses `AppContext.BaseDirectory\data` as `baseDataPath`. |
| `MusicBee.AI.Search.Tests` | `net9.0`, xUnit + FluentAssertions + Moq | Tests for non-network parts only (e.g. `FingerprintHelper`, `Settings`). |

`_smoke/` is a throwaway scratch file — ignore unless explicitly working on it.

## Build / test / deploy

- Build everything: `dotnet build MusicBee.AI.Search.sln -c Release`
- Run tests: `dotnet test MusicBee.AI.Search.Tests\MusicBee.AI.Search.Tests.csproj`
- Run a single test: `dotnet test MusicBee.AI.Search.Tests\MusicBee.AI.Search.Tests.csproj --filter "FullyQualifiedName~FingerprintHelperTests.NormalisesCaseAndWhitespace"`
- Deploy to MusicBee: `.\deploy.ps1 -Force` (builds, stops MusicBee, copies the single DLL into `Plugins\`, seeds `settings.json` from `gh auth token`, restarts MusicBee). Pass `-NoStart`, `-NoToken`, or `-MusicBeePath <dir>` as needed. Auto-elevates when `Plugins\` is under `Program Files`.
- Uninstall: `.\uninstall.ps1 -Force` (also strips the `<State>` block referencing `mb_AISearch.dll` from `MusicBee3Settings.ini`).

MusicBee **must be closed** during deploy — it locks loaded plugin DLLs. The deploy/uninstall scripts handle this with `-Force`.

## Architecture (the parts you can't see from one file)

- **Composition root** is `Bootstrapper` (`MusicBee.AI.Search\Bootstrapper.cs`). Both the plugin (`Plugin.cs`) and the Console host construct one with a `baseDataPath`; everything else (`EmbeddingGenerator`, `ChatClient`, `TrackStore`, `TrackIngestor`, `SemanticSearch`, `ChatService`) is wired up from there. **Add new services by extending `Bootstrapper`, not by `new`-ing them in `Plugin.cs`.**
- **Persistent data** lives at `<baseDataPath>\musicbee_ai_search\`:
  - `settings.json` (see `Settings.cs`)
  - `musicbee_ai_search.bin` — the entire vector store as one binary file (magic `MBAI`, version 1, atomic `.tmp` + rename, 2 s autoflush). See `Storage\TrackStore.cs`.
  - `embedding.meta.json` — records the active embedding model + dimensions. If the user changes either, `Bootstrapper.HandleDimensionChange` deletes the store on next start and a full re-ingest follows.
- For the plugin, `baseDataPath` is `MusicBeeApiInterface.Setting_GetPersistentStoragePath()` (typically `%AppData%\MusicBee\mb_storage`).
- **Vector search** is brute-force cosine over an in-memory `Dictionary<path, Entry>` via `System.Numerics.Tensors.TensorPrimitives.Dot`. Do **not** reintroduce SQLite / `sqlite-vec` / native deps — `deploy.ps1` and `Bootstrapper.CleanupLegacyArtefacts` actively scrub leftovers (`SQLitePCLRaw.*`, `e_sqlite3.dll`, `vec0.dll`, `runtimes\`, `*.db*`).
- **Chat client** is `Microsoft.Extensions.AI`'s `ChatClientBuilder(...).UseFunctionInvocation()` wrapping `GitHubModelsChatClient`. Tool calls are how the LLM invokes `SemanticSearch`; `ChatService` is the orchestrator.
- **MusicBee plugin entry point** is `MusicBeePlugin.Plugin` in the global namespace (required by the MusicBee API in `Interfaces\MusicBeeInterface.cs`). The class is `partial`; the file uses `using static MusicBeePlugin.Interfaces.Plugin;`.
- **Threading model in `Plugin.cs`:** MusicBee notifications run on its own thread and must never block. All ingest work goes through a `BlockingCollection<Action>` drained by a single background `_workThread`. UI work in `OnDockablePanelCreated` self-marshals via `panel.Invoke` (panels are STA WinForms).
- **Panel lifecycle:** MusicBee may call `OnDockablePanelCreated` multiple times. **Always construct a fresh `ChatPanel`** and dispose the previous one — re-parenting an existing `UserControl` corrupts handle state and renders the panel solid black after resize. Same approach as the AlbumInsertsViewer plugin.
- **Ingest dedup:** `FingerprintHelper` produces a deterministic hash of a track's tag metadata; tracks are skipped (and embeddings reused) when the fingerprint is unchanged. Tag/rating/library notifications in `ReceiveNotification` enqueue per-file upserts/deletes through the same work queue.
- **Token resolution order** (`Settings.Load`): `settings.json` → `GITHUB_TOKEN` env var. Fine-grained PAT with **Models: Read** is required; classic OAuth scopes do not work. `deploy.ps1` warns when the `gh` CLI token lacks this grant.

## Conventions

- Target framework split is intentional: the plugin is **`net48` only** (MusicBee runs on .NET Framework). Tests + Console can use newer TFMs. Don't try to multi-target the plugin or pull in `net*`-only packages.
- The plugin is shipped as a **single DLL**. All managed dependencies are embedded by **Costura.Fody** (see `FodyWeavers.xml` and `MusicBee.AI.Search.csproj`). The `managedDeploy` array in `deploy.ps1` is therefore just `mb_AISearch.dll` — adding a new package does **not** require editing `deploy.ps1`, but leaving a loose copy of an embedded assembly in `Plugins\` will shadow the embedded one. Don't add files to `managedDeploy` unless you really intend a loose-on-disk dependency.
- `LangVersion` in the plugin is `14.0` against `net48` — modern C# is fine, but runtime APIs are limited to .NET Framework 4.8 + the referenced packages. No `net*`-only BCL types.
- Keep new infrastructure pure-managed and dependency-light. Anything new that needs a native binary will break the single-DLL deploy story and must be discussed first.
- Tests must not hit the network. `GitHubModelsChatClient` / `GitHubModelsEmbeddingGenerator` / `TrackIngestor` are excluded from the existing test surface for that reason; if you need to test them, mock `IChatClient` / `IEmbeddingGenerator<string, Embedding<float>>` (the project already references Moq).
- WinForms only — there is no WPF in this build any more (the legacy `PresentationFramework*` etc. listed in `uninstall.ps1` are scrubbed precisely because earlier versions used WPF). Don't add `UseWPF`.
