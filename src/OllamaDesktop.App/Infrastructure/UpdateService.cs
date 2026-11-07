using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using OllamaDesktop.Core;

namespace OllamaDesktop.App.Infrastructure;

public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public UpdateService() { _http.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaDesktop/" + BuildInfo.Version); }
    public static string Resource(string name)
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("OllamaDesktop.App.Update." + name) ?? throw new IOException("Missing update resource");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private async Task<byte[]> ReadAsync(string url, int limit, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("UPDATE_SIZE");
        using var content = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[65536]; int size;
        while ((size = await content.ReadAsync(buffer, ct)) > 0) { if (output.Length + size > limit) throw new IOException("UPDATE_SIZE"); await output.WriteAsync(buffer.AsMemory(0,size), ct); }
        return output.ToArray();
    }
    public async Task<UpdateManifest?> CheckAsync(string channel, CancellationToken ct)
    {
        var releases = Encoding.UTF8.GetString(await ReadAsync("https://api.github.com/repos/" + UpdateFeed.Repository + "/releases?per_page=100", 2_000_000, ct));
        var candidate = UpdateFeed.Candidates(releases, BuildInfo.Version, channel).FirstOrDefault();
        if (candidate == default) return null;
        var data = await ReadAsync(candidate.Url,64000,ct);
        var signature = await ReadAsync(candidate.Url + ".sig",1024,ct);
        var manifest = UpdateFeed.Verify(data,signature,Resource("public-key.xml"));
        if ("v" + manifest.Version != candidate.Tag) throw new AppException("UPDATE_TAG", "Версия и подпись выпуска не совпадают.");
        UpdateFeed.Validate(manifest,BuildInfo.Version,channel);
        return manifest;
    }
    public async Task<string> PrepareAsync(UpdateManifest m, string channel, IProgress<string> progress, CancellationToken ct)
    {
        UpdateFeed.Validate(m,BuildInfo.Version,channel);
        var install = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","OllamaDesktopAssistant");
        if (!string.Equals(install,expected,StringComparison.OrdinalIgnoreCase)) throw new AppException("UPDATE_INSTALL", "Обновление доступно после установки приложения. Исходники и тестовые сборки автоматически не заменяются.");
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OllamaDesktopAssistant","Updates",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress.Report("Загрузка и проверка подписи…");
            var url = UpdateFeed.DownloadPrefix + "v" + m.Version + "/update.json";
            var json = await ReadAsync(url,64000,ct); var sig = await ReadAsync(url+".sig",1024,ct);
            var fresh = UpdateFeed.Verify(json,sig,Resource("public-key.xml")); UpdateFeed.Validate(fresh,BuildInfo.Version,channel);
            if (JsonDefaults.Write(fresh) != JsonDefaults.Write(m)) throw new AppException("UPDATE_CHANGED", "Выпуск изменился. Проверьте обновления заново.");
            await File.WriteAllBytesAsync(Path.Combine(stage,"update.json"),json,ct); await File.WriteAllBytesAsync(Path.Combine(stage,"update.json.sig"),sig,ct);
            progress.Report("Загрузка установщика…");
            var installer = await ReadAsync(m.InstallerUrl,300_000_000,ct);
            if (installer.LongLength != m.InstallerBytes || !Convert.ToHexString(SHA256.HashData(installer)).Equals(m.InstallerSha256,StringComparison.OrdinalIgnoreCase)) throw new AppException("UPDATE_HASH", "Установщик повреждён. Обновление отменено.");
            await File.WriteAllBytesAsync(Path.Combine(stage,"Setup.exe"),installer,ct);
            var inventory = await ReadAsync(m.InventoryUrl,2_000_000,ct);
            if (!Convert.ToHexString(SHA256.HashData(inventory)).Equals(m.InventorySha256,StringComparison.OrdinalIgnoreCase)) throw new AppException("UPDATE_HASH", "Манифест файлов повреждён.");
            await File.WriteAllBytesAsync(Path.Combine(stage,"installed-files.json"),inventory,ct);
            await File.WriteAllTextAsync(Path.Combine(stage,"Apply-Update.ps1"),Resource("Apply-Update.ps1"),new UTF8Encoding(true),ct);
            await File.WriteAllTextAsync(Path.Combine(stage,"job.json"),JsonDefaults.Write(new { productId="OllamaDesktopAssistant",installDirectory=install,fromVersion=BuildInfo.Version,channel,pid=Environment.ProcessId,processStart=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks }),ct);
            progress.Report("Файлы проверены. Подготовка перезапуска…"); return stage;
        }
        catch { try { Directory.Delete(stage,true); } catch { } throw; }
    }
    public static void StartWorker(string stage)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden };
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(stage,"Apply-Update.ps1"),"-Stage",stage}) start.ArgumentList.Add(arg);
        _ = Process.Start(start) ?? throw new IOException("UPDATE_WORKER");
    }
    public void Dispose() => _http.Dispose();
}
