using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace OllamaDesktop.Core;

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    public OllamaClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(25),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { BaseAddress = new Uri("https://ollama.com/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaDesktop/" + BuildInfo.Version);
    }
    private static HttpRequestMessage Request(HttpMethod method, string route, string? key)
    {
        var request = new HttpRequestMessage(method, route);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return request;
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AppException("AUTH_401", "Ollama отклонила ключ. Проверьте его в настройках."),
            HttpStatusCode.Forbidden => new AppException("ACCESS_403", "Аккаунту недоступна эта модель или действие. Проверьте доступ и баланс Ollama."),
            HttpStatusCode.NotFound => new AppException("MODEL_404", "Модель или API не найдены. Обновите список моделей в настройках."),
            HttpStatusCode.TooManyRequests => new AppException("LIMIT_429", "Достигнут лимит Ollama. Проверьте тариф/баланс и повторите запрос позже. Автоматического повтора не будет."),
            HttpStatusCode.PaymentRequired => new AppException("PAYMENT_402", "Для запроса недостаточно средств или доступа в аккаунте Ollama."),
            HttpStatusCode.BadRequest => new AppException("REQUEST_400", "Ollama не приняла запрос. Проверьте модель, поддержку изображений и уменьшите размер вложений."),
            _ => new AppException("HTTP_" + (int)response.StatusCode, "Облачный сервис не выполнил запрос. Содержимое чата сохранено; повтор возможен вручную.")
        };
    }
    public async Task<List<ModelInfo>> ModelsAsync(string? key, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var request = Request(HttpMethod.Get, "api/tags", key);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        CheckStatus(response);
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var text = await BoundedText.ReadAsync(input, 2_000_000, timeout.Token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(text);
        var result = new List<ModelInfo>();
        if (!json.RootElement.TryGetProperty("models", out var models)) throw new AppException("MODEL_LIST", "Сервис вернул некорректный список моделей.");
        foreach (var model in models.EnumerateArray())
        {
            var name = model.GetProperty("name").GetString() ?? "";
            var capabilities = model.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                ? caps.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            if (name.Length is > 0 and < 121) result.Add(new ModelInfo(name, capabilities));
        }
        return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    }
    public async Task<ProviderResult> ChatAsync(string key, string model, IReadOnlyList<ApiMessage> messages,
        IReadOnlyList<object>? tools, Action<string>? onDelta, Action? onThinking, CancellationToken cancellationToken, int maxTokens = 8192)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = model, ["messages"] = messages, ["stream"] = true,
                ["options"] = new { num_predict = Math.Clamp(maxTokens, 64, 16384) }
            };
            if (tools is { Count: > 0 }) payload["tools"] = tools;
            if (model.StartsWith("gpt-oss:", StringComparison.OrdinalIgnoreCase)) payload["think"] = "low";
            var body = JsonDefaults.Write(payload);
            if (Encoding.UTF8.GetByteCount(body) > 24 * 1024 * 1024) throw new AppException("REQUEST_SIZE", "Запрос слишком большой. Уменьшите число изображений.");
            using var request = Request(HttpMethod.Post, "api/chat", key);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            CheckStatus(response);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var content = new StringBuilder(); var toolCollector = new StreamingToolCollector();
            var done = false; var thinkingReported = false;
            await foreach (var line in BoundedText.NdjsonAsync(stream, timeout.Token).ConfigureAwait(false))
            {
                using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 48 });
                var root = json.RootElement;
                if (root.TryGetProperty("error", out _)) throw new AppException("CLOUD_STREAM_ERROR", "Ollama прервала обработку. Частичный ответ сохранён. Проверьте доступ к модели и лимиты.");
                if (root.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(thinking.GetString()) && !thinkingReported)
                    { thinkingReported = true; onThinking?.Invoke(); }
                    if (message.TryGetProperty("content", out var chunk) && chunk.ValueKind == JsonValueKind.String)
                    {
                        var delta = chunk.GetString() ?? "";
                        if (content.Length + delta.Length > Limits.ResponseCharacters) throw new AppException("RESPONSE_SIZE", "Ответ превысил ограничение. Частичный текст сохранён.");
                        content.Append(delta); if (delta.Length > 0) onDelta?.Invoke(delta);
                    }
                    if (message.TryGetProperty("tool_calls", out var toolCalls)) toolCollector.Accept(toolCalls);
                }
                if (root.TryGetProperty("done", out var finished) && finished.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length")
                        throw new AppException("TOKEN_LIMIT", "Модель достигла ограничения длины ответа. Сократите задачу; незавершённый файл не создавался.");
                    done = true; break;
                }
            }
            if (!done) throw new AppException("STREAM_INCOMPLETE", "Соединение оборвалось до завершения ответа. Частичный текст сохранён.");
            return new ProviderResult(content.ToString(), toolCollector.Complete());
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        { throw new AppException("CLOUD_TIMEOUT", "Истекло время ожидания облака. Автоматический повтор не выполнялся.", e); }
        catch (JsonException e)
        { throw new AppException("STREAM_JSON", "Сервис вернул повреждённые данные. Частичный ответ сохранён.", e); }
    }
    public async Task TestAsync(string key, string model, CancellationToken token)
    {
        var answer = await ChatAsync(key, model, [new ApiMessage { Role = "user", Content = "Reply with OK only." }], null, null, null, token, 512).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(answer.Content)) throw new AppException("EMPTY_TEST", "Ответ на проверку пуст. Проверьте доступ к выбранной модели.");
    }
    public void Dispose() => _http.Dispose();
}
public static class BoundedText
{
    public static async Task<string> ReadAsync(Stream stream, int maxCharacters, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096]; var result = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > maxCharacters) throw new AppException("INPUT_SIZE", "Полученные данные превышают ограничение размера.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
    public static async IAsyncEnumerable<string> NdjsonAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        var buffer = new char[4096]; var line = new StringBuilder(); var total = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(100));
            var count = await reader.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > 3_000_000) throw new AppException("STREAM_SIZE", "Поток ответа слишком большой.");
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    var value = line.ToString().TrimEnd('\r'); line.Clear();
                    if (!string.IsNullOrWhiteSpace(value)) yield return value;
                }
                else
                {
                    if (line.Length >= 512_000) throw new AppException("STREAM_LINE_SIZE", "Повреждённая или слишком длинная строка ответа сервиса.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
    }
}
