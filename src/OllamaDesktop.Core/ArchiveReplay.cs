namespace OllamaDesktop.Core;

public static class ArchiveReplay
{
    public static async Task<Attachment> StageAsync(AppPaths paths, string chatId, string source, CancellationToken token)
    {
        FileGuard.CheckSize(source);
        var id = Guid.NewGuid().ToString("N");
        var directory = paths.AttachmentDirectory(chatId, id);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "original.zip");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > Limits.FileBytes) throw new AppException("ARCHIVE_SIZE", "Архив превышает 25 МиБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested(); FileGuard.CheckSize(file);
            return new Attachment { Id = id, Name = Path.GetFileName(source), Bytes = new FileInfo(file).Length,
                RelativeOriginal = Path.GetRelativePath(paths.Root, file),
                Sha256 = await Task.Run(() => AppPaths.Hash(file), token).ConfigureAwait(false) };
        }
        catch
        {
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory)) Directory.Delete(directory);
            throw;
        }
    }
    public static bool IsArchive(Attachment attachment) =>
        Path.GetExtension(attachment.RelativeOriginal).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public static string Resolve(AppPaths paths, string chatId, Attachment attachment)
    {
        var relative = attachment.RelativeOriginal.Replace('\\', '/');
        if (!IsArchive(attachment) ||
            !(relative.StartsWith("Attachments/" + AppPaths.Id(chatId) + "/", StringComparison.Ordinal) ||
              relative.StartsWith("Staging/", StringComparison.Ordinal)))
            throw new AppException("REPLAY_ARCHIVE", "Архив не принадлежит этому чату.");
        var file = paths.Resolve(relative);
        FileGuard.CheckSize(file);
        if (!string.Equals(AppPaths.Hash(file), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new AppException("REPLAY_ARCHIVE", "Сохранённый архив изменился. Прикрепите оригинал заново.");
        return file;
    }
}
