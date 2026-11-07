using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaDesktop.Core;

public static class BuildInfo
{
    public const string Version = "0.1.31";
    public const string Product = "Ollama Desktop";
    public const int SchemaVersion = 1;
}
public static class Limits
{
    public const long FileBytes = 512L * 1024 * 1024;
    public const long ExpandedBytes = 1024L * 1024 * 1024;
    public const long MediaBytes = 2L * 1024 * 1024 * 1024;
    public const int MediaSeconds = 6 * 60 * 60;
    public const int ZipEntries = 6000;
    public const int PdfPages = 100;
    public const int ImagePages = 12;
    public const int VisualsPerFile = 8;
    public const int VisualsPerRequest = 50;
    public const int MaxImageSide = 1800;
    public const ulong ImagePixels = 40_000_000;
    public const int DocumentCharacters = 400_000;
    public const int TotalCharacters = 600_000;
    public const int ContextCharacters = 48_000;
    public const int UserCharacters = 16_000;
    public const int ResponseCharacters = 150_000;
    public const int HistoryCharacters = 16_000;
    public const int AttachmentCount = 50;
}
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 48,
        WriteIndented = false
    };
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new AppException("INVALID_JSON", "Получены пустые данные.");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
public sealed class AppException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
public static class FriendlyError
{
    public static string Describe(Exception e) => e switch
    {
        AppException app => app.Message + " [" + app.Code + "]",
        OperationCanceledException => "Операция остановлена или превышено время ожидания.",
        HttpRequestException => "Нет связи с Ollama Cloud. Проверьте интернет и повторите запрос вручную.",
        UnauthorizedAccessException => "Нет доступа к файлу. Выберите доступную папку или закройте файл в другой программе.",
        IOException => "Ошибка чтения или записи. Проверьте свободное место и доступность файла.",
        JsonException => "Модель вернула некорректную структуру. Файл не создан. Повторите запрос вручную.",
        _ => "Операция не выполнена. Сохраните диагностику через настройки. Код: " + e.GetType().Name
    };
}
public sealed record SourceSegment(string Reference, string Text);
public sealed record VisualSource(string Id, string Reference, string RelativePath);
public sealed class Attachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string RelativeOriginal { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public List<SourceSegment> Segments { get; set; } = [];
    public List<VisualSource> Visuals { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public MediaMetadata? Media { get; set; }
    public bool IsMedia => Media is not null;
    public bool CanResume => Media is not null && (!Media.AudioComplete || !Media.FramesComplete);
    public string Summary => Media is not null ? $"{MediaTime.Format(Media.DurationSeconds)} · {Media.Status} · {Media.Lines.Count} реплик · {Visuals.Count} кадров" : $"{Name} · {Segments.Sum(s => s.Text.Length):N0} знаков · {Visuals.Count} изображений" + (Segments.Count == 0 && Visuals.Count == 0 ? " · нет извлечённого содержимого" : "");
    public string Details => Summary + (Warnings.Count == 0 ? "" : "\n" + string.Join("\n", Warnings));
}
public sealed class Artifact
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Format { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Validation { get; set; } = "";
}
public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Новый чат";
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string? ProjectId { get; set; }
}
public sealed class WorkspaceProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый проект";
    public string Instructions { get; set; } = "";
    public string MemoryNotes { get; set; } = "";
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public override string ToString() => Name;
}

public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChatId { get; set; } = "";
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Status { get; set; } = "complete";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<Attachment> Attachments { get; set; } = [];
    public List<Artifact> Artifacts { get; set; } = [];
    public string Note { get; set; } = "";
    public OutputKind Output { get; set; } = OutputKind.Chat;
}
public enum OutputKind { Chat, Word, Excel, PowerPoint, Zip }
public sealed record OutputOption(OutputKind Kind, string Label);
public sealed class AppSettings
{
    public string TextModel { get; set; } = "gpt-oss:120b";
    public string VisionModel { get; set; } = "gemma4:31b";
    public string Theme { get; set; } = "Dark";
    public string SpeechModel { get; set; } = "small";
    public string UpdateChannel { get; set; } = "stable";
    public bool AutomaticUpdateChecks { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public bool CloudConsent { get; set; }
    public bool FullDocument { get; set; } = true;
    public bool AnalyzeVisuals { get; set; } = true;
}
public sealed record ModelInfo(string Name, string[] Capabilities)
{
    public bool IsVision => Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase);
    public override string ToString() => Name;
}
public sealed record ToolCall(string Name, JsonElement Arguments);
public sealed class ApiMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string[]? Images { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("tool_calls")] public object[]? ToolCalls { get; set; }
}
public sealed record ProviderResult(string Content, List<ToolCall> Tools);
public sealed record GenerationResult(string Content, List<Artifact> Artifacts, string Note, bool Complete = true);
public sealed record WorkProgress(string Stage, string? Delta = null, Artifact? CreatedArtifact = null);
public sealed record ExtractionJob(string ChatId, string AttachmentId, string Name, string Extension, string SpeechModel = "small");
public sealed class WorkerResult
{
    public Attachment? Attachment { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
