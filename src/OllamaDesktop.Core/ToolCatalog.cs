using System.Text.Json;

namespace OllamaDesktop.Core;

public static class ToolCatalog
{
    private static object Tool(string name, string description, string schema) => new
    {
        type = "function", function = new { name, description, parameters = JsonSerializer.Deserialize<JsonElement>(schema) }
    };
    public static List<object> For(OutputKind output)
    {
        var tools = new List<object>
        {
            Tool("calculate", "Compute exact decimal arithmetic. Allowed: numbers, + - * / %, parentheses, SUM, AVERAGE, MIN, MAX, ROUND, ABS. Decimal separator is dot; function argument separator is comma. Use for calculations instead of mental arithmetic.",
                """{"type":"object","properties":{"expression":{"type":"string","maxLength":2048}},"required":["expression"],"additionalProperties":false}""")
        };
        if (output == OutputKind.Word) tools.Add(Tool("create_word", "Create the requested real DOCX report using the built-in template. Use only supplied imageIds, not file paths. 1-40 sections; tables up to 8 columns/150 rows. Use strict JSON arrays, not JSON encoded as strings. Fill every required field, including imageIds (use [] when empty). Do not claim creation before this function succeeds.",
            """
            {"type":"object","additionalProperties":false,"required":["title","sections"],"properties":{
              "title":{"type":"string","maxLength":160},"sections":{"type":"array","minItems":1,"maxItems":40,"items":{
                "type":"object","additionalProperties":false,"required":["heading","paragraphs","imageIds"],"properties":{
                  "heading":{"type":"string"},"paragraphs":{"type":"array","items":{"type":"string"}},"imageIds":{"type":"array","items":{"type":"string"}},
                  "table":{"type":"object","additionalProperties":false,"required":["columns","rows"],"properties":{"columns":{"type":"array","items":{"type":"string"}},"rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}}}}
                }}}}}
            """));
        if (output == OutputKind.Excel) tools.Add(Tool("create_workbook", "Create an XLSX workbook. Each sheet has row-1 headers and equal-width data rows. A cell is string, number, boolean, null, or {formula: \"SUM(B2:B5)\"}. Allowed functions: SUM AVERAGE MIN MAX ROUND COUNT ABS; same-sheet bounded references only. Formulas are deterministically recalculated; no macros or external links. Max 6 sheets, 20 columns, 1000 data rows per sheet.",
            """
            {"type":"object","additionalProperties":false,"required":["title","sheets"],"properties":{"title":{"type":"string"},"sheets":{"type":"array","minItems":1,"maxItems":6,"items":{
              "type":"object","additionalProperties":false,"required":["name","columns","rows"],"properties":{"name":{"type":"string","maxLength":31},"columns":{"type":"array","items":{"type":"string"}},"rows":{"type":"array","items":{"type":"array","items":{"anyOf":[{"type":"string"},{"type":"number"},{"type":"boolean"},{"type":"null"},{"type":"object","required":["formula"],"additionalProperties":false,"properties":{"formula":{"type":"string"}}}]}}}}
            }}}}
            """));
        if (output == OutputKind.PowerPoint) tools.Add(Tool("create_presentation", "Create a real 16:9 PPTX using the built-in layout. 1-30 slides. Slide title <=95 characters; <=6 bullets each <=120 characters, <=500 total. With an image, <=4 bullets and <=260 total characters. Use supplied imageId or omit it. No code, paths or HTML.",
            """
            {"type":"object","additionalProperties":false,"required":["title","slides"],"properties":{"title":{"type":"string"},"slides":{"type":"array","minItems":1,"maxItems":30,"items":{
              "type":"object","additionalProperties":false,"required":["title","bullets"],"properties":{"title":{"type":"string","maxLength":95},"bullets":{"type":"array","maxItems":6,"items":{"type":"string","maxLength":120}},"imageId":{"type":"string"}}
            }}}}
            """));
        if (output == OutputKind.Zip) tools.Add(Tool("create_archive", "Create a real ZIP with up to 50 UTF-8 text files (txt, csv, md, json), optionally copy selected original attachments using their exact attachment IDs; created Office files can be packed by their handles from prior tool results. Do NOT invent Office binary by text. To include new Office files, first create them using their proper tools, then use returned artifact handles in artifactIds. For uploaded Office files use attachmentIds.",
            """{"type":"object","additionalProperties":false,"required":["title","files","attachmentIds","artifactIds"],"properties":{"title":{"type":"string","maxLength":120},"files":{"type":"array","maxItems":50,"items":{"type":"object","additionalProperties":false,"required":["name","content"],"properties":{"name":{"type":"string"},"content":{"type":"string"}}}},"attachmentIds":{"type":"array","maxItems":50,"items":{"type":"string"}},"artifactIds":{"type":"array","maxItems":50,"items":{"type":"string"}}}}"""));
        return tools;
    }
    // The chat always offers the finite set of validated creation functions;
    // there is no manual output selector.
    public static List<object> Auto(bool includeArchive = true)
    {
        var tools = For(OutputKind.Chat);
        foreach (var kind in new[] { OutputKind.Word, OutputKind.Excel, OutputKind.PowerPoint, OutputKind.Zip }.Where(k => includeArchive || k != OutputKind.Zip))
            tools.Add(For(kind)[1]);
        return tools;
    }
    public static OutputKind? Kind(string name) => name switch
    {
        "create_word" => OutputKind.Word,
        "create_workbook" => OutputKind.Excel,
        "create_presentation" => OutputKind.PowerPoint,
        "create_archive" => OutputKind.Zip,
        _ => null
    };
    public static string Name(OutputKind output) => output switch { OutputKind.Word => "create_word", OutputKind.Excel => "create_workbook", OutputKind.PowerPoint => "create_presentation", OutputKind.Zip => "create_archive", _ => "" };
}
