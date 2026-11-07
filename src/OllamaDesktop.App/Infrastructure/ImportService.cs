using System.Diagnostics;
using System.IO;
using System.Text;
using OllamaDesktop.Core;

namespace OllamaDesktop.App.Infrastructure;

public sealed class ImportService(AppPaths paths)
{
    public async Task<Attachment> ImportAsync(string chatId, string source, CancellationToken token, string? displayName = null)
    {
        var extension = FileGuard.Extension(source); FileGuard.CheckSize(source); var id = Guid.NewGuid().ToString("N");
        var directory = paths.AttachmentDirectory(chatId, id); Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "original" + extension);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; var total = 0L; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read; if (total > Limits.FileBytes) throw new AppException("COPY_SIZE", "Во время чтения файл превысил 25 МиБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new AppException("PROCESS_PATH", "Не найден исполняемый файл приложения."))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(ImportService).Assembly.Location);
            start.ArgumentList.Add("--extract");
            start.Environment["ODA_WORKER_DATA_ROOT"] = paths.Root;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new AppException("WORKER_START", "Не удалось запустить обработчик документов.");
            try
            {
                using var guard = new JobGuard(process);
                var outputTask = BoundedText.ReadAsync(process.StandardOutput.BaseStream, 4_000_000, timeout.Token);
                var errorTask = BoundedText.ReadAsync(process.StandardError.BaseStream, 16_000, timeout.Token);
                await process.StandardInput.WriteLineAsync(JsonDefaults.Write(new ExtractionJob(chatId, id, displayName ?? Path.GetFileName(source), extension)).AsMemory(), timeout.Token);
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
            { throw new AppException("EXTRACTION_TIMEOUT", "Обработка превысила 90 секунд. Процесс остановлен; выберите меньший документ."); }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
        catch
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            throw;
        }
    }
}
