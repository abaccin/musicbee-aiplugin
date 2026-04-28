using MusicBee.AI.Search;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBee.AI.UI
{
    public partial class Form1 : Form
    {
        private readonly Bootstrapper _bootstrapper;

        public Form1()
        {
            InitializeComponent();
            var dataDir = Path.Combine(Environment.CurrentDirectory, "data");
            Directory.CreateDirectory(dataDir);
            _bootstrapper = new Bootstrapper(dataDir);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _bootstrapper?.Dispose();
            base.OnFormClosed(e);
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select a folder to ingest" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                button1.Enabled = false;
                try
                {
                    await IngestFolder(dlg.SelectedPath);
                    MessageBox.Show(this, "Done.", "Ingest", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Ingest failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    button1.Enabled = true;
                }
            }
        }

        private async Task IngestFolder(string folder)
        {
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".ogg" || ext == ".wav";
                });

            foreach (var path in files)
            {
                try
                {
                    var file = TagLib.File.Create(path);
                    await _bootstrapper.TrackIngestor.IngestTrackAsync(new DbTrackRow
                    {
                        Path = file.Name,
                        Title = file.Tag.Title ?? "",
                        Artist = file.Tag.FirstPerformer ?? "",
                        Album = file.Tag.Album ?? "",
                        Genre = file.Tag.Genres.FirstOrDefault() ?? "",
                        Year = file.Tag.Year.ToString(),
                        Comment = file.Tag.Comment ?? ""
                    });
                }
                catch
                {
                    // skip files that taglib can't read
                }
            }
        }
    }
}
