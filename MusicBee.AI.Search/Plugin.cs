using MusicBee.AI.Search;
using MusicBee.AI.Search.Ui.WinForms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static MusicBeePlugin.Interfaces.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// MusicBee plugin entry point. The chat UI is a pure WinForms
    /// <see cref="ChatPanel"/> docked into a slot the user picks via
    /// View -> Arrange Panels.
    /// </summary>
    public partial class Plugin
    {
        private MusicBeeApiInterface _musicBeeApi;
        private readonly PluginInfo _pluginInfo = new PluginInfo();

        private Bootstrapper _bootstrapper;
        private ChatPanel _chatPanel;

        // Background ingest queue so MusicBee notification thread is never blocked.
        private BlockingCollection<Action> _workQueue;
        private CancellationTokenSource _workCts;
        private Thread _workThread;

        // Last-known ingest progress; pushed to the panel whenever it appears.
        private int _ingestDone;
        private int _ingestTotal;
        private bool _ingestActive;

        public PluginInfo Initialise(IntPtr apiPointer)
        {
            _musicBeeApi = new MusicBeeApiInterface();
            _musicBeeApi.Initialise(apiPointer);

            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(pluginDir)) NativeMethods.SetDllDirectory(pluginDir);
            }
            catch (Exception ex) { Trace("SetDllDirectory failed: " + ex.Message); }

            _pluginInfo.PluginInfoVersion = PluginInfoVersion;
            _pluginInfo.Name = "AI Library Search (GitHub Models)";
            _pluginInfo.Description = "Chat with your music library; tracks are embedded locally and matched via GitHub Models.";
            _pluginInfo.Author = "Andrea Baccin";
            _pluginInfo.TargetApplication = "AI Library Search";
            _pluginInfo.Type = PluginType.PanelView;
            _pluginInfo.VersionMajor = 6;
            _pluginInfo.VersionMinor = 0;
            _pluginInfo.Revision = 0;
            _pluginInfo.MinInterfaceVersion = MinInterfaceVersion;
            _pluginInfo.MinApiRevision = MinApiRevision;
            _pluginInfo.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents;
            _pluginInfo.ConfigurationPanelHeight = 0;

            return _pluginInfo;
        }

        /// <summary>
        /// Called by MusicBee when our PanelView plugin is added to a dock slot.
        /// Marshals onto the panel's UI thread (always STA in WinForms) and
        /// drops a fresh <see cref="ChatPanel"/> straight in.
        /// Return value: &lt; 0 = resizable / fill the dock area.
        /// </summary>
        /// <remarks>
        /// MusicBee may call this multiple times during its lifetime (e.g. when
        /// the user re-arranges panels). We always construct a NEW ChatPanel for
        /// each host panel -- re-parenting the same UserControl across hosts
        /// after the previous host is disposed corrupts handle state and leaves
        /// the panel rendering as a solid black surface after the first resize.
        /// This is the same approach the AlbumInsertsViewer plugin uses.
        /// </remarks>
        public int OnDockablePanelCreated(Control panel)
        {
            if (panel == null) return 0;

            if (panel.InvokeRequired)
            {
                try { return (int)panel.Invoke(new Func<int>(() => OnDockablePanelCreated(panel))); }
                catch (Exception ex) { Trace("OnDockablePanelCreated marshal failed: " + ex); return 0; }
            }

            try
            {
                EnsureBootstrapper();

                // Dispose any previous panel -- we never reuse instances across hosts.
                try
                {
                    if (_chatPanel != null && !_chatPanel.IsDisposed) _chatPanel.Dispose();
                }
                catch { }
                _chatPanel = new ChatPanel(
                    _bootstrapper,
                    EnqueueTracks,
                    PlayTracks,
                    RebuildIndexAsync,
                    MbTheme.FromMusicBee(_musicBeeApi),
                    GetNowPlayingTrack);
                _chatPanel.Dock = DockStyle.Fill;

                // Push the ingest snapshot so a panel docked mid-ingest shows live progress.
                _chatPanel.ReportIngestProgress(_ingestDone, _ingestTotal, _ingestActive);

                panel.Controls.Add(_chatPanel);

                // 0 = let MusicBee resize the dock host freely. Returning a
                // positive value would lock the host's minimum height to that
                // many pixels, which prevents the user from dragging the
                // splitter up to give the panels below us (Playing Tracks,
                // Track Information, Lyrics, ...) more room.
                return 0;
            }
            catch (Exception ex)
            {
                Trace("OnDockablePanelCreated failed: " + ex);
                try
                {
                    MessageBox.Show("AI Library Search panel failed to initialise:\r\n\r\n" + ex,
                        "AI Library Search", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 0;
            }
        }

        public bool Configure(IntPtr panelHandle) => false;

        public void SaveSettings() { }

        public void Close(PluginCloseReason reason)
        {
            try { _workCts?.Cancel(); _workQueue?.CompleteAdding(); _workThread?.Join(TimeSpan.FromSeconds(2)); } catch { }
            try
            {
                if (_chatPanel != null && !_chatPanel.IsDisposed)
                {
                    _chatPanel.Dispose();
                    _chatPanel = null;
                }
            }
            catch { }
            DisposeBootstrapper();
        }

        public void Uninstall()
        {
            try
            {
                if (_musicBeeApi.Setting_GetPersistentStoragePath != null)
                {
                    var dir = Path.Combine(_musicBeeApi.Setting_GetPersistentStoragePath(), "musicbee_ai_search");
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
            }
            catch { }
        }

        /// <summary>
        /// Right-click menu on the panel header. Presence of this method also
        /// signals to MusicBee that the panel has a header menu.
        /// </summary>
        public List<ToolStripItem> GetHeaderMenuItems()
        {
            var rebuild = new ToolStripMenuItem("Rebuild index");
            rebuild.Click += (s, e) => { try { _ = RebuildIndexAsync(); } catch { } };
            return new List<ToolStripItem> { rebuild };
        }

        // ---- callbacks injected into the ChatPanel ----

        private void EnqueueTracks(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            try { _musicBeeApi.NowPlayingList_QueueFilesLast(paths); }
            catch (Exception ex) { Trace("Enqueue failed: " + ex.Message); }
        }

        private void PlayTracks(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            try
            {
                _musicBeeApi.NowPlayingList_Clear();
                _musicBeeApi.NowPlayingList_QueueFilesLast(paths);
                _musicBeeApi.NowPlayingList_PlayNow(paths[0]);
            }
            catch (Exception ex) { Trace("Play failed: " + ex.Message); }
        }

        private Task RebuildIndexAsync()
        {
            return Task.Run(async () =>
            {
                try
                {
                    EnsureBootstrapper();
                    await _bootstrapper.RebuildAsync().ConfigureAwait(false);
                    EnqueueWork(IngestAllTracks);
                }
                catch (Exception ex)
                {
                    Trace("Rebuild failed: " + ex.Message);
                    throw;
                }
            });
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    EnsureBootstrapper();
                    EnqueueWork(IngestAllTracks);
                    break;

                case NotificationType.TagsChanged:
                case NotificationType.RatingChanging:
                case NotificationType.RatingChanged:
                case NotificationType.FileAddedToLibrary:
                    if (!string.IsNullOrEmpty(sourceFileUrl))
                    {
                        var path = sourceFileUrl;
                        EnqueueWork(() => HandleFileUpserted(path));
                    }
                    break;

                case NotificationType.FileRemovedFromLibrary:
                    if (!string.IsNullOrEmpty(sourceFileUrl))
                    {
                        var path = sourceFileUrl;
                        EnqueueWork(() => HandleFileDeleted(path));
                    }
                    break;

                case NotificationType.TrackChanged:
                case NotificationType.PlayStateChanged:
                    try { _chatPanel?.RefreshNowPlaying(); } catch { /* panel may be gone */ }
                    break;
            }
        }

        // Reads metadata for the currently playing track from MusicBee. Returns
        // null when nothing is playing or the API isn't ready. Called by the
        // chat panel before every prompt and on TrackChanged notifications.
        private DbTrackRow GetNowPlayingTrack()
        {
            try
            {
                if (_musicBeeApi.NowPlaying_GetFileUrl == null) return null;
                var path = _musicBeeApi.NowPlaying_GetFileUrl();
                if (string.IsNullOrEmpty(path)) return null;
                return new DbTrackRow
                {
                    Path = path,
                    Title = SafeNowPlayingTag(MetaDataType.TrackTitle),
                    Artist = SafeNowPlayingTag(MetaDataType.Artist),
                    Album = SafeNowPlayingTag(MetaDataType.Album),
                    Genre = SafeNowPlayingTag(MetaDataType.Genre),
                    Year = SafeNowPlayingTag(MetaDataType.Year),
                    Comment = SafeNowPlayingTag(MetaDataType.Comment),
                    Rating = SafeNowPlayingTag(MetaDataType.Rating)
                };
            }
            catch (Exception ex)
            {
                Trace("GetNowPlayingTrack failed: " + ex.Message);
                return null;
            }
        }

        private string SafeNowPlayingTag(MetaDataType type)
        {
            try { return _musicBeeApi.NowPlaying_GetFileTag?.Invoke(type) ?? ""; }
            catch { return ""; }
        }

        // --- helpers ---

        private void EnsureBootstrapper()
        {
            if (_bootstrapper != null) return;
            try
            {
                _bootstrapper = new Bootstrapper(_musicBeeApi.Setting_GetPersistentStoragePath());
            }
            catch (Exception ex)
            {
                Trace("Bootstrapper init failed: " + ex.Message);
                throw;
            }
            StartWorker();
        }

        private void DisposeBootstrapper()
        {
            try { _bootstrapper?.Dispose(); }
            catch { }
            _bootstrapper = null;
        }

        private void StartWorker()
        {
            if (_workThread != null) return;
            _workQueue = new BlockingCollection<Action>(new ConcurrentQueue<Action>());
            _workCts = new CancellationTokenSource();
            _workThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "MusicBee.AI.Ingest"
            };
            _workThread.Start();
        }

        private void EnqueueWork(Action action)
        {
            try { _workQueue?.Add(action); }
            catch (InvalidOperationException) { /* completed */ }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var action in _workQueue.GetConsumingEnumerable(_workCts.Token))
                {
                    try { action(); }
                    catch (Exception ex) { Trace("Ingest worker error: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void IngestAllTracks()
        {
            // MusicBee's Library_QueryFilesEx expects an empty query (or a real
            // filter). "*" is NOT a wildcard here -- it returns zero files.
            if (!_musicBeeApi.Library_QueryFilesEx("", out var allFiles) || allFiles == null || allFiles.Length == 0)
            {
                Trace("No files found in library to ingest.");
                ReportIngest(0, 0, false);
                return;
            }

            var total = allFiles.Length;
            Trace($"Starting embedding ingest for {total} tracks...");
            ReportIngest(0, total, true);

            int done = 0;
            int errors = 0;
            int consecutiveRateLimited = 0;
            foreach (var path in allFiles)
            {
                if (_workCts.IsCancellationRequested) break;
                try
                {
                    HandleFileUpserted(path);
                    consecutiveRateLimited = 0;
                }
                catch (Exception ex)
                {
                    errors++;
                    var rateLimited = IsRateLimited(ex);
                    if (rateLimited)
                    {
                        consecutiveRateLimited++;
                        // After ~10 back-to-back 429s the per-call retry budget is
                        // already exhausted; sleep a longer cooldown rather than
                        // spamming the log + the API. Doubles each time, capped.
                        var cooldownSec = Math.Min(300, 30 * (1 << Math.Min(consecutiveRateLimited - 10, 4)));
                        if (consecutiveRateLimited == 10)
                            Trace($"GitHub Models is rate-limiting embeddings; pausing ingest for {cooldownSec}s.");
                        if (consecutiveRateLimited >= 10)
                        {
                            try { Task.Delay(TimeSpan.FromSeconds(cooldownSec), _workCts.Token).GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) { break; }
                        }
                    }
                    // Surface only the first few file-level errors to avoid
                    // flooding the transcript when something is systemically wrong.
                    if (errors <= 5)
                        Trace($"Ingest error ({System.IO.Path.GetFileName(path)}): {ex.Message}");
                    else if (errors == 6)
                        Trace("Further per-track ingest errors will be suppressed; see status counter.");
                }
                done++;
                var pushNow = done <= 50 || (done % 5 == 0) || done == total;
                if (pushNow) ReportIngest(done, total, true);
                if (done % 100 == 0) Trace($"Ingest progress: {done}/{total} ({errors} errors)");
            }
            Trace($"Ingest finished: {done}/{total} ({errors} errors)");
            ReportIngest(done, total, false);
        }

        private static bool IsRateLimited(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.Message != null && e.Message.IndexOf("429", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private void ReportIngest(int done, int total, bool active)
        {
            _ingestDone = done;
            _ingestTotal = total;
            _ingestActive = active;
            try { _chatPanel?.ReportIngestProgress(done, total, active); }
            catch { /* panel may not exist yet */ }
        }

        private void HandleFileUpserted(string filePath)
        {
            var meta = ExtractMetadata(filePath);
            _bootstrapper.TrackIngestor.IngestTrackAsync(meta).GetAwaiter().GetResult();
        }

        private void HandleFileDeleted(string filePath)
        {
            _bootstrapper.TrackIngestor.DeleteTrackAsync(filePath).GetAwaiter().GetResult();
        }

        private DbTrackRow ExtractMetadata(string filePath)
        {
            return new DbTrackRow
            {
                Path = filePath,
                Title = SafeTag(filePath, MetaDataType.TrackTitle),
                Artist = SafeTag(filePath, MetaDataType.Artist),
                Album = SafeTag(filePath, MetaDataType.Album),
                Genre = SafeTag(filePath, MetaDataType.Genre),
                Year = SafeTag(filePath, MetaDataType.Year),
                Comment = SafeTag(filePath, MetaDataType.Comment),
                Rating = SafeTag(filePath, MetaDataType.Rating)
            };
        }

        private string SafeTag(string filePath, MetaDataType type)
        {
            try { return _musicBeeApi.Library_GetFileTag(filePath, type) ?? ""; }
            catch { return ""; }
        }

        private void Trace(string message)
        {
            try { _musicBeeApi.MB_Trace?.Invoke("[mb_AISearch] " + message); }
            catch { }
            // Mirror to the chat panel so the user can see what's happening
            // without needing to open MusicBee's debug log.
            try { _chatPanel?.AppendLog(message); }
            catch { }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetDllDirectory(string lpPathName);
        }
    }
}
