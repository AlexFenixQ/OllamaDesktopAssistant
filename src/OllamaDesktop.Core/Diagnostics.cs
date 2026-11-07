using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OllamaDesktop.Core;

public static class Diagnostics
{
    private static readonly object Gate = new();
    public static void Record(AppPaths paths, string code, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                var file = paths.Resolve("Diagnostics/events.ndjson");
                if (File.Exists(file) && new FileInfo(file).Length > 256_000) File.Move(file, file + ".previous", true);
                var entry = new { utc = DateTimeOffset.UtcNow.ToString("O"), code = Regex.Replace(code, "[^A-Za-z0-9_]", "").Take(64).Aggregate("", (a, c) => a + c), exceptionType = exception?.GetType().Name };
                File.AppendAllText(file, JsonDefaults.Write(entry) + "\n");
            }
        }
        catch { /* Diagnostics must never replace the original failure. No content or exception messages are logged. */ }
    }
    public static void Export(AppPaths paths, string destination)
    {
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Text(string name, string value) { var entry = zip.CreateEntry(name); using var writer = new StreamWriter(entry.Open()); writer.Write(value); }
        Text("environment.json", JsonDefaults.Write(new { application = BuildInfo.Version, runtime = Environment.Version.ToString(), os = Environment.OSVersion.Version.ToString(), architecture = RuntimeInformation.OSArchitecture.ToString(), databasePresent = File.Exists(paths.Database), keyPresent = File.Exists(paths.KeyFile), utc = DateTimeOffset.UtcNow.ToString("O") }));
        var log = paths.Resolve("Diagnostics/events.ndjson"); var clean = new List<object>();
        if (File.Exists(log) && new FileInfo(log).Length <= 300_000)
        {
            foreach (var line in File.ReadLines(log).TakeLast(300))
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(line); var value = json.RootElement;
                    var utc = value.GetProperty("utc").GetString() ?? ""; var code = value.GetProperty("code").GetString() ?? "";
                    var type = value.TryGetProperty("exceptionType", out var t) ? t.GetString() ?? "" : "";
                    if (DateTimeOffset.TryParse(utc, out var parsed) && Regex.IsMatch(code, "^[A-Za-z0-9_]{0,64}$") && Regex.IsMatch(type, "^[A-Za-z0-9_]{0,80}$"))
                        clean.Add(new { utc = parsed.ToString("O"), code, exceptionType = type });
                }
                catch { }
            }
        }
        Text("events.json", JsonDefaults.Write(clean));
        Text("README_RU.txt", "Архив содержит только версии среды и коды ошибок. Ключи, переписки, вложения, пути пользователя и полные тексты исключений исключены. Перед отправкой можно открыть архив и проверить его содержимое.");
    }
}
