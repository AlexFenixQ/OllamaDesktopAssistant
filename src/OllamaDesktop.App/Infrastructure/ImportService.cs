using System.Diagnostics;
using System.IO;
using System.Text;
using OllamaDesktop.Core;

namespace OllamaDesktop.App.Infrastructure;

public sealed class ImportService(AppPaths paths)
{
    public async Task<Attachment> ImportAsync(string chatId, string source, CancellationToken token, string? displayName = null, IProgress<string>? progress = null)
    {
        var extension = FileGuard.Extension(source); FileGuard.CheckSize(source); var id = Guid.NewGuid().ToString("N");
        if(FileGuard.IsMedia(source)) {
            var info=await MediaTools.ProbeAsync(source,token);
            if(info.Audio)await new SpeechModelStore(paths).EnsureAsync(new SettingsStore(paths).Load().SpeechModel,progress,token);
        }
        var directory = paths.AttachmentDirectory(chatId, id); Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "original" + extension);
        try
        {
            var free=new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
            if(free < new FileInfo(source).Length + 256L*1024*1024) throw new AppException("IMPORT_DISK","Недостаточно места для копии записи или документа.");
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; var total = 0L; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read; if (total > (FileGuard.IsMedia(source) ? Limits.MediaBytes : Limits.FileBytes)) throw new AppException("COPY_SIZE", "Во время чтения файл превысил допустимый размер.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            return await ProcessAsync(chatId,id,displayName ?? Path.GetFileName(source),extension,new SettingsStore(paths).Load().SpeechModel,progress,token);
        }
        catch(Exception error)
        {
            if(FileGuard.IsMedia(destination)) {
                var saved=new MediaExtraction(paths).ReadCheckpoint(chatId,id);
                if(saved is not null) { saved.Warnings.Add(error is OperationCanceledException?"Подготовка остановлена пользователем. Частичная расшифровка сохранена.":error is AppException?error.Message:"Подготовка прервана из-за ошибки. Исходная запись и частичная расшифровка сохранены.");saved.Media!.TranscriptArtifact=MediaArtifacts.Transcript(paths,chatId,saved); return saved; }
            }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            throw;
        }
    }
    public async Task<Attachment> ResumeAsync(string chat,Attachment attachment,IProgress<string>? progress,CancellationToken token) {
        if(attachment.Media is null)throw new AppException("MEDIA_REQUIRED","Это не медиазапись.");
        var file=paths.Resolve(attachment.RelativeOriginal);FileGuard.CheckSize(file);
        if(await Task.Run(()=>AppPaths.Hash(file),token)!=attachment.Sha256)throw new AppException("MEDIA_CHANGED","Сохранённая запись изменилась.");
        if(attachment.Media.HasAudio)await new SpeechModelStore(paths).EnsureAsync(attachment.Media.SpeechModel,progress,token);
        try{return await ProcessAsync(chat,attachment.Id,attachment.Name,Path.GetExtension(file),attachment.Media.SpeechModel,progress,token);}
        catch(OperationCanceledException){var saved=new MediaExtraction(paths).ReadCheckpoint(chat,attachment.Id);if(saved is null)throw;saved.Media!.TranscriptArtifact=MediaArtifacts.Transcript(paths,chat,saved);return saved;}
    }
    private static async Task ReadProgressAsync(StreamReader reader,IProgress<string>? progress,CancellationToken token) {
        var lines=0;while(await reader.ReadLineAsync(token) is {} line){if(++lines>200000 || line.Length>8192)throw new AppException("WORKER_OUTPUT","Слишком большой служебный ответ обработчика.");
            if(!line.StartsWith('{'))continue;try{using var json=System.Text.Json.JsonDocument.Parse(line);if(json.RootElement.TryGetProperty("stage",out var stage))progress?.Report(stage.GetString()??"");}catch(System.Text.Json.JsonException){}
        }
    }
    private async Task<Attachment> ProcessAsync(string chatId,string id,string displayName,string extension,string speechModel,IProgress<string>? progress,CancellationToken token) {
        var destination=Path.Combine(paths.AttachmentDirectory(chatId,id),"original"+extension);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(FileGuard.IsMedia(destination) ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(10));
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new AppException("PROCESS_PATH", "Не найден исполняемый файл приложения."))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(ImportService).Assembly.Location);
            start.ArgumentList.Add("--extract");
            start.Environment["ODA_SPEECH_MODEL_ROOT"]=new SpeechModelStore(paths).Root;
            start.Environment["ODA_WORKER_DATA_ROOT"] = paths.Root;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new AppException("WORKER_START", "Не удалось запустить обработчик документов.");
            try
            {
                using var guard = new NativeJobGuard(process, FileGuard.IsMedia(destination) ? 3072UL : 1536UL);
                var outputTask = BoundedText.ReadAsync(process.StandardOutput.BaseStream, 32_000_000, timeout.Token);
                var errorTask = ReadProgressAsync(process.StandardError, progress, timeout.Token);
                await process.StandardInput.WriteLineAsync(JsonDefaults.Write(new ExtractionJob(chatId, id, displayName, extension, speechModel)).AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var output = await outputTask; await errorTask;
                if (string.IsNullOrWhiteSpace(output)) throw new AppException("WORKER_EXIT", "Обработчик завершился без результата. Возможны повреждение файла или превышение памяти.");
                var result = JsonDefaults.Read<WorkerResult>(output);
                if (result.Attachment is null)
                {
                    Diagnostics.Record(paths, "IMPORT_" + extension.TrimStart('.').ToUpperInvariant() + "_FAILED");
                    throw new AppException(result.ErrorCode ?? "EXTRACTION", result.ErrorMessage ?? "Не удалось прочитать документ.");
                }
                Diagnostics.Record(paths, "IMPORT_" + extension.TrimStart('.').ToUpperInvariant() +
                    (result.Attachment.Segments.Count > 0 ? "_TEXT_OK" : result.Attachment.Visuals.Count > 0 ? "_VISUAL_ONLY" : "_EMPTY"));
                return result.Attachment;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new AppException("EXTRACTION_TIMEOUT", "Превышено время обработки. Уже подготовленные данные сохранены."); }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            }
    }
}
