using System;
using VibeCoder.Core.Models;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Maps each chat-message kind to the id (short name) of the control that renders it.
    /// The WPF template selector looks up a DataTemplate keyed by this id + "Template".
    /// Kept in Core so the exhaustive kind -> control mapping can be unit-tested without WPF.
    /// </summary>
    public static class ChatMessageControlId
    {
        public static string ForKind(ChatMessageKind kind)
        {
            switch (kind)
            {
                case ChatMessageKind.User: return "User";
                case ChatMessageKind.AgentText: return "AgentText";
                case ChatMessageKind.ResultAnswerPlan: return "ResultAnswerPlan";
                case ChatMessageKind.ResultAnswerAct: return "ResultAnswerAct";
                case ChatMessageKind.ToolCall: return "ToolCall";
                case ChatMessageKind.ToolBlocked: return "ToolBlocked";
                case ChatMessageKind.Error: return "Error";
                case ChatMessageKind.SystemNotice: return "SystemNotice";
                case ChatMessageKind.Compaction: return "Compaction";
                case ChatMessageKind.ModeSwitch: return "ModeSwitch";
                case ChatMessageKind.DiffEdit: return "DiffEdit";
                case ChatMessageKind.Question: return "Question";
                case ChatMessageKind.Subagent: return "Subagent";
                case ChatMessageKind.Checkpoint: return "Checkpoint";
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }
}