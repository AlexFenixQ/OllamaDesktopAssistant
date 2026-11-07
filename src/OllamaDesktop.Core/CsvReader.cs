namespace OllamaDesktop.Core;

public static class CsvReader
{
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > Limits.DocumentCharacters) throw new AppException("TEXT_LIMIT", "Текст слишком большой.");
        var delimiter = CsvStreaming.DetectDelimiter(text);
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new System.Text.StringBuilder(); var quoted = false; var cells = 0;
        void EndCell() { if (++cells > 100_000) throw new AppException("CSV_CELLS", "Слишком много ячеек CSV."); row.Add(cell.ToString()); cell.Clear(); }
        void EndRow() { EndCell(); rows.Add(row); row = []; }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == delimiter) EndCell();
            else if (ch == '\n' || ch == '\r') { if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else cell.Append(ch);
        }
        if (quoted) throw new AppException("CSV_QUOTES", "В CSV не закрыта кавычка.");
        if (cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
    private static int CountUnquoted(string text, char delimiter)
    {
        var quote = false; var count = 0;
        foreach (var ch in text) { if (ch == '"') quote = !quote; else if (!quote && ch == delimiter) count++; }
        return count;
    }
}
