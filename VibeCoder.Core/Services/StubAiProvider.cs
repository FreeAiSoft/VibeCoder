using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VibeCoder.Core.Contracts;
using VibeCoder.Core.Models;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Deterministic AI provider stub (offline/demo mode).
    /// </summary>
    public class StubAiProvider : IAiProvider
    {
        public Task<ChatResponse> ChatAsync(string message, CancellationToken cancellationToken = default)
        {
            return ChatAsync(
                new List<ChatMessageData> { new ChatMessageData { Role = "user", Content = message } },
                "",
                cancellationToken);
        }

        public Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessageData> messages, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var lastUser = (messages ?? new List<ChatMessageData>())
                .LastOrDefault(m => m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
            var text = lastUser?.Content ?? "";
            return Task.FromResult(new ChatResponse
            {
                Reasoning = "",
                Answer = "Заглушка: получено сообщение \"" + text + "\". Настройте OpenAI-совместимый провайдер на вкладке Settings."
            });
        }

        public Task<ChatResponse> ChatAsync(IReadOnlyList<ChatMessageData> messages, string systemPrompt, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            return ChatAsync(messages, systemPrompt, cancellationToken);
        }
    }
}