using System.Diagnostics;
using System.IO;
using OllamaDesktop.Core;

namespace OllamaDesktop.App.Infrastructure;

public static class SafeShell
{
    public static void Web(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new AppException("URL_DENIED", "Открываются только HTTPS-ссылки без встроенных учётных данных.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    public static string CheckArtifact(AppPaths paths, Artifact artifact)
    {
        if (!artifact.RelativePath.Replace('\\', '/').StartsWith("Generated/", StringComparison.Ordinal)) throw new AppException("ARTIFACT_PATH", "Недопустимый путь результата.");
        var path = paths.Resolve(artifact.RelativePath);
        if (!new[] { ".docx", ".xlsx", ".pptx", ".zip" }.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new AppException("ARTIFACT_TYPE", "Открытие такого результата запрещено.");
        if (!File.Exists(path)) throw new AppException("ARTIFACT_MISSING", "Файл перемещён или удалён.");
        if (!AppPaths.Hash(path).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new AppException("ARTIFACT_CHANGED", "Файл изменился после создания. Автоматическое открытие остановлено; проверьте файл в папке результатов.");
        return path;
    }
    public static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public static void Folder(string path) => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
}
