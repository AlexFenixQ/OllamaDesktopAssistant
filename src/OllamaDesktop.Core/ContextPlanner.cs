using System.Text;
using System.Text.RegularExpressions;

namespace OllamaDesktop.Core;

public static class ContextPlanner
{
    public static List<SourceSegment> Split(IEnumerable<SourceSegment> segments, int length = 6000)
    {
        if (length < 100) throw new ArgumentOutOfRangeException(nameof(length));
        var result = new List<SourceSegment>();
        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0) continue;
            for (var start = 0; start < segment.Text.Length; start += length)
                result.Add(new SourceSegment(segment.Reference + (segment.Text.Length > length ? $" · часть {start / length + 1}" : ""), segment.Text.Substring(start, Math.Min(length, segment.Text.Length - start))));
        }
        return result;
    }
    public static string Pack(IEnumerable<SourceSegment> segments) => string.Join("\n\n", segments.Select(s => "[" + s.Reference + "]\n" + s.Text));
    public static List<SourceSegment> Retrieve(IReadOnlyList<SourceSegment> segments, string query, int budget)
    {
        var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Distinct().ToArray();
        var ranked = segments.Select((s, i) => new { Segment = s, Index = i, Score = words.Sum(w => s.Text.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1 : 0) })
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index);
        var result = new List<(int Index, SourceSegment Segment)>(); var used = 0;
        foreach (var item in ranked)
        {
            var size = item.Segment.Text.Length + item.Segment.Reference.Length + 8;
            if (used + size > budget) continue;
            used += size; result.Add((item.Index, item.Segment));
        }
        return result.OrderBy(x => x.Index).Select(x => x.Segment).ToList();
    }
    public static List<List<SourceSegment>> Batches(IReadOnlyList<SourceSegment> segments, int budget)
    {
        var result = new List<List<SourceSegment>>(); var current = new List<SourceSegment>(); var size = 0;
        foreach (var segment in Split(segments, Math.Min(6000, budget - 512)))
        {
            var addition = segment.Text.Length + segment.Reference.Length + 8;
            if (size + addition > budget && current.Count > 0) { result.Add(current); current = []; size = 0; }
            current.Add(segment); size += addition;
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }
    public static List<ApiMessage> History(IEnumerable<ChatMessage> history, out bool omitted)
    {
        var selected = new List<ApiMessage>(); var used = 0; omitted = false;
        foreach (var message in history.Reverse())
        {
            if (message.Status != "complete" || message.Role is not ("user" or "assistant")) continue;
            var text = message.Content;
            if (message.Artifacts.Count > 0) text += "\nСозданные ранее файлы: " + string.Join(", ", message.Artifacts.Select(a => a.Name)) + ". Их полное содержимое не включено; для редактирования прикрепите файл.";
            if (used + text.Length > Limits.HistoryCharacters) { omitted = true; break; }
            used += text.Length; selected.Add(new ApiMessage { Role = message.Role, Content = text });
        }
        selected.Reverse();
        // Do not start with an orphan assistant message when older context was excluded.
        while (selected.Count > 0 && selected[0].Role == "assistant") selected.RemoveAt(0);
        return selected;
    }
}
