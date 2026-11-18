using System.Collections.Generic;
using Agent.Core.Models;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Projects the visible UI transcript (<see cref="ChatMessage"/>) into the role-tagged
    /// messages the AI provider receives (<see cref="ChatMessageData"/>). Kept in Core
    /// (no WPF/VS dependency) so the role mapping can be unit-tested.
    /// </summary>
    public static class TranscriptBuilder
    {
        /// <summary>
        /// Maps chat messages to provider messages. Only <see cref="ChatMessageKind.User"/> and the
        /// agent answer kinds are transcribed; whitespace-only messages, nulls and every other kind
        /// (tool calls, errors, notices, questions, …) are skipped.
        /// </summary>
        public static List<ChatMessageData> Build(IEnumerable<ChatMessage> messages)
        {
            var transcript = new List<ChatMessageData>();
            if (messages == null)
                return transcript;

            foreach (var message in messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Text))
                    continue;

                switch (message.Kind)
                {
                    case ChatMessageKind.User:
                        transcript.Add(new ChatMessageData { Role = "user", Content = message.Text });
                        break;
                    case ChatMessageKind.AgentText:
                    case ChatMessageKind.ResultAnswerPlan:
                    case ChatMessageKind.ResultAnswerAct:
                        transcript.Add(new ChatMessageData { Role = "assistant", Content = message.Text });
                        break;
                }
            }

            return transcript;
        }
    }
}
