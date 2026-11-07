using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OllamaDesktop.Core;

// Recover only model-generated JSON under an explicit user request to create files.
// Never interpret JSON from attachment contents as a tool command.
public static class DraftToolRecovery
{
    public static List<ToolCall> FromAssistantText(string text, IReadOnlySet<OutputKind> requested)
    {
        var recovered = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 250_000) return recovered;
        foreach (Match fence in Regex.Matches(text, @"```(?:json)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            try
            {
                var raw = fence.Groups[1].Value;
                var node = JsonNode.Parse(raw) as JsonObject;
                if (node is null) continue;
                OutputKind? kind = node.ContainsKey("sections") ? OutputKind.Word :
                    node.ContainsKey("sheets") ? OutputKind.Excel :
                    node.ContainsKey("slides") ? OutputKind.PowerPoint : null;
                if (kind is null || !requested.Contains(kind.Value) || recovered.Any(c => ToolCatalog.Kind(c.Name) == kind)) continue;
                JsonObject normalized = kind.Value switch
                {
                    OutputKind.Word => Word(node),
                    OutputKind.Excel => Excel(node),
                    _ => Slides(node)
                };
                using var document = JsonDocument.Parse(normalized.ToJsonString());
                recovered.Add(new ToolCall(ToolCatalog.Name(kind.Value), document.RootElement.Clone()));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or RegexMatchTimeoutException)
            {
                // Leave invalid suggestions unused. Only the normal validated artifact writer can create files.
            }
        }
        return recovered;
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static JsonArray StringArray(JsonNode? node)
    {
        var arr = new JsonArray();
        if (node is JsonArray source)
            foreach (var item in source) if (item is JsonValue v && v.TryGetValue<string>(out var s)) arr.Add(s);
        return arr;
    }
    private static JsonObject Word(JsonObject raw)
    {
        var sections = new JsonArray();
        if (raw["sections"] is JsonArray input)
            foreach (var entry in input.OfType<JsonObject>())
            {
                var type = Str(entry["type"]);
                if (type == "table")
                {
                    var headers = StringArray(entry["headers"] ?? entry["columns"]);
                    var rows = new JsonArray();
                    if (entry["rows"] is JsonArray original)
                        foreach (var row in original.OfType<JsonArray>())
                        {
                            var cells = new JsonArray();
                            foreach (var cell in row) cells.Add(cell?.ToString() ?? "");
                            rows.Add(cells);
                        }
                    if (headers.Count > 0 && entry["footer"] is JsonArray footer)
                    {
                        var cells = new JsonArray();
                        foreach (var cell in footer) cells.Add(cell?.ToString() ?? "");
                        rows.Add(cells);
                    }
                    sections.Add(new JsonObject { ["heading"] = "Таблица", ["paragraphs"] = new JsonArray(),
                        ["imageIds"] = new JsonArray(), ["table"] = new JsonObject { ["columns"] = headers, ["rows"] = rows } });
                }
                else
                {
                    var p = StringArray(entry["paragraphs"]);
                    if (p.Count == 0 && !string.IsNullOrWhiteSpace(Str(entry["text"]))) p.Add(Str(entry["text"]));
                    var section = new JsonObject { ["heading"] = Str(entry["heading"]), ["paragraphs"] = p,
                        ["imageIds"] = StringArray(entry["imageIds"]) };
                    if (entry["table"] is JsonObject table) section["table"] = table.DeepClone();
                    sections.Add(section);
                }
            }
        return new JsonObject { ["title"] = Str(raw["title"]), ["sections"] = sections };
    }
    private static JsonObject Excel(JsonObject raw)
    {
        var sheets = new JsonArray();
        if (raw["sheets"] is JsonArray input)
            foreach (var entry in input.OfType<JsonObject>())
            {
                var cols = new JsonArray();
                if (entry["columns"] is JsonArray columns)
                    foreach (var c in columns)
                        cols.Add(c is JsonObject o ? Str(o["header"]) : Str(c));
                var rows = new JsonArray();
                if (entry["rows"] is JsonArray inputRows)
                    foreach (var row in inputRows.OfType<JsonArray>())
                        rows.Add(row.DeepClone());
                if (entry["footer"] is JsonArray footer)
                    foreach (var footerRow in footer.OfType<JsonArray>())
                    {
                        var row = new JsonArray();
                        foreach (var cell in footerRow)
                        {
                            // Recognize malformed model output such as a formula serialized inside a string.
                            var t = Str(cell);
                            var formula = Regex.Match(t, "^\\s*\\{formula:\\s*\\\"(=?[A-Za-z0-9():+*/ .,-]+)\\\"\\}\\s*$");
                            if (formula.Success) row.Add(new JsonObject { ["formula"] = formula.Groups[1].Value });
                            else row.Add(cell?.DeepClone());
                        }
                        rows.Add(row);
                    }
                sheets.Add(new JsonObject { ["name"] = Str(entry["name"]), ["columns"] = cols, ["rows"] = rows });
            }
        return new JsonObject { ["title"] = Str(raw["title"]), ["sheets"] = sheets };
    }
    private static JsonObject Slides(JsonObject raw)
    {
        var slides = new JsonArray();
        if (raw["slides"] is JsonArray input)
            foreach (var entry in input.OfType<JsonObject>())
            {
                var slide = new JsonObject { ["title"] = Str(entry["title"]), ["bullets"] = StringArray(entry["bullets"]) };
                if (!string.IsNullOrWhiteSpace(Str(entry["imageId"]))) slide["imageId"] = Str(entry["imageId"]);
                slides.Add(slide);
            }
        return new JsonObject { ["title"] = Str(raw["title"]), ["slides"] = slides };
    }
}
