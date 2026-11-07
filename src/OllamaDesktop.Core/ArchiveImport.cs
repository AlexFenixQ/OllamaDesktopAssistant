using System.IO.Compression;
using OllamaDesktop.Core;

namespace OllamaDesktop.Core;

/// <summary>Bounded, local ZIP expansion. Files then go through the existing isolated import worker.</summary>
public sealed record ArchiveMember(string Path, string DisplayName);
public sealed record ArchiveExpansion(IReadOnlyList<ArchiveMember> Files, IReadOnlyList<string> Skipped);
public static class ArchiveImport
{
    public static async Task<ArchiveExpansion> ExpandDetailedAsync(string archivePath, string stagingDirectory, int availableSlots, CancellationToken token)
    {
        return await ExpandCoreAsync(archivePath, stagingDirectory, availableSlots, token).ConfigureAwait(false);
    }
    public static async Task<IReadOnlyList<string>> ExpandAsync(string archivePath, string stagingDirectory, int availableSlots, CancellationToken token)
    {
        var detail = await ExpandCoreAsync(archivePath, stagingDirectory, availableSlots, token).ConfigureAwait(false);
        return detail.Files.Select(f => f.Path).ToArray();
    }
    private static async Task<ArchiveExpansion> ExpandCoreAsync(string archivePath, string stagingDirectory, int availableSlots, CancellationToken token)
    {
        if (availableSlots <= 0) throw new AppException("ATTACHMENT_COUNT", "Допускается до 50 файлов за запрос.");
        FileGuard.CheckSize(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 150) throw new AppException("ZIP_ENTRIES", "В архиве больше 150 записей. Разделите архив.");
        var files = new List<(ZipArchiveEntry Entry, string Name)>();
        var skipped = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name.EndsWith('/')) continue;
            if (name.Length > 240 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
                name.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')) ||
                (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000))
                throw new AppException("ZIP_PATH", "В архиве обнаружен недопустимый путь, ссылка или имя.");
            if (name.Split('/').Length > 12 || !unique.Add(name)) throw new AppException("ZIP_PATH", "Повторяющиеся или слишком вложенные пути ZIP.");
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".zip") throw new AppException("ZIP_NESTED", "Вложенные ZIP не распаковываются автоматически.");
            try { FileGuard.Extension(name); }
            catch (AppException e) when (e.Code == "FORMAT_UNSUPPORTED") { skipped.Add(name); continue; }
            if (entry.Length == 0 || entry.Length > Limits.FileBytes) throw new AppException("ZIP_FILE_SIZE", "В архиве есть пустой или слишком большой файл.");
            expanded = checked(expanded + entry.Length);
            if (expanded > Limits.ExpandedBytes || (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1,entry.CompressedLength) > 1000))
                throw new AppException("ZIP_EXPANSION", "Архив превышает ограничения безопасной распаковки.");
            files.Add((entry,name));
        }
        if (files.Count == 0) throw new AppException("ZIP_EMPTY", "Архив не содержит поддерживаемых файлов.");
        if (files.Count > availableSlots) throw new AppException("ATTACHMENT_COUNT", "В архиве больше файлов, чем свободных мест (всего допускается 50).");
        Directory.CreateDirectory(stagingDirectory);
        var extracted = new List<ArchiveMember>();
        for (var i=0; i<files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = files[i].Entry;
            var target = Path.Combine(stagingDirectory, i.ToString("D3") + "-" + Path.GetFileName(files[i].Name));
            await using (var input=entry.Open())
            await using (var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
            {
                var buffer=new byte[81920]; long bytes=0; int read;
                while ((read=await input.ReadAsync(buffer,token))>0)
                {
                    bytes+=read;
                    if (bytes>entry.Length || bytes>Limits.FileBytes) throw new AppException("ZIP_SIZE_CHANGED", "Размер содержимого ZIP не совпал с объявленным.");
                    await output.WriteAsync(buffer.AsMemory(0,read),token);
                }
                if (bytes!=entry.Length) throw new AppException("ZIP_CORRUPT", "ZIP содержит неполный файл.");
            }
            extracted.Add(new ArchiveMember(target, Path.GetFileName(files[i].Name)));
        }
        return new ArchiveExpansion(extracted, skipped);
    }
}
