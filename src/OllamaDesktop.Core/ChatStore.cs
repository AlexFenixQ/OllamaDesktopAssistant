using Microsoft.Data.Sqlite;

namespace OllamaDesktop.Core;

public sealed class ChatStore(AppPaths paths)
{
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, ForeignKeys = true, DefaultTimeout = 10, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    public void Initialize()
    {
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > BuildInfo.SchemaVersion) throw new AppException("DATABASE_NEWER", "База истории создана более новой версией приложения. Обратное преобразование запрещено.");
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS chats(id TEXT PRIMARY KEY, title TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY, chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                created TEXT NOT NULL, role TEXT NOT NULL, status TEXT NOT NULL, content TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_messages_chat ON messages(chat_id,created);
            PRAGMA user_version=1;
            UPDATE messages SET status='interrupted' WHERE status='streaming';
            """;
        command.ExecuteNonQuery();
        new ProjectStore(paths).Initialize();
    }
    public void SaveConversation(Conversation chat)
    {
        AppPaths.Id(chat.Id);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO chats(id,title,updated) VALUES($id,$title,$updated) ON CONFLICT(id) DO UPDATE SET title=$title,updated=$updated";
        cmd.Parameters.AddWithValue("$id", chat.Id); cmd.Parameters.AddWithValue("$title", chat.Title); cmd.Parameters.AddWithValue("$updated", chat.Updated.ToString("O")); cmd.ExecuteNonQuery();
        new ProjectStore(paths).SetChatProject(chat.Id, chat.ProjectId);
    }
    public List<Conversation> List(string search = "")
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,title,updated FROM chats ORDER BY updated DESC";
        using var reader = cmd.ExecuteReader(); var all = new List<Conversation>();
        while (reader.Read()) all.Add(new Conversation { Id = reader.GetString(0), Title = reader.GetString(1), Updated = DateTimeOffset.Parse(reader.GetString(2)) });
        var mapping = new ProjectStore(paths).ChatMapping();
        foreach (var chat in all) if (mapping.TryGetValue(chat.Id, out var projectId)) chat.ProjectId = projectId;
        if (string.IsNullOrWhiteSpace(search)) return all;
        var matching = all.Where(x => x.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
        using var messageCommand = c.CreateCommand(); messageCommand.CommandText = "SELECT chat_id,content FROM messages";
        reader.Close(); using var messages = messageCommand.ExecuteReader();
        while (messages.Read()) if (messages.GetString(1).Contains(search, StringComparison.OrdinalIgnoreCase)) matching.Add(messages.GetString(0));
        return all.Where(x => matching.Contains(x.Id)).ToList();
    }
    public List<ChatMessage> LoadMessages(string chatId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload,content,status FROM messages WHERE chat_id=$id ORDER BY created,rowid";
        cmd.Parameters.AddWithValue("$id", AppPaths.Id(chatId));
        using var reader = cmd.ExecuteReader(); var result = new List<ChatMessage>();
        while (reader.Read())
        {
            var message = JsonDefaults.Read<ChatMessage>(reader.GetString(0));
            message.Content = reader.GetString(1); message.Status = reader.GetString(2); result.Add(message);
        }
        return result;
    }
    public void SaveMessage(ChatMessage message)
    {
        AppPaths.Id(message.ChatId); AppPaths.Id(message.Id);
        using var c = Open(); using var transaction = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO messages(id,chat_id,created,role,status,content,payload) VALUES($id,$chat,$created,$role,$status,$content,$payload)
            ON CONFLICT(id) DO UPDATE SET status=$status,content=$content,payload=$payload;
            UPDATE chats SET updated=MAX(updated,$created) WHERE id=$chat;
            """;
        cmd.Parameters.AddWithValue("$id", message.Id); cmd.Parameters.AddWithValue("$chat", message.ChatId);
        cmd.Parameters.AddWithValue("$created", message.Created.ToString("O")); cmd.Parameters.AddWithValue("$role", message.Role);
        cmd.Parameters.AddWithValue("$status", message.Status); cmd.Parameters.AddWithValue("$content", message.Content); cmd.Parameters.AddWithValue("$payload", JsonDefaults.Write(message));
        cmd.ExecuteNonQuery(); transaction.Commit();
    }
    public void DeleteConversation(string chatId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM chats WHERE id=$id"; cmd.Parameters.AddWithValue("$id", AppPaths.Id(chatId)); cmd.ExecuteNonQuery();
        new ProjectStore(paths).SetChatProject(chatId, null);
        // Only generated/copy directories owned by this exact chat are removed; saved external copies are never touched.
        foreach (var kind in new[] { "Attachments", "Generated" })
        {
            var path = paths.Resolve(kind + "/" + AppPaths.Id(chatId));
            if (Directory.Exists(path))
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) AppPaths.AssertNotReparse(item);
                Directory.Delete(path, true);
            }
        }
    }
    public List<WorkspaceProject> Projects() => new ProjectStore(paths).Projects();
    public WorkspaceProject? GetProject(string? id) => new ProjectStore(paths).GetProject(id);
    public void SaveProject(WorkspaceProject project) => new ProjectStore(paths).SaveProject(project);
    public void DeleteProject(string id) => new ProjectStore(paths).DeleteProject(id);
}
