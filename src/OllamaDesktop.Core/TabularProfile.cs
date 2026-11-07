using System.Globalization;
using System.Text;

namespace OllamaDesktop.Core;

/// <summary>
/// Deterministic, bounded aggregation of every received row. No cloud model is
/// asked to calculate financial totals or to compress hundreds of thousands of cells.
/// This is a summary, not a searchable copy of all source rows.
/// </summary>
public sealed class TabularProfile(string sheetName)
{
    private readonly List<string> _headers = [];
    private readonly Dictionary<int, decimal> _sums = [];
    private readonly Dictionary<string, decimal> _byItem = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _byWarehouse = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _byItemQuantity = new(StringComparer.Ordinal);
    private int _itemColumn = -1, _warehouseColumn = -1, _amountColumn = -1;
    private int _quantityColumn = -1, _stockColumn = -1, _reserveColumn = -1;
    private int _rows, _badNumbers, _unmatchedRows;
    private decimal _available, _stockValue;
    private int _valuedStocks;
    public int Rows => _rows;
    public string SheetName => sheetName;

    public void AddRow(IReadOnlyList<string> values)
    {
        if (_headers.Count == 0)
        {
            if (values.Count == 0 || values.All(string.IsNullOrWhiteSpace)) throw new AppException("TABLE_HEADER", "Пустая строка заголовков.");
            if (values.Count > 256) throw new AppException("TABLE_COLUMNS", "Таблица содержит более 256 столбцов. Разделите её; столбцы не были скрыто отброшены.");
            _headers.AddRange(values);
            _itemColumn = Find("номенклатур", "кодтовар", "код товара", "товар", "артикул");
            _warehouseColumn = Find("склад");
            _quantityColumn = Find("количеств", "кол-во");
            _stockColumn = Find("остаток");
            _reserveColumn = Find("резерв");
            _amountColumn = Find("итого", "выручк", "сумма с ндс", "суммабезндс", "сумма без ндс", "оборот");
            if (_amountColumn < 0) _amountColumn = Find("стоимость остатка");
            return;
        }
        if (++_rows > 1_000_000) throw new AppException("TABLE_ROWS", "Таблица содержит более миллиона строк. Для обработки требуется пакетный импорт по частям.");
        string Value(int idx) => idx < 0 || idx >= values.Count ? "" : values[idx];
        decimal? Number(int idx)
        {
            var value = Value(idx);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!TryNumber(value, out var number)) { if (idx >= 0 && !value.StartsWith('=') && !value.Contains("[кэш:", StringComparison.Ordinal)) _badNumbers++; return null; }
            return number;
        }
        for (int i = 0; i < Math.Min(_headers.Count, values.Count); i++)
        {
            if (!MonetaryHeader(_headers[i])) continue;
            if (Number(i) is { } v) _sums[i] = _sums.GetValueOrDefault(i) + v;
        }
        decimal? revenue = Number(_amountColumn);
        var item = Value(_itemColumn).Trim();
        var warehouse = Value(_warehouseColumn).Trim();
        if (revenue.HasValue)
        {
            if (item.Length > 0) _byItem[item] = _byItem.GetValueOrDefault(item) + revenue.Value;
            if (warehouse.Length > 0) _byWarehouse[warehouse] = _byWarehouse.GetValueOrDefault(warehouse) + revenue.Value;
            if (item.Length == 0 && _itemColumn >= 0) _unmatchedRows++;
        }
        if (item.Length > 0 && Number(_quantityColumn) is { } qty)
            _byItemQuantity[item] = _byItemQuantity.GetValueOrDefault(item) + qty;
        // Stock book may store formulas with no cached results. Derive from source
        // quantities and cost rather than trusting a model-generated formula value.
        if (_stockColumn >= 0 && _reserveColumn >= 0 && Number(_stockColumn) is { } stock && Number(_reserveColumn) is { } reserved)
        {
            var available = stock - reserved;
            _available += available;
            int costIndex = Find("себестоимость");
            if (Number(costIndex) is { } cost) { _stockValue += available * cost; _valuedStocks++; }
        }
    }

    private int Find(params string[] matches)
    {
        for (int i = 0; i < _headers.Count; i++)
        {
            var h = _headers[i].Trim().ToLowerInvariant();
            if (matches.Any(m => h.Contains(m, StringComparison.Ordinal))) return i;
        }
        return -1;
    }
    private static bool MonetaryHeader(string header)
    {
        var s = header.ToLowerInvariant();
        return !s.Contains('%') && !s.Contains("ставка") &&
            (s.Contains("сумм") || s.Contains("итого") || s.Contains("выруч") || s.Contains("ндс") || s.Contains("стоимость"));
    }
    public static bool TryNumber(string raw, out decimal number)
    {
        // Only parse literal/cached numeric values. Do not execute workbook formulas.
        var value = raw.Trim().Replace("\u00a0", "").Replace(" ", "").Replace("₽", "");
        if (value.StartsWith('='))
        {
            var marker = value.IndexOf("[кэш:", StringComparison.Ordinal);
            if (marker < 0) { number = 0; return false; }
            value = value[(marker + 5)..].TrimEnd(']', ' ');
        }
        var style = NumberStyles.Number | NumberStyles.AllowExponent;
        var russian = CultureInfo.GetCultureInfo("ru-RU");
        // A comma-only value is decimal in Russian exports, not a thousands
        // separator as it would be when parsing under invariant culture.
        if (value.Contains(',') && (!value.Contains('.') || value.LastIndexOf(',') > value.LastIndexOf('.')))
            return decimal.TryParse(value, style, russian, out number);
        if (decimal.TryParse(value, style, CultureInfo.InvariantCulture, out number)) return true;
        return decimal.TryParse(value, style, russian, out number);
    }
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Лист/таблица «{sheetName}». Обработано локально: {_rows} строк данных; {_headers.Count} столбцов.");
        sb.AppendLine("Заголовки: " + string.Join(" | ", _headers.Select((h,i) => ColumnName(i) + ": " + h)));
        foreach (var (index, sum) in _sums.OrderBy(k => k.Key)) sb.AppendLine($"Сумма столбца {ColumnName(index)} («{_headers[index]}») по всем {_rows} строкам: {sum.ToString("0.00", CultureInfo.InvariantCulture)}.");
        if (_stockColumn >= 0 && _reserveColumn >= 0)
        {
            sb.AppendLine($"Доступный остаток (остаток минус резерв), посчитан по всем строкам: {_available.ToString("0.###", CultureInfo.InvariantCulture)}.");
            if (_valuedStocks > 0) sb.AppendLine($"Стоимость доступного остатка по себестоимости, рассчитана программно ({_valuedStocks} строк): {_stockValue.ToString("0.00", CultureInfo.InvariantCulture)}.");
        }
        FormatTop(sb, "ТОП по номенклатуре (сумма выбранного денежного столбца)", _byItem);
        FormatTop(sb, "ТОП складов по денежному обороту", _byWarehouse);
        if (_amountColumn < 0) FormatTop(sb, "ТОП номенклатуры по количеству", _byItemQuantity);
        if (_unmatchedRows > 0) sb.AppendLine($"Строки без кода товара при подсчёте денежного оборота: {_unmatchedRows}.");
        if (_badNumbers > 0) sb.AppendLine($"Нечисловые значения в числовых полях: {_badNumbers}. Они не включены в суммы; проверьте исходник.");
        sb.AppendLine("ОХВАТ: все строки этого листа обработаны локально для указанных агрегатов. Подробные строки НЕ переданы целиком в облако, исходный файл сохранён. Для произвольных построчных вопросов нужна выборка по исходному файлу. Суммы рассчитаны без выполнения формул Excel.");
        return sb.ToString();
    }
    private static void FormatTop(StringBuilder sb, string label, Dictionary<string, decimal> data)
    {
        if (data.Count == 0) return;
        sb.AppendLine($"{label}: групп {data.Count}; первые 15:");
        foreach (var (key,value) in data.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key, StringComparer.Ordinal).Take(15))
            sb.AppendLine($"{key} = {value.ToString("0.00", CultureInfo.InvariantCulture)}");
    }
    private static string ColumnName(int index)
    {
        var name = ""; for (var x=index+1; x>0; x=(x-1)/26) name = (char)('A'+(x-1)%26)+name;
        return name;
    }
}

/// <summary>Bounded CSV record reader; supports quoted delimiters and embedded newlines.</summary>
public static class CsvStreaming
{
    public static IEnumerable<List<string>> Read(TextReader source, char delimiter)
    {
        var field = new StringBuilder(); var row = new List<string>(); bool quoted = false; long cells = 0;
        void EndCell()
        {
            if (++cells > 12_000_000 || row.Count >= 256) throw new AppException("CSV_CELLS", "Таблица превышает безопасное число ячеек или столбцов.");
            row.Add(field.ToString()); field.Clear();
        }
        int c;
        while ((c=source.Read()) >= 0)
        {
            var ch=(char)c;
            if (quoted)
            {
                if (ch=='"' && source.Peek()=='"') { source.Read();field.Append('"'); }
                else if (ch=='"') quoted=false;
                else field.Append(ch);
            }
            else if (ch=='"' && field.Length==0) quoted=true;
            else if (ch==delimiter) EndCell();
            else if (ch=='\r' || ch=='\n')
            {
                if (ch=='\r' && source.Peek()=='\n') source.Read();
                EndCell(); yield return row; row=[];
            }
            else field.Append(ch);
            if (field.Length > 65_536) throw new AppException("CSV_FIELD", "Поле CSV превышает 65 536 символов.");
        }
        if (quoted) throw new AppException("CSV_QUOTES", "В CSV не закрыта кавычка.");
        if (field.Length>0 || row.Count>0) { EndCell();yield return row; }
    }
    public static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted)
            {
                if (ch is '\r' or '\n') break;
                if (counts.ContainsKey(ch)) counts[ch]++;
            }
        }
        return counts.OrderByDescending(x => x.Value).First().Key;
    }
}
