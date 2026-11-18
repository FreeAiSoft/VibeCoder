using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using Agent.Core.Contracts;
using Agent.Core.Models;
using Newtonsoft.Json;

namespace VibeCoder.Services
{
    /// <summary>
    /// SQLite-backed <see cref="IChatHistoryStore"/>. The database lives at
    /// %AppData%\VibeCoder\chat_history.db. VibeCoder does not scope history per workspace, so
    /// <see cref="WorkspaceId"/> is accepted for the shared contract but ignored (global scope).
    /// </summary>
    public sealed class ChatHistoryStore : IChatHistoryStore
    {
        private const int MaxStoredConversations = 50;

        /// <summary>Accepted for <see cref="IChatHistoryStore"/> compatibility; VibeCoder does not scope history.</summary>
        public string WorkspaceId { get; set; } = "";

        public static string DirectoryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCoder");

        public static string DatabasePath => Path.Combine(DirectoryPath, "chat_history.db");

        private static string ConnectionString =>
            "Data Source=" + DatabasePath + ";Version=3;Foreign Keys=True;Default Timeout=5;";

        private const string CreateMessagesSql = @"
CREATE TABLE IF NOT EXISTS messages (
    conversation_id TEXT NOT NULL,
    position INTEGER NOT NULL,
    text TEXT NOT NULL,
    reasoning TEXT NULL,
    mode TEXT NULL,
    kind TEXT NULL,
    tool_name TEXT NULL,
    options TEXT NULL,
    PRIMARY KEY (conversation_id, position),
    FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_messages_conversation ON messages(conversation_id, position);";

        /// <summary>Creates the database and schema if needed.</summary>
        public void Initialize()
        {
            Directory.CreateDirectory(DirectoryPath);

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS conversations (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);" + CreateMessagesSql;
                command.ExecuteNonQuery();
            }

            // Migrate databases created before the kind/tool_name/options columns existed, drop the
            // legacy "role" column, and add the important flag for the shared history contract.
            using (var connection = Open())
            {
                EnsureColumn(connection, "messages", "kind", "TEXT NULL");
                EnsureColumn(connection, "messages", "tool_name", "TEXT NULL");
                EnsureColumn(connection, "messages", "options", "TEXT NULL");
                EnsureColumn(connection, "conversations", "is_important", "INTEGER NOT NULL DEFAULT 0");

                if (HasColumn(connection, "messages", "role"))
                {
                    using (var drop = connection.CreateCommand())
                    {
                        drop.CommandText = "DROP TABLE messages;";
                        drop.ExecuteNonQuery();
                    }
                    using (var recreate = connection.CreateCommand())
                    {
                        recreate.CommandText = CreateMessagesSql;
                        recreate.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>Returns conversation metadata (newest first), without messages.</summary>
        public IReadOnlyList<ChatConversation> ListConversations(int limit = MaxStoredConversations)
        {
            var result = new List<ChatConversation>();
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT id, title, created_at_utc, updated_at_utc, is_important
FROM conversations
ORDER BY updated_at_utc DESC
LIMIT @limit;";
                command.Parameters.AddWithValue("@limit", limit);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(ReadConversationMeta(reader));
                }
            }
            return result;
        }

        /// <summary>Loads a full conversation (messages ordered by position), or null if not found.</summary>
        public ChatConversation LoadConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            using (var connection = Open())
            {
                var conversation = LoadConversationMeta(connection, id);
                if (conversation == null)
                    return null;

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT text, reasoning, mode, kind, tool_name, options
FROM messages
WHERE conversation_id = @id
ORDER BY position;";
                    command.Parameters.AddWithValue("@id", id);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            conversation.Messages.Add(new ChatHistoryMessage
                            {
                                Kind = ParseKind(reader["kind"] as string ?? ""),
                                Text = reader["text"] as string ?? "",
                                Reasoning = reader["reasoning"] as string ?? "",
                                Mode = reader["mode"] as string ?? "",
                                ToolName = reader["tool_name"] as string ?? "",
                                Options = DecodeOptions(reader["options"] as string ?? "")
                            });
                        }
                    }
                }
                return conversation;
            }
        }


        /// <summary>Inserts or updates a conversation and its messages in a single transaction.</summary>
        public void SaveConversation(ChatConversation conversation)
        {
            if (conversation == null || string.IsNullOrWhiteSpace(conversation.Id))
                return;
            if (conversation.Messages == null)
                conversation.Messages = new List<ChatHistoryMessage>();

            // Do not keep conversations that never got a user message (e.g. a fresh "New chat").
            if (!HasUserMessage(conversation))
            {
                DeleteConversation(conversation.Id);
                return;
            }

            conversation.UpdatedAtUtc = DateTime.UtcNow;

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = @"
INSERT OR REPLACE INTO conversations (id, title, created_at_utc, updated_at_utc, is_important)
VALUES (@id, @title, @created, @updated, @important);";
                    upsert.Parameters.AddWithValue("@id", conversation.Id);
                    upsert.Parameters.AddWithValue("@title", string.IsNullOrWhiteSpace(conversation.Title) ? "New chat" : conversation.Title);
                    upsert.Parameters.AddWithValue("@created", ToTimestamp(conversation.CreatedAtUtc));
                    upsert.Parameters.AddWithValue("@updated", ToTimestamp(conversation.UpdatedAtUtc));
                    upsert.Parameters.AddWithValue("@important", conversation.IsImportant ? 1 : 0);
                    upsert.ExecuteNonQuery();
                }

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM messages WHERE conversation_id = @id;";
                    delete.Parameters.AddWithValue("@id", conversation.Id);
                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = @"
INSERT INTO messages (conversation_id, position, text, reasoning, mode, kind, tool_name, options)
VALUES (@id, @position, @text, @reasoning, @mode, @kind, @tool_name, @options);";
                    var idParam = insert.Parameters.Add("@id", System.Data.DbType.String);
                    var positionParam = insert.Parameters.Add("@position", System.Data.DbType.Int64);
                    var textParam = insert.Parameters.Add("@text", System.Data.DbType.String);
                    var reasoningParam = insert.Parameters.Add("@reasoning", System.Data.DbType.String);
                    var modeParam = insert.Parameters.Add("@mode", System.Data.DbType.String);
                    var kindParam = insert.Parameters.Add("@kind", System.Data.DbType.String);
                    var toolNameParam = insert.Parameters.Add("@tool_name", System.Data.DbType.String);
                    var optionsParam = insert.Parameters.Add("@options", System.Data.DbType.String);

                    idParam.Value = conversation.Id;
                    for (var i = 0; i < conversation.Messages.Count; i++)
                    {
                        var message = conversation.Messages[i];
                        positionParam.Value = i;
                        textParam.Value = message.Text;
                        reasoningParam.Value = message.Reasoning;
                        modeParam.Value = message.Mode;
                        kindParam.Value = message.Kind.ToString();
                        toolNameParam.Value = message.ToolName ?? "";
                        optionsParam.Value = EncodeOptions(message.Options);
                        insert.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }

        /// <summary>Sets the important flag without touching messages or timestamps.</summary>
        public void SetConversationImportant(string id, bool isImportant)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE conversations SET is_important = @important WHERE id = @id;";
                command.Parameters.AddWithValue("@important", isImportant ? 1 : 0);
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>Deletes a conversation (messages are removed via ON DELETE CASCADE).</summary>
        public void DeleteConversation(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM conversations WHERE id = @id;";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>Deletes every stored conversation and all of their messages.</summary>
        public void DeleteAllConversations()
        {
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM messages; DELETE FROM conversations;";
                command.ExecuteNonQuery();
            }
        }

        private static bool HasColumn(SQLiteConnection connection, string table, string column)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + table + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader["name"] as string, column, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        private static void EnsureColumn(SQLiteConnection connection, string table, string column, string type)
        {
            if (HasColumn(connection, table, column))
                return;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + type + ";";
                command.ExecuteNonQuery();
            }
        }

        private static ChatMessageKind ParseKind(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out ChatMessageKind kind))
                return kind;
            return ChatMessageKind.SystemNotice;
        }

        private static SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        private static ChatConversation LoadConversationMeta(SQLiteConnection connection, string id)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, title, created_at_utc, updated_at_utc, is_important FROM conversations WHERE id = @id;";
                command.Parameters.AddWithValue("@id", id);
                using (var reader = command.ExecuteReader())
                {
                    return reader.Read() ? ReadConversationMeta(reader) : null;
                }
            }
        }

        private static ChatConversation ReadConversationMeta(SQLiteDataReader reader)
        {
            return new ChatConversation
            {
                Id = reader["id"] as string ?? "",
                Title = reader["title"] as string ?? "",
                CreatedAtUtc = ParseTimestamp(reader["created_at_utc"] as string),
                UpdatedAtUtc = ParseTimestamp(reader["updated_at_utc"] as string),
                IsImportant = Convert.ToInt32(reader["is_important"], CultureInfo.InvariantCulture) != 0
            };
        }

        private static bool HasUserMessage(ChatConversation conversation)
        {
            foreach (var message in conversation.Messages)
                if (message.Kind == ChatMessageKind.User)
                    return true;
            return false;
        }

        private static string ToTimestamp(DateTime value) =>
            value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        private static DateTime ParseTimestamp(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
                return result;
            return default(DateTime);
        }

        private static string EncodeOptions(IReadOnlyList<string> options)
        {
            if (options == null || options.Count == 0)
                return "";
            try { return JsonConvert.SerializeObject(options); }
            catch { return ""; }
        }

        private static List<string> DecodeOptions(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<string>();
            try
            {
                var options = JsonConvert.DeserializeObject<List<string>>(json);
                return options ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}


