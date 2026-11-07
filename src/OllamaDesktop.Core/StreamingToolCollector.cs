using System.Text;
using System.Text.Json;

namespace OllamaDesktop.Core;

/// <summary>
/// Collects streaming tool requests without attempting to deserialize incomplete arguments.
/// Only completed, size-bounded JSON objects are passed to allowlisted tools.
/// </summary>
public sealed class StreamingToolCollector
{
    private sealed class Pending
    {
        public string Name = "";
        public JsonElement? Object;
        public StringBuilder String = new();
    }
    private readonly Dictionary<int, Pending> _pending = new();
    public void Accept(JsonElement calls)
    {
        if (calls.ValueKind != JsonValueKind.Array) throw new AppException("TOOL_STREAM", "Облако вернуло неверный список команд. Файлы не изменены.");
        var position = 0;
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                throw new AppException("TOOL_STREAM", "Некорректный формат команды модели.");
            var index = position;
            if (call.TryGetProperty("index", out var indexValue) && indexValue.ValueKind == JsonValueKind.Number && indexValue.TryGetInt32(out var explicitIndex))
                index = explicitIndex;
            else if (function.TryGetProperty("index", out indexValue) && indexValue.ValueKind == JsonValueKind.Number && indexValue.TryGetInt32(out explicitIndex))
                index = explicitIndex;
            if (index < 0 || index >= 8) throw new AppException("TOOL_LIMIT", "Модель запросила слишком много команд за один шаг.");
            var indexed = call.TryGetProperty("index", out _) || function.TryGetProperty("index", out _);
            var name = function.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString() ?? "" : "";
            if (_pending.TryGetValue(index, out var existing) && existing.Name.Length != 0 && name.Length != 0 &&
                (existing.Name != name || (existing.Object is { } prior && function.TryGetProperty("arguments", out var next) &&
                  next.ValueKind == JsonValueKind.Object && prior.GetRawText() != next.GetRawText())))
            {
                if (indexed)
                {
                    if (existing.Name != name) throw new AppException("TOOL_STREAM", "Имя потоковой команды изменилось.");
                }
                else
                {
                // Providers may send distinct complete calls in separate chunks without an index.
                index = Enumerable.Range(0, 8).FirstOrDefault(i => !_pending.ContainsKey(i), -1);
                if (index < 0) throw new AppException("TOOL_LIMIT", "Модель запросила более восьми команд.");
                }
            }
            if (!_pending.TryGetValue(index, out var pending)) _pending[index] = pending = new Pending();
            if (name.Length != 0) pending.Name = name;
            if (!function.TryGetProperty("arguments", out var args)) { position++; continue; }
            if (args.ValueKind == JsonValueKind.Object)
            {
                pending.Object = args.Clone();
                pending.String.Clear();
            }
            else if (args.ValueKind == JsonValueKind.String)
            {
                var piece = args.GetString() ?? "";
                if (piece.Length > 250_000) throw new AppException("TOOL_ARGS", "Параметры команды превышают лимит.");
                if (pending.String.Length > 0 && piece.StartsWith(pending.String.ToString(), StringComparison.Ordinal))
                    pending.String.Clear(); // Handle cumulative streaming snapshots.
                pending.String.Append(piece);
                if (pending.String.Length > 250_000) throw new AppException("TOOL_ARGS", "Параметры команды превышают лимит.");
            }
            else if (args.ValueKind != JsonValueKind.Null)
                throw new AppException("TOOL_ARGS", "Параметры команды должны быть JSON-объектом.");
            position++;
        }
    }
    public List<ToolCall> Complete()
    {
        var result = new List<ToolCall>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, pending) in _pending.OrderBy(p => p.Key))
        {
            if (string.IsNullOrWhiteSpace(pending.Name) || pending.Name.Length > 96)
                throw new AppException("TOOL_ARGS", "Модель не указала корректное имя инструмента.");
            JsonElement args;
            if (pending.Object is { } complete) args = complete;
            else
            {
                if (pending.String.Length == 0) throw new AppException("TOOL_ARGS", "От модели не получены параметры инструмента.");
                try { using var document = JsonDocument.Parse(pending.String.ToString(), new JsonDocumentOptions { MaxDepth = 48 }); args = document.RootElement.Clone(); }
                catch (JsonException error) { throw new AppException("TOOL_ARGS_JSON", "Модель вернула неполные JSON-параметры. Файл не был создан; уменьшите запрос и повторите вручную.", error); }
            }
            if (args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > 250_000)
                throw new AppException("TOOL_ARGS", "Недопустимые параметры инструмента.");
            if (unique.Add(pending.Name + args.GetRawText())) result.Add(new ToolCall(pending.Name, args));
        }
        return result;
    }
}
