using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace VibeCoder.Core.Models
{
    /// <summary>Operating mode of the agent chat: read-only planning vs. full action.</summary>
    public enum AgentMode
    {
        /// <summary>Agent reads, analyzes and plans only; mutating tools are not exposed.</summary>
        Plan,
        /// <summary>Agent can perform real changes (write files, run builds).</summary>
        Act,
        /// <summary>Support agent that answers from the bundled user documentation.</summary>
        Help
    }

    /// <summary>
    /// Category-level permissions for agent tools. Each tool declares one category;
    /// a tool is allowed only while its category is enabled in the settings.
    /// </summary>
    [Flags]
    public enum AgentPermissions
    {
        /// <summary>Read files, logs and compiler output.</summary>
        ReadFiles = 1,
        /// <summary>Write/edit files.</summary>
        EditFiles = 2,
        /// <summary>Run arbitrary shell/PowerShell commands.</summary>
        ExecuteCommand = 4,
        /// <summary>Fetch or download web resources.</summary>
        WebFetch = 8,
        /// <summary>Call MCP-provided tools.</summary>
        Mcp = 16,
        /// <summary>Spawn sub-agents / delegate tasks (spawn_agent tool).</summary>
        Subagents = 32,
        /// <summary>All permissions enabled.</summary>
        All = ReadFiles | EditFiles | ExecuteCommand | WebFetch | Mcp | Subagents
    }

    /// <summary>How the agent talks to the model about tools.</summary>
    public enum ToolCallingMode
    {
        /// <summary>Model outputs JSON {"action":"final"/"tool_call",...} in plain text.</summary>
        JsonChat,
        /// <summary>Native OpenAI-style tools / tool_calls.</summary>
        Native
    }

    /// <summary>How the agent uses the project memory bank.</summary>
    public enum MemoryBankMode
    {
        /// <summary>Memory bank is not used at all.</summary>
        Disabled,
        /// <summary>Memory bank is exposed as dedicated tools (read_memory / update_memory).</summary>
        Tool,
        /// <summary>Memory bank is a convention: read/update the files with regular file tools.</summary>
        Convention
    }

    /// <summary>
    /// Kind of a chat entry in the UI transcript. The kind determines the label and the
    /// renderer, mirroring Cline's discriminated message kinds (say/ask) with a typed payload.
    /// </summary>
    public enum ChatMessageKind
    {
        /// <summary>A message typed by the user.</summary>
        User,
        /// <summary>The agent's final answer (markdown).</summary>
        AgentText,
        /// <summary>The final answer of the agent in Plan mode (read-only); rendered as the highlighted result window.</summary>
        ResultAnswerPlan,
        /// <summary>The final answer of the agent in Act mode (action); rendered as the highlighted result window.</summary>
        ResultAnswerAct,
        /// <summary>A tool invocation that was executed.</summary>
        ToolCall,
        /// <summary>A tool invocation that was blocked (e.g. mutating tool in Plan mode).</summary>
        ToolBlocked,
        /// <summary>An extension-side error.</summary>
        Error,
        /// <summary>A system notice (informational).</summary>
        SystemNotice,
        /// <summary>A context compaction notice.</summary>
        Compaction,
        /// <summary>A Plan/Act mode switch notice.</summary>
        ModeSwitch,
        /// <summary>A diff edit produced by apply_fix (reserved for a future renderer).</summary>
        DiffEdit,
        /// <summary>An interactive question asked by the agent, with answer options.</summary>
        Question,
        /// <summary>A sub-agent (spawn_agent) status card: live progress and final report.</summary>
        Subagent,

        /// <summary>A saved checkpoint (shadow-git snapshot) with Compare/Restore actions.</summary>
        Checkpoint
    }

    /// <summary>Class of an AI provider failure, used by the agent loop to recover from context overflow.</summary>
    public enum ProviderErrorKind
    {
        /// <summary>No classification available.</summary>
        None,
        /// <summary>Authentication/authorization failed (HTTP 401/403).</summary>
        Auth,
        /// <summary>Rate limit exceeded (HTTP 429).</summary>
        RateLimit,
        /// <summary>The request exceeded the model's context window.</summary>
        ContextWindowExceeded,
        /// <summary>The response was blocked by a content filter.</summary>
        ContentFilter,
        /// <summary>A transient provider-side error worth retrying.</summary>
        Transient,
        /// <summary>Any other, unclassified failure.</summary>
        Other
    }


    /// <summary>Compiler error, warning or message from the Visual Studio Error List.</summary>
    public class BuildError
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Project { get; set; }
        public string Severity { get; set; }
        public int ErrorLevel { get; set; }
        public string ErrorLevelName { get; set; }
        public string Category { get; set; }
    }

    /// <summary>
    /// A snapshot of an exception captured from the Visual Studio debugger while it is
    /// stopped on the exception. Stored as plain text so it can be handed to the agent
    /// without any dependency on EnvDTE.
    /// </summary>
    public class DebuggerExceptionSnapshot
    {
        /// <summary>Fully qualified exception type, e.g. "System.NullReferenceException".</summary>
        public string TypeName { get; set; } = "";

        /// <summary>Short message of the top-level exception.</summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// Full exception dump produced by <c>Exception.ToString()</c>: type, message, stack
        /// trace and the complete chain of nested <c>InnerException</c> values.
        /// </summary>
        public string ExceptionDump { get; set; } = "";

        /// <summary>Reason the debugger entered break mode (e.g. "dbgEventReasonExceptionThrown").</summary>
        public string BreakReason { get; set; } = "";

        /// <summary>Current stack frame / function name at the stop location, when available.</summary>
        public string Location { get; set; } = "";
    }


    /// <summary>Agent response in free-form chat: final text + the model's "reasoning" (when the provider emits it).</summary>
    public class ChatResponse
    {
        public string Reasoning { get; set; }
        public string Answer { get; set; }

        /// <summary>
        /// Provider-reported completion reason, e.g. "stop" (finished normally) or
        /// "length" (cut off at the output-token limit). Empty when the provider
        /// does not report it.
        /// </summary>
        public string FinishReason { get; set; }

        /// <summary>Native tool calls returned by the model (empty in JSON-over-chat mode).</summary>
        public IReadOnlyList<NativeToolCall> ToolCalls { get; set; } = new List<NativeToolCall>();
    }

    /// <summary>OpenAI function-calling schema for one tool.</summary>
    public sealed class ToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>OpenAI JSON Schema of arguments ({"type":"object","properties":...}).</summary>
        public Newtonsoft.Json.Linq.JObject Parameters { get; set; }
    }

    /// <summary>One native tool_call returned by the model.</summary>
    public sealed class NativeToolCall
    {
        /// <summary>tool_call_id that must be echoed back in the tool-result message.</summary>
        public string Id { get; set; }

        /// <summary>function.name.</summary>
        public string Name { get; set; }

        /// <summary>function.arguments parsed as JSON.</summary>
        public Newtonsoft.Json.Linq.JObject Arguments { get; set; }
    }

    /// <summary>An image attached to a chat message, encoded as a base64 data URL part.</summary>
    public sealed class ChatImagePart
    {
        /// <summary>MIME type of the image, e.g. "image/png" or "image/jpeg".</summary>
        public string MediaType { get; set; } = "image/png";

        /// <summary>Base64-encoded image bytes (without the "data:...;base64," prefix).</summary>
        public string DataBase64 { get; set; } = "";
    }


    /// <summary>A single chat message passed to the AI provider (role + content).</summary>
    public class ChatMessageData
    {
        /// <summary>Message role: "system" | "user" | "assistant" | "tool".</summary>
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";

        /// <summary>Images attached to this message (role "user" only). Empty for plain text messages.</summary>
        public IReadOnlyList<ChatImagePart> Images { get; set; } = new List<ChatImagePart>();

        /// <summary>tool_call_id of the native tool call this message answers (role "tool" only).</summary>
        public string ToolCallId { get; set; } = "";

        /// <summary>Native tool calls made by an assistant message (role "assistant" only).</summary>
        public IReadOnlyList<NativeToolCall> ToolCalls { get; set; } = new List<NativeToolCall>();
    }

    /// <summary>A single incremental chunk of a streamed chat response.</summary>
    public class ChatStreamDelta
    {
        /// <summary>Incremental reasoning text, when the model emits a reasoning chunk.</summary>
        public string ReasoningDelta { get; set; }

        /// <summary>Incremental answer content text, when the model emits a content chunk.</summary>
        public string ContentDelta { get; set; }
    }

    /// <summary>Token usage reported by the AI provider for a single request.</summary>
    public class TokenUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }

        /// <summary>
        /// Input tokens served from the provider's prompt cache.
        /// DeepSeek: <c>prompt_cache_hit_tokens</c>; OpenAI: <c>prompt_tokens_details.cached_tokens</c>.
        /// </summary>
        public int CacheHitTokens { get; set; }

        /// <summary>Input tokens not served from cache (DeepSeek: <c>prompt_cache_miss_tokens</c>).</summary>
        public int CacheMissTokens { get; set; }

        /// <summary>Estimated cost of all requests in USD, computed from per-million token prices.</summary>
        public double EstimatedCostUsd { get; set; }
    }

    /// <summary>Outgoing AI message reported by a provider right before it is sent.</summary>
    public class AiMessageEventArgs : EventArgs
    {
        /// <summary>Message role, e.g. "system" or "user".</summary>
        public string Role { get; set; }
        /// <summary>Message text sent to the AI provider.</summary>
        public string Content { get; set; }
    }

    // Reported by a provider when a full outgoing request (system prompt + all messages) is assembled.
    public class PromptAssembledEventArgs : EventArgs
    {
        public string SystemPrompt { get; set; }
        public IReadOnlyList<ChatMessageData> Messages { get; set; }
        public int EstimatedInputTokens { get; set; }
    }

    /// <summary>Reported by a provider on each retry attempt while a request keeps failing.</summary>
    public class RetryAttemptEventArgs : EventArgs
    {
        /// <summary>1-based number of the retry attempt.</summary>
        public int Attempt { get; set; }
        /// <summary>HTTP status code returned by the provider, or null when no response was received.</summary>
        public int? StatusCode { get; set; }
        /// <summary>Reason phrase returned by the provider.</summary>
        public string ReasonPhrase { get; set; }
        /// <summary>Response body returned by the provider (truncated).</summary>
        public string Body { get; set; }
        /// <summary>Delay before the next attempt.</summary>
        public TimeSpan WaitBeforeRetry { get; set; }
        /// <summary>Time elapsed since the first attempt of this request.</summary>
        public TimeSpan Elapsed { get; set; }
        /// <summary>Time remaining until the retry budget is exhausted.</summary>
        public TimeSpan Remaining { get; set; }
        /// <summary>Whether another attempt will be made.</summary>
        public bool WillRetry { get; set; }
    }
}
