using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OllamaDesktop.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WordSpec
{
    public string Title { get; set; } = "";
    public List<WordSection> Sections { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WordSection
{
    public string Heading { get; set; } = "";
    public List<string> Paragraphs { get; set; } = [];
    public DocumentTable? Table { get; set; }
    public List<string> ImageIds { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DocumentTable
{
    public List<string> Columns { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WorkbookSpec
{
    public string Title { get; set; } = "";
    public List<SheetSpec> Sheets { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SheetSpec
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public List<List<JsonElement>> Rows { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PresentationSpec
{
    public string Title { get; set; } = "";
    public List<SlideSpec> Slides { get; set; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SlideSpec
{
    public string Title { get; set; } = "";
    public List<string> Bullets { get; set; } = [];
    public string? ImageId { get; set; }
}
public static class SpecValidation
{
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new AppException("DOCUMENT_SPEC", message + " Файл не создан."); }
    public static void Text(string? value, int max, bool required = false)
    {
        Require(value is not null && value.Length <= max && (!required || !string.IsNullOrWhiteSpace(value)), "Текст отсутствует или превышает ограничение");
        Require(!value!.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')), "В тексте недопустимые управляющие символы");
    }
    public static void Word(WordSpec spec, ISet<string> imageIds)
    {
        Text(spec.Title, 160, true); Require(spec.Sections is { Count: > 0 and <= 40 }, "Требуется 1–40 разделов");
        foreach (var section in spec.Sections)
        {
            Require(section is not null, "Пустой раздел"); Text(section.Heading, 160);
            Require(section.Paragraphs is { Count: <= 80 } && section.ImageIds is { Count: <= 3 }, "Слишком много абзацев или изображений");
            foreach (var text in section.Paragraphs) Text(text, 6000);
            foreach (var image in section.ImageIds) Require(imageIds.Contains(image), "Модель указала неизвестное изображение");
            if (section.Table is { } table)
            {
                Require(table.Columns is { Count: > 0 and <= 8 } && table.Rows is { Count: <= 150 }, "Таблица Word превышает 8 столбцов или 150 строк");
                foreach (var column in table.Columns) Text(column, 160, true);
                foreach (var row in table.Rows) { Require(row is not null && row.Count == table.Columns.Count, "Неодинаковое число ячеек в таблице"); foreach (var cell in row) Text(cell, 1500); }
            }
        }
        Require(JsonDefaults.Write(spec).Length <= 240_000, "Документ слишком большой");
    }
    public static void Workbook(WorkbookSpec spec)
    {
        Text(spec.Title, 160, true); Require(spec.Sheets is { Count: > 0 and <= 6 }, "Требуется 1–6 листов");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in spec.Sheets)
        {
            Require(sheet is not null, "Пустой лист");
            Text(sheet.Name, 31, true); Require(!sheet.Name.Any(c => "[]:*?/\\".Contains(c)) && !sheet.Name.StartsWith('\'') && !sheet.Name.EndsWith('\'') && names.Add(sheet.Name), "Некорректное или повторяющееся имя листа");
            Require(sheet.Columns is { Count: > 0 and <= 20 } && sheet.Rows is { Count: > 0 and <= 1000 }, "Требуется 1–20 столбцов и 1–1000 строк");
            var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in sheet.Columns) { Text(header, 100, true); Require(headers.Add(header), "Заголовки столбцов должны быть уникальны"); }
            foreach (var row in sheet.Rows)
            {
                Require(row is not null && row.Count == sheet.Columns.Count, "Количество ячеек не совпадает с заголовками");
                foreach (var value in row)
                {
                    if (value.ValueKind == JsonValueKind.String) Text(value.GetString(), 4000);
                    else if (value.ValueKind == JsonValueKind.Number) Require(value.TryGetDouble(out var number) && double.IsFinite(number), "Недопустимое число");
                    else if (value.ValueKind == JsonValueKind.Object)
                    {
                        Require(value.EnumerateObject().Count() == 1 && value.TryGetProperty("formula", out var formula) && formula.ValueKind == JsonValueKind.String, "В ячейке допустимо только текстовое поле formula");
                        Formula(value.GetProperty("formula").GetString() ?? "", sheet.Rows.Count + 1, sheet.Columns.Count);
                    }
                    else Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null, "Недопустимое значение ячейки");
                }
            }
        }
        Require(JsonDefaults.Write(spec).Length <= 240_000, "Книга слишком большая");
    }
    public static string Formula(string input, int rows, int columns)
    {
        var formula = input.Trim().TrimStart('=').ToUpperInvariant();
        Require(formula.Length is > 0 and <= 256 && Regex.IsMatch(formula, @"^[A-Z0-9$.,:+\-*/()% \t]+$"), "Формула содержит запрещённые символы");
        var allowed = new HashSet<string> { "SUM", "AVERAGE", "MIN", "MAX", "ROUND", "COUNT", "ABS" };
        foreach (Match match in Regex.Matches(formula.Replace("$", ""), @"[A-Z]+[0-9]*"))
        {
            if (allowed.Contains(match.Value)) continue;
            var reference = Regex.Match(match.Value, @"^([A-Z]{1,3})([1-9][0-9]{0,5})$");
            Require(reference.Success, "Поддерживаются только SUM, AVERAGE, MIN, MAX, ROUND, COUNT, ABS и ссылки внутри листа");
            var column = 0; foreach (var c in reference.Groups[1].Value) column = column * 26 + c - 'A' + 1;
            Require(column <= columns && int.Parse(reference.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) <= rows, "Ссылка формулы вне созданной таблицы");
        }
        return formula;
    }
    public static void Presentation(PresentationSpec spec, ISet<string> imageIds)
    {
        Text(spec.Title, 120, true); Require(spec.Slides is { Count: > 0 and <= 30 }, "Требуется 1–30 слайдов");
        foreach (var slide in spec.Slides)
        {
            Require(slide is not null, "Пустой слайд");
            Text(slide.Title, 95, true); Require(slide.Bullets is { Count: <= 6 }, "На слайде допускается до 6 пунктов");
            foreach (var bullet in slide.Bullets) Text(bullet, 120, true);
            Require(slide.Bullets.Sum(x => x.Length) <= 500, "Слайд перегружен текстом; разделите его");
            if (!string.IsNullOrWhiteSpace(slide.ImageId))
            {
                Require(imageIds.Contains(slide.ImageId), "Неизвестное изображение слайда");
                Require(slide.Bullets.Count <= 4 && slide.Bullets.Sum(x => x.Length) <= 260, "Для слайда с картинкой сократите текст до 4 коротких пунктов");
            }
        }
    }
}
