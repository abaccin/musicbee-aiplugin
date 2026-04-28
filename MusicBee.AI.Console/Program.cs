using Microsoft.Extensions.AI;
using MusicBee.AI.Search;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicBee.AI.Console
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);

            var settingsPath = Path.Combine(dataDir, "musicbee_ai_search", "settings.json");
            var settings = Settings.Load(settingsPath);
            if (string.IsNullOrWhiteSpace(settings.Token))
            {
                System.Console.Error.WriteLine(
                    "ERROR: no GitHub token. Set the GITHUB_TOKEN environment variable or edit settings.json.");
                System.Console.Error.WriteLine($"settings.json path: {settingsPath}");
                return;
            }

            using var bootstrapper = new Bootstrapper(dataDir, settings);

            System.Console.WriteLine("MusicBee AI Console (GitHub Models backend)");
            System.Console.WriteLine("Commands:  exit | clear | ingest <folder>");
            System.Console.WriteLine(new string('-', 60));

            while (true)
            {
                System.Console.Write("\nYou: ");
                var input = System.Console.ReadLine();
                if (input is null) break;
                input = input.Trim();
                if (input.Length == 0) continue;

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    bootstrapper.ChatService.Reset();
                    System.Console.WriteLine("(history cleared)");
                    continue;
                }
                if (input.StartsWith("ingest", StringComparison.OrdinalIgnoreCase))
                {
                    var folder = input.Length > 7 ? input.Substring(7).Trim().Trim('"') : "";
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    {
                        System.Console.WriteLine("Usage: ingest <existing folder path>");
                        continue;
                    }
                    await IngestFolder(bootstrapper, folder);
                    continue;
                }

                bootstrapper.ChatService.AddMessage(new ChatMessage(ChatRole.User, input));
                System.Console.Write("Assistant: ");
                var sb = new System.Text.StringBuilder();
                try
                {
                    await foreach (var update in bootstrapper.ChatService.GetStreamingResponseAsync())
                    {
                        if (string.IsNullOrEmpty(update.Text)) continue;
                        sb.Append(update.Text);
                        System.Console.Write(update.Text);
                    }
                    bootstrapper.ChatService.AddMessage(new ChatMessage(ChatRole.Assistant, sb.ToString()));
                    System.Console.WriteLine();
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"\nError: {ex.Message}");
                }
            }
        }

        private static async Task IngestFolder(Bootstrapper bootstrapper, string folder)
        {
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var e = Path.GetExtension(f).ToLowerInvariant();
                    return e == ".mp3" || e == ".flac" || e == ".m4a" || e == ".ogg" || e == ".wav" || e == ".wma";
                }).ToList();

            System.Console.WriteLine($"Found {files.Count} audio files. Ingesting...");
            int done = 0;
            foreach (var path in files)
            {
                try
                {
                    var file = TagLib.File.Create(path);
                    var row = new DbTrackRow
                    {
                        Path = file.Name,
                        Title = file.Tag.Title ?? "",
                        Artist = file.Tag.FirstPerformer ?? "",
                        Album = file.Tag.Album ?? "",
                        Genre = file.Tag.Genres.FirstOrDefault() ?? "",
                        Year = file.Tag.Year.ToString(),
                        Comment = file.Tag.Comment ?? ""
                    };
                    await bootstrapper.TrackIngestor.IngestTrackAsync(row);
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"  ! {Path.GetFileName(path)}: {ex.Message}");
                }
                if (++done % 25 == 0) System.Console.WriteLine($"  {done}/{files.Count}");
            }
            System.Console.WriteLine($"Done: {done}/{files.Count}");
        }
    }
}
