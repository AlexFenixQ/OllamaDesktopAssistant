using System.Security.Cryptography;
using System.Text;

namespace OllamaDesktop.Core;

public sealed class SettingsStore(AppPaths paths)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OllamaDesktopAssistant/key/v1");
    public bool HasKey => File.Exists(paths.KeyFile);
    public AppSettings Load()
    {
        if (!File.Exists(paths.SettingsFile)) return new AppSettings();
        if (new FileInfo(paths.SettingsFile).Length > 64_000) throw new AppException("SETTINGS_SIZE", "Файл настроек повреждён.");
        var settings = JsonDefaults.Read<AppSettings>(File.ReadAllText(paths.SettingsFile));
        Validate(settings);
        return settings;
    }
    public static void Validate(AppSettings settings)
    {
        foreach (var id in new[] { settings.TextModel, settings.VisionModel })
            if (string.IsNullOrWhiteSpace(id) || id.Length > 120 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                throw new AppException("MODEL_ID", "Укажите корректные идентификаторы облачных моделей.");
        if (settings.SpeechModel is not ("small" or "medium")) settings.SpeechModel = "small";
        if (settings.UpdateChannel is not ("stable" or "beta")) settings.UpdateChannel = "stable";
        if (settings.Theme is not ("Dark" or "Light")) settings.Theme = "Dark";
    }
    public void Save(AppSettings settings) { Validate(settings); AppPaths.AtomicText(paths.SettingsFile, JsonDefaults.Write(settings)); }
    public void StoreKey(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        key = key.Trim();
        if (key.Length is < 8 or > 4096 || key.Any(char.IsWhiteSpace)) throw new AppException("KEY_FORMAT", "Проверьте API-ключ: пробелы и переносы строк недопустимы.");
        var raw = Encoding.UTF8.GetBytes(key);
        try
        {
            var encrypted = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
            var temp = paths.KeyFile + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, paths.KeyFile, true);
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
    public string ReadKey()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        if (!HasKey) throw new AppException("KEY_MISSING", "Откройте настройки и введите личный API-ключ Ollama.");
        try
        {
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(paths.KeyFile), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(raw); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        catch (CryptographicException e) { throw new AppException("KEY_UNREADABLE", "Ключ не удалось расшифровать. Введите его заново в настройках этой учётной записи Windows.", e); }
    }
    public void DeleteKey() { if (File.Exists(paths.KeyFile)) File.Delete(paths.KeyFile); }
}
