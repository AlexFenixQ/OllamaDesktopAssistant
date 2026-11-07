namespace OllamaDesktop.Core;

/// <summary>
/// Selects the original request without repeating tool side effects or adding
/// the previous assistant answer to the new cloud context.
/// </summary>
public sealed record RegenerationTurn(ChatMessage User, ChatMessage Previous, List<ChatMessage> History);

public static class RegenerationPlanner
{
    public static RegenerationTurn? Select(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0 || messages[^1].Role != "assistant") return null;
        var index = messages.Count - 1;
        var userIndex = -1;
        for (var i = index - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user") { userIndex = i; break; }
        }
        if (userIndex < 0) return null;
        return new RegenerationTurn(messages[userIndex], messages[index], messages.Take(userIndex).ToList());
    }
}
