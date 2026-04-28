using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBee.AI.Search.Ui.WinForms
{
    /// <summary>
    /// Pure-WinForms chat panel hosted directly inside MusicBee's dockable
    /// panel slot. No WPF / ElementHost / interop -- which is what every
    /// other MusicBee plugin uses.
    /// </summary>
    public sealed class ChatPanel : UserControl
    {
        private readonly MbTheme _theme;
        private Color Bg => _theme.Background;
        private Color BgAlt => _theme.BackgroundAlt;
        private Color BgInput => _theme.InputBackground;
        private Color Border => _theme.Border;
        private Color Fg => _theme.Foreground;
        private Color FgDim => _theme.ForegroundDim;
        private Color BtnBg => _theme.ButtonBackground;
        private Color Accent => _theme.Accent;

        private readonly Bootstrapper _bootstrapper;
        private readonly Action<string[]> _enqueueLast;
        private readonly Action<string[]> _playNow;
        private readonly Func<Task> _rebuildAllAsync;
        private readonly Func<DbTrackRow> _getNowPlaying;
        private CancellationTokenSource _cts;

        private readonly HashSet<string> _suggestedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // controls
        private Panel _progressPanel;
        private ProgressBar _progressBar;
        private Label _progressLabel;
        private Label _statusLabel;
        private TextBox _transcriptBox;
        private TextBox _promptBox;
        private Button _sendButton;
        private Button _cancelButton;
        private Button _resetButton;
        private Button _settingsButton;
        private Button _rebuildButton;
        private ListView _suggestedList;
        // Copilot-style "Active Document" chip for the now-playing track.
        // _nowPlayingChip is shown when a track is playing AND _useNowPlaying
        // is true; clicking the chip's X dismisses it (sets _useNowPlaying=false)
        // and reveals _useNowPlayingAddBtn so the user can re-add it.
        private FlowLayoutPanel _nowPlayingRow;
        private Panel _nowPlayingChip;
        private CheckBox _nowPlayingChipCheck;
        private Label _nowPlayingChipIcon;
        private Label _nowPlayingChipText;
        private FlowLayoutPanel _samplesRow;
        private bool _useNowPlaying = true;
        private DbTrackRow _nowPlayingTrack;
        private string _weatherHint; // e.g. "rainy", "sunny" — populated best-effort from wttr.in

        public ChatPanel(Bootstrapper bootstrapper,
                         Action<string[]> enqueueLast,
                         Action<string[]> playNow,
                         Func<Task> rebuildAllAsync,
                         MbTheme theme = null,
                         Func<DbTrackRow> getNowPlaying = null)
        {
            _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
            _enqueueLast = enqueueLast ?? (_ => { });
            _playNow = playNow ?? (_ => { });
            _rebuildAllAsync = rebuildAllAsync ?? (() => Task.CompletedTask);
            _getNowPlaying = getNowPlaying ?? (() => null);
            _theme = theme ?? MbTheme.Default;

            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 9f);
            // Make sure the dock host can shrink us freely. UserControl's default
            // MinimumSize is (0,0) but some hosts copy AutoScrollMinSize from
            // child layouts; force Empty + AutoSize=false so MusicBee's vertical
            // splitter between this panel and the panels below us isn't blocked.
            MinimumSize = Size.Empty;
            AutoSize = false;
            // NOTE: do NOT set DoubleBuffered on this UserControl.When hosted inside
            // MusicBee's dockable panel, the host invalidates with RDW_NOCHILDREN on
            // resize; combined with our buffered surface that suppresses repaint of
            // every nested control, leaving the panel solid black until the first
            // user interaction. Plain (non-buffered) painting is what every other
            // working MusicBee plugin (e.g. AlbumInsertsViewer) does.
            SetStyle(ControlStyles.ResizeRedraw, true);
            BuildUi();
            RefreshNowPlaying();
            RebuildSamples();

            _bootstrapper.ChatService.TracksSuggested += OnTracksSuggested;
            _bootstrapper.ServicesChanged += OnBootstrapperServicesChanged;
            _bootstrapper.EmbeddingProviderChanged += OnEmbeddingProviderChanged;
        }

        private void OnBootstrapperServicesChanged(object sender, EventArgs e)
        {
            // ChatService instance was swapped by ApplyChangedSettingsAsync.
            // Re-subscribe TracksSuggested on the NEW instance; the old
            // instance will be GC'd along with its events.
            try { _bootstrapper.ChatService.TracksSuggested += OnTracksSuggested; } catch { }
        }

        private async void OnEmbeddingProviderChanged(object sender, EventArgs e)
        {
            // Embedding identity changed -> store was rebuilt; kick ingest off
            // again so the user doesn't have to do it manually. Best-effort:
            // failures surface in the status bar.
            try
            {
                RunOnUi(() => SetStatus("Re-indexing after embedding change..."));
                await _rebuildAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { RunOnUi(() => SetStatus("Re-index failed: " + ex.Message)); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _bootstrapper.ChatService.TracksSuggested -= OnTracksSuggested; } catch { }
                try { _bootstrapper.ServicesChanged -= OnBootstrapperServicesChanged; } catch { }
                try { _bootstrapper.EmbeddingProviderChanged -= OnEmbeddingProviderChanged; } catch { }
                try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // MusicBee's dockable-panel host invalidates only OUR top-level UserControl
        // when the user drags the panel's edge. Without WS_CLIPCHILDREN +
        // WS_CLIPSIBLINGS plus an explicit child-invalidation on resize, our
        // background paints over the children and they never receive a WM_PAINT,
        // leaving the panel as a solid dark rectangle until the user clicks
        // somewhere. Together these two overrides eliminate the "black on resize"
        // symptom.
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_CLIPCHILDREN = 0x02000000;
                const int WS_CLIPSIBLINGS = 0x04000000;
                var cp = base.CreateParams;
                cp.Style |= WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
                return cp;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated && !IsDisposed)
            {
                // true => invalidate children too. Without this, children are
                // marked clean because the host repainted the parent only.
                Invalidate(true);
                Update();
            }
        }

        // ---------- UI construction ----------
        // Layout is intentionally pure-Dock (no TableLayoutPanel for the root).
        // MusicBee's dockable host repeatedly resizes the panel during a drag --
        // including transient sizes where Absolute-height TLP rows do not fit --
        // and a TableLayoutPanel-rooted layout collapses its rows in that situation
        // and never recovers, leaving the panel as two big dark rectangles. Pure
        // Dock has none of that fragility (it's also what AlbumInsertsViewer and
        // the other reference plugins use).
        private void BuildUi()
        {
            SuspendLayout();

            // 1. Status bar (bottom-most).
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = 20,
                BackColor = Color.FromArgb(22, 22, 26),
                ForeColor = FgDim,
                Padding = new Padding(8, 0, 8, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Ready"
            };

            // 2. Prompt row -- textbox on the left, buttons on the right.
            // Compact (60 px) so the cumulative height of the bottom-docked
            // strip stays small enough that the user can drag MusicBee's
            // splitter up and give the panels below us (Playing Tracks /
            // Track Information / Lyrics) reasonable room.
            var promptRow = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Bg,
                Padding = new Padding(8, 4, 8, 6)
            };

            // 2b. Now-playing strip (sits ABOVE the prompt row, BELOW the centre area).
            // Mirrors GitHub Copilot Chat's "Active Document" chip: when a track
            // is playing it appears as a bordered pill with an icon, the track
            // text and an X to dismiss. Dismissing reveals a "Use now playing"
            // chip that re-adds the track. The strip auto-collapses when nothing
            // is playing and no add-button is shown.
            _nowPlayingRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 0),
                BackColor = Bg,
                Padding = new Padding(8, 4, 8, 0),
                Margin = new Padding(0),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            BuildNowPlayingChip();
            _samplesRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Bg,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _nowPlayingRow.Controls.Add(_nowPlayingChip);
            _nowPlayingRow.Controls.Add(_samplesRow);

            // Prompt buttons: Dock=Right one-by-one rather than a FlowLayoutPanel.
            // FlowLayoutPanel + AutoSize + Dock=Right has unpredictable wrap behaviour
            // and was hiding the Reset button. Direct Dock=Right (in reverse z-order
            // so the LAST added gets the right-most slot) is deterministic.
            _sendButton = MakeButton("\u2191  Send");
            _sendButton.Dock = DockStyle.Right;
            _sendButton.Click += async (s, e) => await SendAsync();
            _cancelButton = MakeButton("\u2715  Cancel");
            _cancelButton.Dock = DockStyle.Right;
            _cancelButton.Enabled = false;
            _cancelButton.Click += (s, e) => _cts?.Cancel();
            _resetButton = MakeButton("\u21BA  Reset");
            _resetButton.Dock = DockStyle.Right;
            _resetButton.Click += (s, e) =>
            {
                _bootstrapper.ChatService.Reset();
                _transcriptBox.Clear();
                ClearSuggested();
                SetStatus("Conversation reset.");
            };

            _promptBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                BackColor = BgInput,
                ForeColor = Fg,
                BorderStyle = BorderStyle.FixedSingle,
                AcceptsReturn = false,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 10f)
            };
            _promptBox.KeyDown += PromptBox_KeyDown;
            // Add Fill child first, then the Dock=Right buttons in left-to-right
            // visual order. WinForms docks the LAST-added control first (outermost),
            // so adding Reset last places it at the right edge, then Cancel, then Send.
            promptRow.Controls.Add(_promptBox);
            promptRow.Controls.Add(_sendButton);
            promptRow.Controls.Add(_cancelButton);
            promptRow.Controls.Add(_resetButton);

            // 3. Header row (title + Settings + Rebuild). TableLayoutPanel with
            // AutoSize so the row height adapts to the buttons' preferred height
            // (DPI / font scaling). MinimumSize keeps it at >=44 px so it has
            // visual presence even on low-DPI displays.
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 32),
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Bg,
                Padding = new Padding(8, 4, 8, 4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _settingsButton = MakeButton("\u2699  Settings");
            _settingsButton.Anchor = AnchorStyles.Right;
            _settingsButton.Margin = new Padding(4, 2, 0, 2);
            _settingsButton.Click += (s, e) => SettingsForm.ShowSingleton(this.FindForm(), _bootstrapper, _theme);

            _rebuildButton = MakeButton("\u27F3  Rebuild");
            _rebuildButton.Anchor = AnchorStyles.Right;
            _rebuildButton.Margin = new Padding(4, 2, 0, 2);
            _rebuildButton.Click += async (s, e) => await RebuildIndexAsync();

            var title = new Label
            {
                Text = "AI Library Search",
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Fg,
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(_settingsButton, 1, 0);
            header.Controls.Add(_rebuildButton, 2, 0);

            _progressPanel = BuildProgressPanel();
            _progressPanel.Dock = DockStyle.Top;
            _progressPanel.Visible = false;

            // 4. Centre area: left (suggested) | splitter | right (conversation).
            var centerArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Bg
            };

            // ----- left side -----
            var leftArea = new Panel
            {
                Dock = DockStyle.Left,
                Width = 220,
                BackColor = Bg
            };
            // Suggested-tracks toolbar: 2 rows × 3 columns of equal-percent cells
            // so the buttons always fit and never wrap unpredictably the way a
            // FlowLayoutPanel does in narrow widths.
            var leftButtons = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 96,
                ColumnCount = 3,
                RowCount = 2,
                BackColor = Bg,
                Padding = new Padding(2, 4, 2, 4)
            };
            leftButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            leftButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            leftButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            leftButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            leftButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var allBtn = MakeCellButton("\u2611 All");
            allBtn.Click += (s, e) => SetAllChecked(true);
            var noneBtn = MakeCellButton("\u2610 None");
            noneBtn.Click += (s, e) => SetAllChecked(false);
            var clearBtn = MakeCellButton("\u2715 Clear");
            clearBtn.Click += (s, e) => ClearSuggested();
            var playBtn = MakeCellButton("\u25B6 Play");
            playBtn.Click += (s, e) =>
            {
                var paths = CheckedPaths();
                if (paths.Length == 0) return;
                _playNow(paths);
                SetStatus($"Playing {paths.Length} track(s).");
            };
            var enqueueBtn = MakeCellButton("\u2795 Enqueue");
            enqueueBtn.Click += (s, e) =>
            {
                var paths = CheckedPaths();
                if (paths.Length == 0) return;
                _enqueueLast(paths);
                SetStatus($"Enqueued {paths.Length} track(s).");
            };
            leftButtons.Controls.Add(allBtn,     0, 0);
            leftButtons.Controls.Add(noneBtn,    1, 0);
            leftButtons.Controls.Add(clearBtn,   2, 0);
            leftButtons.Controls.Add(playBtn,    0, 1);
            leftButtons.Controls.Add(enqueueBtn, 1, 1);
            leftButtons.SetColumnSpan(enqueueBtn, 2);

            var leftHeader = new Label
            {
                Text = "Suggested tracks",
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 26,
                ForeColor = Fg,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Padding = new Padding(4, 0, 4, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _suggestedList = new ListView
            {
                Dock = DockStyle.Fill,
                BackColor = BgAlt,
                ForeColor = Fg,
                BorderStyle = BorderStyle.FixedSingle,
                CheckBoxes = true,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.None,
                View = View.Details,
                MultiSelect = true,
                ShowItemToolTips = true
            };
            _suggestedList.Columns.Add("Track", 200);
            // Auto-fill the column to the ListView's client width so long track
            // names use the full panel width instead of getting truncated.
            void ResizeCol()
            {
                if (_suggestedList.Columns.Count == 0) return;
                var w = _suggestedList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
                if (w > 50) _suggestedList.Columns[0].Width = w;
            }
            _suggestedList.SizeChanged += (_, __) => ResizeCol();
            _suggestedList.HandleCreated += (_, __) => ResizeCol();
            // Fill first, then docked siblings.
            leftArea.Controls.Add(_suggestedList);
            leftArea.Controls.Add(leftHeader);
            leftArea.Controls.Add(leftButtons);

            // ----- splitter -----
            var splitter = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 6,
                BackColor = Border,
                MinExtra = 100,
                MinSize = 120
            };

            // ----- right side -----
            var rightHeader = new Label
            {
                Text = "Conversation",
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 26,
                ForeColor = Fg,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Padding = new Padding(4, 0, 4, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _transcriptBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                BackColor = BgAlt,
                ForeColor = Fg,
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                // Segoe UI renders the BMP glyphs we use as chat bullets
                // (\u25CF for You, \u2726 for Assistant) via the standard
                // font-fallback chain. Consolas does not.
                Font = new Font("Segoe UI", 9.5f)
            };
            var rightArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Bg
            };
            rightArea.Controls.Add(_transcriptBox);
            rightArea.Controls.Add(rightHeader);

            // Fill last so the docked siblings get their slots first.
            centerArea.Controls.Add(rightArea);
            centerArea.Controls.Add(splitter);
            centerArea.Controls.Add(leftArea);

            // 5. Compose root in z-order: Fill last.
            Controls.Add(centerArea);
            Controls.Add(_progressPanel);
            Controls.Add(header);
            // Bottom-docked stack (added last->outermost-bottom because WinForms
            // docks in REVERSE child order): status (very bottom), prompt
            // (above status), now-playing (above prompt).
            Controls.Add(_nowPlayingRow);
            Controls.Add(promptRow);
            Controls.Add(_statusLabel);

            ResumeLayout(true);
        }

        // Show or hide a Dock=Top panel.  Width/height stay intrinsic to the panel.
        private void SetTopPanelVisible(Control panel, bool visible)
        {
            if (panel == null) return;
            panel.Visible = visible;
            // Force the parent to re-flow docked children.
            panel.Parent?.PerformLayout();
        }


        private Panel BuildProgressPanel()
        {
            var p = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                BackColor = BgAlt,
                Padding = new Padding(8, 6, 8, 6),
                Visible = false
            };
            _progressLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Fg,
                Text = "Indexing...",
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 12,
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous,
                ForeColor = Accent
            };
            p.Controls.Add(_progressBar);
            p.Controls.Add(_progressLabel);
            return p;
        }

        // Use Segoe UI Symbol so the unicode icons (\u25B6 \u2611 \u2715 \u27F3 etc)
        // render reliably alongside Latin text. It falls back to Segoe UI for
        // ordinary characters, so the appearance for letters is unchanged.
        private static readonly Font ButtonFont = new Font("Segoe UI Symbol", 9.5f, FontStyle.Regular);
        private static readonly Font CellButtonFont = new Font("Segoe UI", 8.25f, FontStyle.Regular);

        private Button MakeButton(string text)
        {
            var b = new Button
            {
                Text = text,
                BackColor = BtnBg,
                ForeColor = Fg,
                FlatStyle = FlatStyle.Flat,
                Font = ButtonFont,
                Margin = new Padding(2, 2, 2, 2),
                Padding = new Padding(8, 2, 8, 2),
                MinimumSize = new Size(0, 26),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Border;
            return b;
        }

        // Cell button: fills its TableLayoutPanel cell (no AutoSize). Used for
        // grid-style toolbars where each button gets an equal share of the row.
        private Button MakeCellButton(string text)
        {
            var b = new Button
            {
                Text = text,
                BackColor = BtnBg,
                ForeColor = Fg,
                FlatStyle = FlatStyle.Flat,
                Font = CellButtonFont,
                Margin = new Padding(3, 3, 3, 3),
                Padding = new Padding(2, 0, 2, 0),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = false,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Border;
            return b;
        }

        // Chat-bullet prefixes for transcript lines. BMP glyphs that render in
        // Segoe UI without needing the colour-emoji font.
        private const string UserPrefix = "\u25CF You: ";          // ●
        private const string AssistantPrefix = "\u2726 Assistant: "; // ✦

        // ---------- behaviour ----------
        public void RefreshNowPlaying()
        {
            RunOnUi(() =>
            {
                try
                {
                    _nowPlayingTrack = _getNowPlaying();
                    UpdateNowPlayingRow();
                }
                catch { /* ignore */ }
            });
        }

        private void UpdateNowPlayingRow()
        {
            var hasTrack = _nowPlayingTrack != null && !string.IsNullOrEmpty(_nowPlayingTrack.Path);
            _nowPlayingChip.Visible = hasTrack;
            if (hasTrack)
            {
                _nowPlayingChipText.Text = FormatNowPlayingShort(_nowPlayingTrack);
                _nowPlayingChipCheck.Checked = _useNowPlaying;
            }
            // Row stays visible whenever we have either a chip or any sample suggestions.
            _nowPlayingRow.Visible = hasTrack || (_samplesRow != null && _samplesRow.Controls.Count > 0);
            _nowPlayingRow.Parent?.PerformLayout();
        }

        private static string FormatNowPlayingShort(DbTrackRow t)
        {
            if (t == null) return "";
            var artist = t.Artist;
            var title = t.Title;
            if (string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(title)) return t.Path ?? "";
            if (string.IsNullOrEmpty(artist)) return title;
            if (string.IsNullOrEmpty(title))  return artist;
            return $"{artist} \u2014 {title}";
        }

        // Builds the "Active Document"-style chip: bordered pill with a leading
        // checkbox (toggles whether the now-playing context is sent to the model),
        // a music-note icon and the track text. Replaces the older X-dismiss +
        // separate "+ Use now playing" button design with a single always-visible
        // chip that can be checked / unchecked in place.
        private void BuildNowPlayingChip()
        {
            _nowPlayingChip = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = BgInput,
                Padding = new Padding(4, 3, 8, 3),
                Margin = new Padding(0, 0, 8, 4),
                BorderStyle = BorderStyle.FixedSingle
            };
            _nowPlayingChipCheck = new CheckBox
            {
                AutoSize = true,
                Checked = _useNowPlaying,
                BackColor = BgInput,
                ForeColor = Fg,
                Margin = new Padding(0, 2, 4, 0),
                Padding = new Padding(0),
                Text = "",
                FlatStyle = FlatStyle.Standard
            };
            _nowPlayingChipCheck.CheckedChanged += (s, e) =>
            {
                _useNowPlaying = _nowPlayingChipCheck.Checked;
            };
            _nowPlayingChipIcon = new Label
            {
                Text = "\u266B",
                AutoSize = true,
                ForeColor = Accent,
                BackColor = BgInput,
                Font = new Font("Segoe UI Symbol", 9.5f, FontStyle.Bold),
                Margin = new Padding(0),
                Padding = new Padding(0, 2, 4, 0)
            };
            _nowPlayingChipText = new Label
            {
                Text = "",
                AutoSize = true,
                MaximumSize = new Size(360, 0),
                ForeColor = Fg,
                BackColor = BgInput,
                Font = new Font("Segoe UI", 9f),
                Margin = new Padding(0),
                Padding = new Padding(0, 3, 0, 0)
            };

            // Lay out chip contents left-to-right via a tiny FlowLayoutPanel so
            // sizes adapt to text length.
            var flow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = BgInput,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            flow.Controls.Add(_nowPlayingChipCheck);
            flow.Controls.Add(_nowPlayingChipIcon);
            flow.Controls.Add(_nowPlayingChipText);
            _nowPlayingChip.Controls.Add(flow);

            // Click anywhere on the chip (other than the checkbox) toggles the
            // checkbox — gives a larger hit target and matches the previous
            // "click chip to refresh" gesture.
            EventHandler toggle = (s, e) => _nowPlayingChipCheck.Checked = !_nowPlayingChipCheck.Checked;
            _nowPlayingChip.Click       += toggle;
            _nowPlayingChipIcon.Click   += toggle;
            _nowPlayingChipText.Click   += toggle;
        }

        private static string BuildNowPlayingContext(DbTrackRow t)
        {
            if (t == null) return null;
            var sb = new StringBuilder();
            sb.AppendLine("The user is currently listening to the following track in MusicBee:");
            if (!string.IsNullOrEmpty(t.Title))   sb.AppendLine($"- Title: {t.Title}");
            if (!string.IsNullOrEmpty(t.Artist))  sb.AppendLine($"- Artist: {t.Artist}");
            if (!string.IsNullOrEmpty(t.Album))   sb.AppendLine($"- Album: {t.Album}");
            if (!string.IsNullOrEmpty(t.Year))    sb.AppendLine($"- Year: {t.Year}");
            if (!string.IsNullOrEmpty(t.Genre))   sb.AppendLine($"- Genre: {t.Genre}");
            if (!string.IsNullOrEmpty(t.Comment)) sb.AppendLine($"- Comment: {t.Comment}");
            sb.AppendLine("If the user's question refers to \"this song\", \"the current track\", \"playing now\" etc., answer with respect to this track. You may still call SearchLibrary if the user wants similar tracks from their library.");
            return sb.ToString();
        }

        // ---------- sample-prompt chips ----------

        // Rebuilds the small row of clickable suggestion chips (next to the
        // now-playing checkbox) based on time-of-day and current season, plus
        // an optional weather hint fetched best-effort from wttr.in.
        public void RebuildSamples()
        {
            // Capture base samples synchronously so the row appears immediately.
            var samples = BuildSamples(DateTime.Now, _weatherHint);
            RunOnUi(() => ApplySamples(samples));

            // Then kick off a one-shot weather lookup; refresh once if it hits.
            if (_weatherHint == null)
            {
                _ = Task.Run(async () =>
                {
                    var w = await TryFetchWeatherAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(w)) return;
                    _weatherHint = w;
                    var refreshed = BuildSamples(DateTime.Now, _weatherHint);
                    RunOnUi(() => ApplySamples(refreshed));
                });
            }
        }

        private void ApplySamples(IList<string> samples)
        {
            if (_samplesRow == null) return;
            _samplesRow.SuspendLayout();
            try
            {
                _samplesRow.Controls.Clear();
                foreach (var s in samples)
                {
                    var chip = new Label
                    {
                        Text = s,
                        AutoSize = true,
                        BackColor = BgInput,
                        ForeColor = FgDim,
                        Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                        Padding = new Padding(8, 4, 8, 4),
                        Margin = new Padding(0, 0, 6, 4),
                        BorderStyle = BorderStyle.FixedSingle,
                        Cursor = Cursors.Hand
                    };
                    var captured = s;
                    chip.Click += (sender, e) =>
                    {
                        _promptBox.Text = captured;
                        _promptBox.SelectionStart = _promptBox.Text.Length;
                        _promptBox.Focus();
                    };
                    _samplesRow.Controls.Add(chip);
                }
            }
            finally { _samplesRow.ResumeLayout(); }
            UpdateNowPlayingRow();
        }

        private static IList<string> BuildSamples(DateTime now, string weather)
        {
            var hour = now.Hour;
            var month = now.Month;

            string timeOfDay =
                hour < 6  ? "late night" :
                hour < 12 ? "morning"    :
                hour < 14 ? "midday"     :
                hour < 18 ? "afternoon"  :
                hour < 22 ? "evening"    :
                            "night";

            string season =
                month == 12 || month <= 2 ? "winter" :
                month <= 5                ? "spring" :
                month <= 8                ? "summer" :
                                            "autumn";

            // Three suggestions: time of day, season, and a "smart" pick that
            // either uses weather (if known) or falls back to a generic mood.
            var samples = new List<string>(3);
            samples.Add($"Suggest some {timeOfDay} tracks");
            samples.Add($"Pick a few {season} songs from my library");
            if (!string.IsNullOrEmpty(weather))
                samples.Add($"Find music for a {weather} day");
            else
                samples.Add(hour < 12
                    ? "Recommend something to wake me up"
                    : hour < 18
                        ? "Find me focus music"
                        : "Pick chill tracks to wind down");
            return samples;
        }

        // Best-effort weather fetch from wttr.in. Returns a short adjective
        // ("sunny", "rainy", "snowy", "cloudy", "windy", …) or null on failure.
        // No API key required; uses the IP-based location of the caller.
        private static async Task<string> TryFetchWeatherAsync()
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicBee-AI-Search/1.0");
                // %C = weather condition only, e.g. "Partly cloudy".
                var raw = await http.GetStringAsync("https://wttr.in/?format=%C").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var s = raw.Trim().ToLowerInvariant();
                if (s.Contains("rain") || s.Contains("drizzle") || s.Contains("shower")) return "rainy";
                if (s.Contains("snow") || s.Contains("sleet") || s.Contains("blizzard")) return "snowy";
                if (s.Contains("storm") || s.Contains("thunder")) return "stormy";
                if (s.Contains("fog") || s.Contains("mist") || s.Contains("haze")) return "foggy";
                if (s.Contains("wind") || s.Contains("gale")) return "windy";
                if (s.Contains("clear") || s.Contains("sunny")) return "sunny";
                if (s.Contains("cloud") || s.Contains("overcast")) return "cloudy";
                return null;
            }
            catch { return null; }
        }

        private async Task SendAsync()
        {
            var prompt = (_promptBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt) || !_sendButton.Enabled) return;
            _promptBox.Clear();
            // New user query: drop previous suggestions so the next set the
            // model returns isn't mixed in with stale picks from earlier turns.
            ClearSuggested();
            SetBusy(true);
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            SetStatus("Thinking...");
            AppendTranscript($"{UserPrefix}{prompt}{Environment.NewLine}{Environment.NewLine}{AssistantPrefix}");

            try
            {
                // If the user wants the now-playing track included as context,
                // refresh it (so a track change since the last refresh is picked
                // up) and prepend a system message describing it. Sent as a
                // SYSTEM message rather than user text so it doesn't appear in
                // the visible transcript and doesn't pollute the user prompt.
                if (_useNowPlaying && _nowPlayingTrack != null)
                {
                    RefreshNowPlaying();
                    var ctx = BuildNowPlayingContext(_nowPlayingTrack);
                    if (!string.IsNullOrEmpty(ctx))
                        _bootstrapper.ChatService.AddMessage(new ChatMessage(ChatRole.System, ctx));
                }

                _bootstrapper.ChatService.AddMessage(new ChatMessage(ChatRole.User, prompt));
                var sb = new StringBuilder();
                await foreach (var update in _bootstrapper.ChatService.GetStreamingResponseAsync(_cts.Token))
                {
                    if (string.IsNullOrEmpty(update.Text)) continue;
                    sb.Append(update.Text);
                    AppendTranscript(update.Text);
                }
                _bootstrapper.ChatService.AddMessage(new ChatMessage(ChatRole.Assistant, sb.ToString()));
                AppendTranscript(Environment.NewLine + Environment.NewLine);
                SetStatus("Ready");
            }
            catch (OperationCanceledException) { SetStatus("Cancelled"); }
            catch (Exception ex)
            {
                AppendTranscript($"{Environment.NewLine}[error] {ex.Message}{Environment.NewLine}{Environment.NewLine}");
                SetStatus("Error");
            }
            finally { SetBusy(false); }
        }

        private async Task RebuildIndexAsync()
        {
            var ok = MessageBox.Show(this,
                "Delete the local embedding store and re-index every track in your library? This may take a while.",
                "Rebuild index", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ok != DialogResult.OK) return;

            SetBusy(true);
            SetStatus("Rebuilding index...");
            try
            {
                await _rebuildAllAsync();
                SetStatus("Rebuild started in the background.");
            }
            catch (Exception ex) { SetStatus("Rebuild failed: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private void PromptBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                _ = SendAsync();
            }
        }

        // ---------- helpers (thread-safe) ----------
        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { /* control may be disposed */ }
        }

        private void AppendTranscript(string text)
        {
            RunOnUi(() =>
            {
                _transcriptBox.AppendText(text);
                _transcriptBox.SelectionStart = _transcriptBox.TextLength;
                _transcriptBox.ScrollToCaret();
            });
        }

        private void SetStatus(string text) => RunOnUi(() => _statusLabel.Text = text);

        // Public log sink so Plugin.Trace can surface progress + errors in the
        // chat transcript. Lines are appended verbatim with a timestamp.
        public void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            RunOnUi(() =>
            {
                var stamp = DateTime.Now.ToString("HH:mm:ss");
                _transcriptBox.AppendText($"[{stamp}] {text}{Environment.NewLine}");
                _transcriptBox.SelectionStart = _transcriptBox.TextLength;
                _transcriptBox.ScrollToCaret();
            });
        }

        private void SetBusy(bool busy)
        {
            RunOnUi(() =>
            {
                _sendButton.Enabled = !busy;
                _cancelButton.Enabled = busy;
                _resetButton.Enabled = !busy;
                _rebuildButton.Enabled = !busy;
            });
        }

        private void SetRowVisible(Control rowChild, bool visible, int height)
        {
            // Pure-Dock layout now: just toggle Visible. The Dock=Top child
            // contributes its own intrinsic Height to the layout when visible
            // and contributes nothing when hidden.
            if (rowChild == null) return;
            rowChild.Visible = visible;
            rowChild.Parent?.PerformLayout();
        }

        public void ReportIngestProgress(int done, int total, bool active)
        {
            RunOnUi(() =>
            {
                SetRowVisible(_progressPanel, active, 28);
                if (total > 0)
                {
                    _progressBar.Maximum = 100;
                    _progressBar.Value = Math.Max(0, Math.Min(100, (int)(100.0 * done / total)));
                    _progressLabel.Text = active
                        ? $"Indexing {done:N0} / {total:N0} tracks"
                        : $"Indexed {done:N0} / {total:N0} tracks.";
                }
                else if (active)
                {
                    _progressLabel.Text = "Indexing...";
                }
                // Status bar is for one-off messages; the progress panel
                // already shows ingest state. Clear status when ingest is
                // active so we don't print the same line in two places.
                if (active) _statusLabel.Text = "";
                else if (string.IsNullOrEmpty(_statusLabel.Text)) _statusLabel.Text = "Ready";
            });
        }

        // ---------- suggested-tracks management ----------
        private void OnTracksSuggested(IReadOnlyList<DbTrackRow> tracks)
        {
            if (tracks == null || tracks.Count == 0) return;
            RunOnUi(() =>
            {
                _suggestedList.BeginUpdate();
                try
                {
                    foreach (var t in tracks)
                    {
                        if (string.IsNullOrEmpty(t.Path)) continue;
                        if (!_suggestedPaths.Add(t.Path)) continue;
                        var display = string.IsNullOrEmpty(t.Album)
                            ? $"{t.Artist} - {t.Title}"
                            : $"{t.Artist} - {t.Title}  ({t.Album}{(string.IsNullOrEmpty(t.Year) ? "" : ", " + t.Year)})";
                        var item = new ListViewItem(display) { Checked = true, Tag = t.Path, ToolTipText = display };
                        _suggestedList.Items.Add(item);
                    }
                }
                finally { _suggestedList.EndUpdate(); }
            });
        }

        private string[] CheckedPaths()
        {
            var paths = new List<string>();
            foreach (ListViewItem it in _suggestedList.CheckedItems)
            {
                if (it.Tag is string p) paths.Add(p);
            }
            return paths.ToArray();
        }

        private void SetAllChecked(bool value)
        {
            foreach (ListViewItem it in _suggestedList.Items) it.Checked = value;
        }

        private void ClearSuggested()
        {
            _suggestedList.Items.Clear();
            _suggestedPaths.Clear();
        }

        private static string Escape(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
