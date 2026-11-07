using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OllamaDesktop.Core;

public static class ArchiveService
{
    public static Artifact Create(AppPaths paths, string chatId, string requestId, ToolCall call, IReadOnlyList<Attachment> attachments, CancellationToken token, IReadOnlyDictionary<string, Artifact>? generated = null)
    {
        if (call.Name != "create_archive") throw new AppException("TOOL_DENIED", "Запрещённая операция архива.");
        using var doc = JsonDocument.Parse(call.Arguments.GetRawText());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("title" or "files" or "attachmentIds" or "artifactIds")))
            throw new AppException("ZIP_SPEC", "Недопустимая структура архива.");
        var title = StringProperty(root, "title");
        if (title.Length is < 1 or > 120) throw new AppException("ZIP_SPEC", "Недопустимое название архива.");
        if (!root.TryGetProperty("files", out var files)) throw new AppException("ZIP_SPEC", "Отсутствует список files.");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 50) throw new AppException("ZIP_SPEC", "Допускается до 50 файлов в создаваемом архиве.");
        var requested = StringArray(root, "attachmentIds");
        var artifactIds = StringArray(root, "artifactIds");
        if (requested.Length + artifactIds.Length + files.GetArrayLength() == 0) throw new AppException("ZIP_EMPTY", "Нельзя создать пустой архив.");
        if (requested.Length + artifactIds.Length + files.GetArrayLength() > 50) throw new AppException("ZIP_SPEC", "Архив может содержать максимум 50 файлов.");
        var directory = "Generated/" + AppPaths.Id(chatId);
        Directory.CreateDirectory(paths.Resolve(directory));
        var relative = directory + "/" + AppPaths.SafeName(title) + "-" + AppPaths.Id(requestId)[..8] + ".zip";
        var final = paths.Resolve(relative);
        if (File.Exists(final)) throw new AppException("ARTIFACT_EXISTS", "Архив уже существует; перезапись запрещена.");
        var temp = paths.Resolve(directory + "/pending-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total=0;
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in files.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (file.ValueKind != JsonValueKind.Object || file.EnumerateObject().Any(p=>p.Name is not ("name" or "content"))) throw new AppException("ZIP_SPEC", "Недопустимая запись ZIP.");
                    var name = StringProperty(file, "name");
                    var content = StringProperty(file, "content");
                    ValidateName(name, seen);
                    if (!(name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".csv",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)))
                        throw new AppException("ZIP_OUTPUT_FORMAT", "Генерация ZIP поддерживает текстовые TXT, MD, CSV и JSON. DOCX/XLSX/PPTX добавляются только из существующих вложений.");
                    var data=Encoding.UTF8.GetBytes(content);
                    if (data.Length > 2_000_000) throw new AppException("ZIP_ENTRY_LIMIT", "Текстовый файл превышает 2 МиБ.");
                    total+=data.Length;
                    using var output=zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    output.Write(data);
                }
                foreach (var id in artifactIds)
                {
                    token.ThrowIfCancellationRequested();
                    if (generated is null || !generated.TryGetValue(id, out var artifact))
                        throw new AppException("ZIP_ARTIFACT", "Архив может включать только созданные в текущем запросе файлы.");
                    ValidateName(artifact.Name, seen);
                    var source = paths.Resolve(artifact.RelativePath);
                    if (AppPaths.Hash(source) != artifact.Sha256) throw new AppException("ZIP_ARTIFACT_CHANGED", "Созданный файл изменился.");
                    var length = new FileInfo(source).Length;
                    total += length;
                    if (length > Limits.FileBytes || total > Limits.ExpandedBytes)
                        throw new AppException("ZIP_SIZE", "Файлы не помещаются в допустимый размер ZIP.");
                    zip.CreateEntryFromFile(source, artifact.Name, CompressionLevel.Optimal);
                }
                foreach (var id in requested)
                {
                    token.ThrowIfCancellationRequested();
                    var attachment=attachments.SingleOrDefault(a => a.Id == id) ?? throw new AppException("ZIP_ATTACHMENT", "В архив можно добавить только выбранное вложение.");
                    var name=attachment.Name;
                    ValidateName(name,seen);
                    var path=paths.Resolve(attachment.RelativeOriginal);
                    if (AppPaths.Hash(path) != attachment.Sha256) throw new AppException("ZIP_ATTACHMENT_CHANGED", "Исходное вложение изменено.");
                    var info=new FileInfo(path);
                    if (info.Length>Limits.FileBytes) throw new AppException("ZIP_ENTRY_LIMIT", "Вложение слишком велико.");
                    total+=info.Length;
                    if (total>Limits.ExpandedBytes) throw new AppException("ZIP_EXPANSION", "Архив превышает безопасный общий размер.");
                    zip.CreateEntryFromFile(path,name,CompressionLevel.Optimal);
                }
            }
            if (total > Limits.ExpandedBytes || new FileInfo(temp).Length > Limits.FileBytes) throw new AppException("ZIP_SIZE", "Результат превышает 25 МиБ.");
            using (var verify=ZipFile.OpenRead(temp))
            {
                foreach (var entry in verify.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    using var input=entry.Open();
                    var buffer=new byte[81920]; long length=0; int read;
                    while ((read=input.Read(buffer,0,buffer.Length))>0) { length+=read; if (length>Limits.FileBytes) throw new AppException("ZIP_CORRUPT", "Недопустимая запись архива."); }
                    if (length!=entry.Length) throw new AppException("ZIP_CORRUPT", "Ошибка проверки ZIP.");
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp,final,false);
            return new Artifact { Name=Path.GetFileName(final), RelativePath=relative, Format=".zip",Bytes=new FileInfo(final).Length,Sha256=AppPaths.Hash(final), Validation="ZIP проверен; содержимое подготовлено по запросу. Проверьте факты." };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static Artifact BundleGenerated(AppPaths paths, string chatId, string requestId, IReadOnlyList<Artifact> files, CancellationToken token)
    {
        if (files.Count == 0 || files.Count > 50) throw new AppException("ZIP_SPEC", "Невозможно сформировать архив из выбранных файлов.");
        var ids = new Dictionary<string, Artifact>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++) ids["generated_" + (i + 1)] = files[i];
        using var doc = JsonDocument.Parse(JsonDefaults.Write(new { title = "Созданные файлы", files = Array.Empty<object>(), attachmentIds = Array.Empty<string>(), artifactIds = ids.Keys.ToArray() }));
        return Create(paths, chatId, requestId, new ToolCall("create_archive", doc.RootElement.Clone()), [], token, ids);
    }
    private static void ValidateName(string name, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Contains('/') || name.Contains('\\') || name.Contains(':') ||
            name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            Path.GetFileName(name) != name || !seen.Add(name))
            throw new AppException("ZIP_PATH", "Недопустимое или повторяющееся имя внутри ZIP.");
    }
    private static string StringProperty(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new AppException("ZIP_SPEC", "Поле " + name + " должно быть строкой.");
        return property.GetString()!;
    }
    private static string[] StringArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return [];
        if (property.ValueKind != JsonValueKind.Array || property.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            throw new AppException("ZIP_SPEC", "Поле " + name + " должно быть массивом строк.");
        return property.EnumerateArray().Select(x => x.GetString()!).ToArray();
    }
}
