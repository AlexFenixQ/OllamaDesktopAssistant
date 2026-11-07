using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OllamaDesktop.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string Database => Resolve("history.db");
    public string SettingsFile => Resolve("settings.json");
    public string KeyFile => Resolve("key.dpapi");
    public static AppPaths Default() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaDesktopAssistant", "Data"));
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        for (var cursor = Root; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if (Directory.Exists(cursor) || File.Exists(cursor)) AssertNotReparse(cursor);
        Directory.CreateDirectory(Root);
        AssertNotReparse(Root);
        foreach (var dir in new[] { "Attachments", "Generated", "Diagnostics", "Staging" }) Directory.CreateDirectory(Resolve(dir));
    }
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new AppException("PATH_DENIED", "Недопустимый путь к файлу.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or "." || p.Length == 0)) throw new AppException("PATH_DENIED", "Недопустимый путь к файлу.");
        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AppException("PATH_DENIED", "Доступ вне папки приложения запрещён.");
        var cursor = Root;
        AssertNotReparse(cursor);
        foreach (var part in parts)
        {
            cursor = Path.Combine(cursor, part);
            if (File.Exists(cursor) || Directory.Exists(cursor)) AssertNotReparse(cursor);
        }
        return full;
    }
    public static void AssertNotReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new AppException("REPARSE_DENIED", "Ссылки и перенаправления в папках данных не поддерживаются.");
    }
    public static string Id(string id) => Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)
        ? id : throw new AppException("INVALID_ID", "Некорректный идентификатор.");
    public string AttachmentDirectory(string chat, string attachment) => Resolve($"Attachments/{Id(chat)}/{Id(attachment)}");
    public static string SafeName(string text)
    {
        var invalid = "<>:\"/\\|?*";
        var name = new string(text.Where(c => c >= 32 && !invalid.Contains(c)).Take(70).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "Документ";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "_" + name;
        return name;
    }
    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static void AtomicText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
