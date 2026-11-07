using Microsoft.Data.Sqlite;

namespace OllamaDesktop.Core;

// Separate additive database preserves compatibility with existing schema-1 chat history.
// Older releases can still read the original chats.db after rollback.
public sealed class ProjectStore(AppPaths paths)
{
    private SqliteConnection Open()
    {
        var file = paths.Resolve("Projects/projects.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, ForeignKeys = true, DefaultTimeout = 10, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    public void Initialize()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, name TEXT NOT NULL,
              instructions TEXT NOT NULL DEFAULT '', memory_notes TEXT NOT NULL DEFAULT '', updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat_projects(chat_id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_chat_projects_project ON chat_projects(project_id);
            """;
        cmd.ExecuteNonQuery();
    }
    public Dictionary<string,string> ChatMapping()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT chat_id,project_id FROM chat_projects";
        using var reader = cmd.ExecuteReader(); var result = new Dictionary<string,string>(StringComparer.Ordinal);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
    public void SetChatProject(string chatId, string? projectId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.Parameters.AddWithValue("$chat", AppPaths.Id(chatId));
        if (string.IsNullOrWhiteSpace(projectId)) cmd.CommandText = "DELETE FROM chat_projects WHERE chat_id=$chat";
        else
        {
            cmd.CommandText = "INSERT INTO chat_projects(chat_id,project_id) VALUES($chat,$project) " +
                              "ON CONFLICT(chat_id) DO UPDATE SET project_id=$project";
            cmd.Parameters.AddWithValue("$project", AppPaths.Id(projectId));
        }
        cmd.ExecuteNonQuery();
    }
    public List<WorkspaceProject> Projects()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,instructions,memory_notes,updated FROM projects ORDER BY updated DESC";
        using var reader = cmd.ExecuteReader(); var results = new List<WorkspaceProject>();
        while (reader.Read()) results.Add(new WorkspaceProject { Id = reader.GetString(0), Name = reader.GetString(1),
            Instructions = reader.GetString(2), MemoryNotes = reader.GetString(3), Updated = DateTimeOffset.Parse(reader.GetString(4)) });
        return results;
    }
    public WorkspaceProject? GetProject(string? id) => string.IsNullOrWhiteSpace(id)
        ? null : Projects().FirstOrDefault(p => p.Id == AppPaths.Id(id));
    public void SaveProject(WorkspaceProject project)
    {
        AppPaths.Id(project.Id);
        if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 100 ||
            project.Instructions.Length > 4000 || project.MemoryNotes.Length > 4000)
            throw new AppException("PROJECT_LIMIT", "Название или заметки проекта слишком длинные.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO projects(id,name,instructions,memory_notes,updated) VALUES($id,$name,$instructions,$notes,$updated) " +
                          "ON CONFLICT(id) DO UPDATE SET name=$name,instructions=$instructions,memory_notes=$notes,updated=$updated";
        cmd.Parameters.AddWithValue("$id", project.Id); cmd.Parameters.AddWithValue("$name", project.Name);
        cmd.Parameters.AddWithValue("$instructions", project.Instructions);
        cmd.Parameters.AddWithValue("$notes", project.MemoryNotes);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public void DeleteProject(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", AppPaths.Id(id)); cmd.ExecuteNonQuery();
    }
}
