using MusicBee.AI.Search.AI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBee.AI.Search.Ui.WinForms
{
    /// <summary>
    /// Standalone modeless settings window. Replaces the inline settings panel
    /// previously embedded in <see cref="ChatPanel"/>. Sections (Chat,
    /// Embeddings, Pacing) each contain provider-specific fields that toggle
    /// based on the selected provider.
    ///
    /// Uses TableLayoutPanel exclusively for sizing (a previous attempt with
    /// FlowLayoutPanel + Dock=Top rendered every group as an empty box because
    /// FlowLayoutPanel ignores DockStyle on its children and the rows had no
    /// intrinsic size).
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private static SettingsForm _instance;
        public static void ShowSingleton(IWin32Window owner, Bootstrapper bootstrapper, MbTheme theme)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.BringToFront();
                _instance.Focus();
                return;
            }
            _instance = new SettingsForm(bootstrapper, theme);
            _instance.FormClosed += (_, __) => _instance = null;
            if (owner != null) _instance.Show(owner);
            else               _instance.Show();
        }

        private readonly Bootstrapper _bootstrapper;
        private readonly MbTheme _theme;

        // chat
        private ComboBox _chatProvider;
        private TextBox _chatGhEndpoint, _chatGhModel, _chatGhToken;
        private TextBox _chatOllamaEndpoint;
        private ComboBox _chatOllamaModel;
        private TableLayoutPanel _chatGhPanel, _chatOllamaPanel;

        // embeddings
        private ComboBox _embProvider;
        private TextBox _embGhEndpoint, _embGhModel;
        private NumericUpDown _embGhDim;
        private TextBox _embOllamaEndpoint;
        private ComboBox _embOllamaModel;
        private TableLayoutPanel _embGhPanel, _embOllamaPanel;

        // pacing
        private NumericUpDown _maxRpm, _minRpm, _batchSize;

        // footer
        private Button _saveBtn, _cancelBtn, _testBtn, _refreshBtn;
        private Label _statusLabel;

        private SettingsForm(Bootstrapper bootstrapper, MbTheme theme)
        {
            _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
            _theme = theme ?? MbTheme.Default;

            Text = "AI Library Search — Settings";
            StartPosition = FormStartPosition.CenterParent;
            Width = 760;
            Height = 820;
            MinimumSize = new Size(620, 780);
            BackColor = _theme.Background;
            ForeColor = _theme.Foreground;
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            BuildUi();
            LoadFromSettings();
        }

        // ---- UI construction ----
        private void BuildUi()
        {
            SuspendLayout();

            // Footer is docked first (bottom), then groups Top-stacked from
            // bottom upward so layout order matches z-order.
            Controls.Add(BuildFooter());
            Controls.Add(BuildPacingGroup());
            Controls.Add(BuildEmbeddingsGroup());
            Controls.Add(BuildChatGroup());

            ResumeLayout(true);
        }

        private GroupBox BuildChatGroup()
        {
            var g = NewGroup("Chat", height: 240);

            // Parent grid: row 0 = provider combo, row 1 = sub-host (fills remaining space).
            // Must be exactly 2 rows so the sub-host gets all leftover height; otherwise
            // extra absolute rows steal space and the bottom of the sub-panel gets clipped.
            var grid = NewFormGrid(rows: 2);
            _chatProvider = NewProviderCombo();
            _chatProvider.SelectedIndexChanged += (_, __) => RefreshChatVisibility();
            AddRow(grid, 0, "Provider", _chatProvider);

            // Two stacked sub-panels; only one is visible based on provider.
            _chatGhPanel = NewFormGrid(rows: 3);
            _chatGhPanel.Dock = DockStyle.None;
            _chatGhEndpoint = NewTextBox();
            _chatGhModel = NewTextBox();
            _chatGhToken = NewTextBox();
            _chatGhToken.UseSystemPasswordChar = true;
            AddRow(_chatGhPanel, 0, "Endpoint", _chatGhEndpoint);
            AddRow(_chatGhPanel, 1, "Chat model", _chatGhModel);
            AddRow(_chatGhPanel, 2, "GitHub PAT", _chatGhToken);

            _chatOllamaPanel = NewFormGrid(rows: 2);
            _chatOllamaPanel.Dock = DockStyle.None;
            _chatOllamaEndpoint = NewTextBox();
            _chatOllamaModel = NewModelCombo();
            _refreshBtn = NewButton("Refresh", width: 90);
            _refreshBtn.Click += async (_, __) => await RefreshOllamaModelsAsync();
            AddRow(_chatOllamaPanel, 0, "Endpoint (/v1)", _chatOllamaEndpoint);
            AddRow(_chatOllamaPanel, 1, "Model", _chatOllamaModel, _refreshBtn);

            // Host both sub-panels in a single cell that spans both columns.
            var subHost = new Panel { Dock = DockStyle.Fill, BackColor = _theme.Background };
            _chatGhPanel.Dock = DockStyle.Fill;
            _chatOllamaPanel.Dock = DockStyle.Fill;
            subHost.Controls.Add(_chatGhPanel);
            subHost.Controls.Add(_chatOllamaPanel);
            grid.SetColumnSpan(subHost, 2);
            grid.Controls.Add(subHost, 0, 1);
            grid.RowStyles[1] = new RowStyle(SizeType.Percent, 100);

            g.Controls.Add(grid);
            return g;
        }

        private GroupBox BuildEmbeddingsGroup()
        {
            var g = NewGroup("Embeddings", height: 240);

            var grid = NewFormGrid(rows: 2);
            _embProvider = NewProviderCombo();
            _embProvider.SelectedIndexChanged += (_, __) => RefreshEmbVisibility();
            AddRow(grid, 0, "Provider", _embProvider);

            _embGhPanel = NewFormGrid(rows: 3);
            _embGhPanel.Dock = DockStyle.None;
            _embGhEndpoint = NewTextBox();
            _embGhModel = NewTextBox();
            _embGhDim = NewNumeric(1, 8192);
            AddRow(_embGhPanel, 0, "Endpoint", _embGhEndpoint);
            AddRow(_embGhPanel, 1, "Embedding model", _embGhModel);
            AddRow(_embGhPanel, 2, "Dimensions", _embGhDim);

            _embOllamaPanel = NewFormGrid(rows: 2);
            _embOllamaPanel.Dock = DockStyle.None;
            _embOllamaEndpoint = NewTextBox();
            _embOllamaModel = NewModelCombo();
            AddRow(_embOllamaPanel, 0, "Endpoint (/v1)", _embOllamaEndpoint);
            AddRow(_embOllamaPanel, 1, "Model", _embOllamaModel);

            var subHost = new Panel { Dock = DockStyle.Fill, BackColor = _theme.Background };
            _embGhPanel.Dock = DockStyle.Fill;
            _embOllamaPanel.Dock = DockStyle.Fill;
            subHost.Controls.Add(_embGhPanel);
            subHost.Controls.Add(_embOllamaPanel);
            grid.SetColumnSpan(subHost, 2);
            grid.Controls.Add(subHost, 0, 1);
            grid.RowStyles[1] = new RowStyle(SizeType.Percent, 100);

            g.Controls.Add(grid);
            return g;
        }

        private GroupBox BuildPacingGroup()
        {
            var g = NewGroup("Request pacing", height: 150);
            var grid = NewFormGrid(rows: 3);
            _maxRpm = NewNumeric(1, 6000);
            _minRpm = NewNumeric(1, 6000);
            _batchSize = NewNumeric(1, 1024);
            AddRow(grid, 0, "Max RPM", _maxRpm);
            AddRow(grid, 1, "Min RPM", _minRpm);
            AddRow(grid, 2, "Batch size", _batchSize);
            g.Controls.Add(grid);
            return g;
        }

        private Panel BuildFooter()
        {
            var p = new Panel { Dock = DockStyle.Bottom, Height = 88, BackColor = _theme.Background, Padding = new Padding(12, 6, 12, 12) };
            _statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = _theme.ForegroundDim,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = _theme.Background,
                Padding = new Padding(0, 4, 0, 0)
            };
            _saveBtn = NewButton("Save");
            _saveBtn.Click += async (_, __) => await SaveAsync();
            _cancelBtn = NewButton("Cancel");
            _cancelBtn.Click += (_, __) => Close();
            _testBtn = NewButton("Test connection", width: 140);
            _testBtn.Click += async (_, __) => await TestConnectionAsync();
            btnRow.Controls.Add(_saveBtn);
            btnRow.Controls.Add(_cancelBtn);
            btnRow.Controls.Add(_testBtn);
            p.Controls.Add(btnRow);
            p.Controls.Add(_statusLabel);
            return p;
        }

        // ---- Builders ----
        private GroupBox NewGroup(string title, int height) => new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = height,
            ForeColor = _theme.Foreground,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(0, 0, 0, 8)
        };

        // 2-column grid: label (200px) + control (fill). Rows are 36 px tall
        // so a 9 pt TextBox + its 4/4 vertical margin actually fits.
        private TableLayoutPanel NewFormGrid(int rows)
        {
            var g = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows,
                BackColor = _theme.Background,
                AutoSize = false
            };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++)
                g.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            return g;
        }

        private void AddRow(TableLayoutPanel grid, int row, string label, Control control, Control extra = null)
        {
            var lbl = new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = _theme.Foreground,
                AutoSize = false
            };
            grid.Controls.Add(lbl, 0, row);

            if (extra != null)
            {
                var rowHost = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1,
                    BackColor = _theme.Background,
                    AutoSize = false
                };
                rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                rowHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                control.Dock = DockStyle.Fill;
                extra.Dock = DockStyle.Fill;
                extra.Margin = new Padding(4, 0, 0, 0);
                rowHost.Controls.Add(control, 0, 0);
                rowHost.Controls.Add(extra, 1, 0);
                grid.Controls.Add(rowHost, 1, row);
            }
            else
            {
                control.Dock = DockStyle.Fill;
                control.Margin = new Padding(0, 4, 0, 4);
                grid.Controls.Add(control, 1, row);
            }
        }

        private TextBox NewTextBox() => new TextBox
        {
            BackColor = _theme.InputBackground,
            ForeColor = _theme.Foreground,
            BorderStyle = BorderStyle.FixedSingle
        };

        private NumericUpDown NewNumeric(int min, int max) => new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            BackColor = _theme.InputBackground,
            ForeColor = _theme.Foreground,
            BorderStyle = BorderStyle.FixedSingle
        };

        private ComboBox NewProviderCombo()
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = _theme.InputBackground,
                ForeColor = _theme.Foreground,
                FlatStyle = FlatStyle.Flat
            };
            c.Items.Add("GitHubModels");
            c.Items.Add("Ollama");
            return c;
        }

        private ComboBox NewModelCombo() => new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = _theme.InputBackground,
            ForeColor = _theme.Foreground,
            FlatStyle = FlatStyle.Flat
        };

        private Button NewButton(string text, int width = 100)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 28,
                BackColor = _theme.ButtonBackground,
                ForeColor = _theme.Foreground,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(4)
            };
            b.FlatAppearance.BorderColor = _theme.Border;
            return b;
        }

        // ---- Load / Save / Apply ----
        private void LoadFromSettings()
        {
            var s = _bootstrapper.Settings;
            _chatProvider.SelectedItem = s.ChatProvider == "Ollama" ? "Ollama" : "GitHubModels";
            _chatGhEndpoint.Text = s.Endpoint;
            _chatGhModel.Text = s.ChatModel;
            _chatGhToken.Text = s.Token;
            _chatOllamaEndpoint.Text = s.OllamaEndpoint;
            _chatOllamaModel.Text = s.OllamaChatModel ?? "";

            _embProvider.SelectedItem = s.EmbeddingsProvider == "Ollama" ? "Ollama" : "GitHubModels";
            _embGhEndpoint.Text = s.Endpoint;
            _embGhModel.Text = s.EmbeddingModel;
            _embGhDim.Value = Clamp(s.EmbeddingDimensions, (int)_embGhDim.Minimum, (int)_embGhDim.Maximum);
            _embOllamaEndpoint.Text = s.OllamaEndpoint;
            _embOllamaModel.Text = s.OllamaEmbeddingModel ?? "";

            _maxRpm.Value = Clamp(s.MaxRequestsPerMinute, (int)_maxRpm.Minimum, (int)_maxRpm.Maximum);
            _minRpm.Value = Clamp(s.MinRequestsPerMinute, (int)_minRpm.Minimum, (int)_minRpm.Maximum);
            _batchSize.Value = Clamp(s.EmbeddingBatchSize, (int)_batchSize.Minimum, (int)_batchSize.Maximum);

            RefreshChatVisibility();
            RefreshEmbVisibility();
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private void RefreshChatVisibility()
        {
            var ollama = (string)_chatProvider.SelectedItem == "Ollama";
            _chatGhPanel.Visible = !ollama;
            _chatOllamaPanel.Visible = ollama;
        }

        private void RefreshEmbVisibility()
        {
            var ollama = (string)_embProvider.SelectedItem == "Ollama";
            _embGhPanel.Visible = !ollama;
            _embOllamaPanel.Visible = ollama;
        }

        private async Task RefreshOllamaModelsAsync()
        {
            var endpoint = (_chatOllamaEndpoint.Text ?? "").Trim();
            if (string.IsNullOrEmpty(endpoint)) endpoint = (_embOllamaEndpoint.Text ?? "").Trim();
            if (string.IsNullOrEmpty(endpoint)) { SetStatus("Set an Ollama endpoint first."); return; }
            SetStatus("Refreshing models…");
            try
            {
                var lister = new OllamaModelLister();
                var models = await lister.ListModelsAsync(endpoint, CancellationToken.None).ConfigureAwait(true);
                FillCombo(_chatOllamaModel, models, _bootstrapper.Settings.OllamaChatModel);
                FillCombo(_embOllamaModel,  models, _bootstrapper.Settings.OllamaEmbeddingModel);
                SetStatus($"Loaded {models.Count} model(s).");
            }
            catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
        }

        private static void FillCombo(ComboBox cb, IReadOnlyList<string> items, string preserve)
        {
            var keep = string.IsNullOrEmpty(cb.Text) ? preserve : cb.Text;
            cb.Items.Clear();
            foreach (var i in items) cb.Items.Add(i);
            if (!string.IsNullOrEmpty(keep)) cb.Text = keep;
        }

        private async Task SaveAsync()
        {
            try
            {
                var s = CloneSettings(_bootstrapper.Settings);
                s.ChatProvider = (string)_chatProvider.SelectedItem ?? "GitHubModels";
                s.EmbeddingsProvider = (string)_embProvider.SelectedItem ?? "GitHubModels";

                // GitHub Models endpoint is shared by both lanes; whichever
                // panel is visible owns the value (the hidden one would still
                // show the same text since LoadFromSettings populated both).
                s.Endpoint = (_chatGhEndpoint.Visible ? _chatGhEndpoint.Text : _embGhEndpoint.Text)?.Trim() ?? s.Endpoint;
                s.ChatModel = (_chatGhModel.Text ?? "").Trim();
                s.Token = (_chatGhToken.Text ?? "").Trim();
                s.EmbeddingModel = (_embGhModel.Text ?? "").Trim();
                s.EmbeddingDimensions = (int)_embGhDim.Value;

                s.OllamaEndpoint = (_chatOllamaEndpoint.Visible ? _chatOllamaEndpoint.Text : _embOllamaEndpoint.Text)?.Trim() ?? s.OllamaEndpoint;
                s.OllamaChatModel = (_chatOllamaModel.Text ?? "").Trim();
                s.OllamaEmbeddingModel = (_embOllamaModel.Text ?? "").Trim();

                s.MaxRequestsPerMinute = (int)_maxRpm.Value;
                s.MinRequestsPerMinute = (int)_minRpm.Value;
                if (s.MinRequestsPerMinute > s.MaxRequestsPerMinute)
                    s.MinRequestsPerMinute = s.MaxRequestsPerMinute;
                s.EmbeddingBatchSize = (int)_batchSize.Value;

                SetStatus("Applying…");
                await _bootstrapper.ApplyChangedSettingsAsync(s).ConfigureAwait(true);
                SetStatus("Saved.");
                Close();
            }
            catch (Exception ex) { SetStatus("Save failed: " + ex.Message); }
        }

        private async Task TestConnectionAsync()
        {
            _testBtn.Enabled = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                // Test what the user sees on screen, not what was last saved.
                var chatProvider  = (string)_chatProvider.SelectedItem ?? "GitHubModels";
                var embProvider   = (string)_embProvider.SelectedItem  ?? "GitHubModels";

                // ---- Chat ping ----
                SetStatus($"Testing chat ({chatProvider})…");
                Uri chatEp; string chatModel; Func<string> chatToken;
                if (chatProvider == "Ollama")
                {
                    chatEp    = new Uri((_chatOllamaEndpoint.Text ?? "").Trim());
                    chatModel = ((string)_chatOllamaModel.SelectedItem ?? _chatOllamaModel.Text ?? "").Trim();
                    chatToken = () => null;
                }
                else
                {
                    chatEp    = new Uri((_chatGhEndpoint.Text ?? "").Trim());
                    chatModel = (_chatGhModel.Text ?? "").Trim();
                    var tok   = (_chatGhToken.Text ?? "").Trim();
                    chatToken = () => tok;
                }
                if (string.IsNullOrEmpty(chatModel)) { SetStatus("Test failed: chat model is empty."); return; }

                int chars = 0;
                using (var chatClient = new OpenAiCompatibleChatClient(chatEp, chatModel, chatToken,
                           maxRequestsPerMinute: 60, minRequestsPerMinute: 4))
                {
                    var msg = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "ping");
                    var resp = await chatClient.GetResponseAsync(new[] { msg }, options: null, cancellationToken: cts.Token).ConfigureAwait(true);
                    chars = resp?.Text?.Length ?? 0;
                }

                // ---- Embeddings ping ----
                SetStatus($"Chat OK ({chars} chars). Testing embeddings ({embProvider})…");
                Uri embEp; string embModel; Func<string> embToken; int embDim;
                if (embProvider == "Ollama")
                {
                    embEp    = new Uri((_embOllamaEndpoint.Text ?? "").Trim());
                    embModel = ((string)_embOllamaModel.SelectedItem ?? _embOllamaModel.Text ?? "").Trim();
                    embToken = () => null;
                    embDim   = 0;
                }
                else
                {
                    embEp    = new Uri((_embGhEndpoint.Text ?? "").Trim());
                    embModel = (_embGhModel.Text ?? "").Trim();
                    var tok2 = (_chatGhToken.Text ?? "").Trim();
                    embToken = () => tok2;
                    embDim   = (int)_embGhDim.Value;
                }
                if (string.IsNullOrEmpty(embModel)) { SetStatus("Test failed: embedding model is empty."); return; }

                int dims = 0;
                using (var embClient = new OpenAiCompatibleEmbeddingGenerator(embEp, embModel, embToken, embDim,
                           maxRequestsPerMinute: 60, minRequestsPerMinute: 4, batchSize: 1))
                {
                    var emb = await embClient.GenerateAsync(new[] { "ping" }, options: null, cancellationToken: cts.Token).ConfigureAwait(true);
                    dims = emb?.FirstOrDefault()?.Vector.Length ?? 0;
                }

                SetStatus($"OK — chat ({chars} chars), embeddings ({dims} dims).");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Test timed out after 20 s — endpoint unreachable or model not loaded.");
            }
            catch (Exception ex)
            {
                SetStatus("Test failed: " + ex.Message);
            }
            finally
            {
                _testBtn.Enabled = true;
            }
        }

        private void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), text); return; }
            _statusLabel.Text = text;
        }

        private static Settings CloneSettings(Settings src) => new Settings
        {
            Endpoint = src.Endpoint,
            ChatModel = src.ChatModel,
            EmbeddingModel = src.EmbeddingModel,
            EmbeddingDimensions = src.EmbeddingDimensions,
            Token = src.Token,
            ChatProvider = src.ChatProvider,
            EmbeddingsProvider = src.EmbeddingsProvider,
            OllamaEndpoint = src.OllamaEndpoint,
            OllamaChatModel = src.OllamaChatModel,
            OllamaEmbeddingModel = src.OllamaEmbeddingModel,
            MaxRequestsPerMinute = src.MaxRequestsPerMinute,
            MinRequestsPerMinute = src.MinRequestsPerMinute,
            EmbeddingBatchSize = src.EmbeddingBatchSize,
        };
    }
}
