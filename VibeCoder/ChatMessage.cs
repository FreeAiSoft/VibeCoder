using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeCoder.Core.Models;
using VibeCoder.Core.Services;

namespace VibeCoder
{
    /// <summary>
    /// A single chat entry: a <see cref="Kind"/> (what the entry is), a typed
    /// <see cref="Payload"/> (its content), and optionally the model's "reasoning".
    /// Rendered per-kind by the chat list.
    /// </summary>
    public class ChatMessage : INotifyPropertyChanged
    {
        private ChatMessageKind _kind;
        private ChatPayload _payload = new TextPayload();
        private string _reasoning = "";
        private string _mode = "";
        private bool _isThinking;
        private bool _isPartial;
        private bool _isToolExpanded;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>What kind of entry this is; drives the label and the renderer.</summary>
        public ChatMessageKind Kind
        {
            get => _kind;
            set
            {
                if (_kind != value)
                {
                    _kind = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Label));
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(SingleLineDisplayText));
                    OnPropertyChanged(nameof(Blocks));
                    OnPropertyChanged(nameof(KindName));
                    OnPropertyChanged(nameof(CanExpandTool));
                }
            }
        }

        /// <summary>String form of <see cref="Kind"/>, for XAML data triggers.</summary>
        public string KindName => Kind.ToString();

        /// <summary>Typed content payload. <see cref="TextPayload"/> for text entries, <see cref="ToolCallPayload"/> for tool calls.</summary>
        public ChatPayload Payload
        {
            get => _payload;
            set
            {
                if (!ReferenceEquals(_payload, value))
                {
                    _payload = value ?? new TextPayload();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Text));
                    OnPropertyChanged(nameof(Label));
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(SingleLineDisplayText));
                    OnPropertyChanged(nameof(Blocks));
                    OnPropertyChanged(nameof(CanExpandTool));
                }
            }
        }

        /// <summary>Plain text of the entry (proxies into a <see cref="TextPayload"/>). Empty for tool calls.</summary>
        public string Text
        {
            get => (Payload as TextPayload)?.Text ?? "";
            set
            {
                if (Payload is TextPayload current)
                {
                    if (current.Text != value)
                    {
                        current.Text = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(DisplayText));
                        OnPropertyChanged(nameof(SingleLineDisplayText));
                        OnPropertyChanged(nameof(Blocks));
                    }
                }
                else
                {
                    Payload = new TextPayload(value);
                }
            }
        }

        public string Reasoning
        {
            get => _reasoning;
            set { if (_reasoning != value) { _reasoning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasReasoning)); OnPropertyChanged(nameof(ShowReasoning)); } }
        }

        /// <summary>Agent mode ("Plan"/"Act") this message was produced in; used for the UI label.</summary>
        public string Mode
        {
            get => _mode;
            set { if (_mode != value) { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(Blocks)); } }
        }

        /// <summary>True while the model is still generating; the reasoning control shows "Thinking…".</summary>
        public bool IsThinking
        {
            get => _isThinking;
            set
            {
                if (_isThinking != value)
                {
                    _isThinking = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ReasoningHeader));
                    OnPropertyChanged(nameof(ShowReasoning));
                }
            }
        }

        /// <summary>True while the entry's text is still being streamed in.</summary>
        public bool IsPartial
        {
            get => _isPartial;
            set { if (_isPartial != value) { _isPartial = value; OnPropertyChanged(); } }
        }

        /// <summary>True when a tool-call message is expanded to show its full output (collapsed by default).</summary>
        public bool IsToolExpanded
        {
            get => _isToolExpanded;
            set
            {
                if (_isToolExpanded != value)
                {
                    _isToolExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(SingleLineDisplayText));
                    OnPropertyChanged(nameof(Blocks));
                }
            }
        }

        /// <summary>True when the tool-call message has more output than the collapsed preview shows.</summary>
        public bool CanExpandTool
        {
            get
            {
                if (Kind != ChatMessageKind.ToolCall)
                    return false;
                var full = (Payload as ToolCallPayload)?.Result ?? "";
                return ToolCallDisplayFormatter.HasMore(full, ToolCallDisplayFormatter.CollapsedPreviewLines);
            }
        }

        /// <summary>When this entry was created (UTC).</summary>
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

        /// <summary>True when the reasoning expander should be visible: while thinking or when reasoning exists.</summary>
        public bool ShowReasoning => IsThinking || HasReasoning;

        /// <summary>Header of the reasoning expander: "Thinking…" while streaming, "Reasoning" when done.</summary>
        public string ReasoningHeader => IsThinking ? "Thinking…" : "Reasoning";

        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case ChatMessageKind.User: return "You: ";
                    case ChatMessageKind.AgentText:
                    case ChatMessageKind.ResultAnswerPlan:
                    case ChatMessageKind.ResultAnswerAct:
                        return string.IsNullOrWhiteSpace(Mode) ? "Agent: " : "Agent [" + Mode + "]: ";
                    case ChatMessageKind.Error: return "⚠ Extension error: ";
                    default: return "";
                }
            }
        }

        public string DisplayText
        {
            get
            {
                if (Kind == ChatMessageKind.ToolCall)
                {
                    var p = Payload as ToolCallPayload;
                    var full = p?.Result ?? "";
                    if (!IsToolExpanded)
                        return ToolCallDisplayFormatter.Collapse(full, ToolCallDisplayFormatter.CollapsedPreviewLines);
                    return full;
                }

                if (Kind == ChatMessageKind.ToolBlocked)
                {
                    var p = Payload as ToolCallPayload;
                    var name = p?.ToolName ?? "";
                    return "⛔ " + name + " blocked in Plan mode.";
                }

                if (Kind == ChatMessageKind.Question)
                {
                    var q = Payload as QuestionPayload;
                    if (q == null) return Text ?? "";
                    return q.Question;
                }

                if (Kind == ChatMessageKind.Subagent)
                {
                    var s = Payload as SubagentPayload;
                    var prompt = s?.Item?.Prompt ?? "";
                    return "⚙ spawn_agent → " + (string.IsNullOrWhiteSpace(prompt) ? "(sub-agent)" : prompt);
                }

                if (Kind == ChatMessageKind.Checkpoint)
                {
                    var c = Payload as CheckpointPayload;
                    return "🔖 Checkpoint " + (c?.ShortHash ?? "") + (string.IsNullOrWhiteSpace(c?.Message) ? "" : " — " + c.Message);
                }

                var t = Text ?? "";
                return string.IsNullOrEmpty(Label) ? t : Label + t;
            }
        }

        /// <summary>Single-line form of <see cref="DisplayText"/> with line breaks collapsed to spaces; used by the collapsed pinned header.</summary>
        public string SingleLineDisplayText => DisplayText.Replace('\r', ' ').Replace('\n', ' ').Trim();

        /// <summary>Message content split into text and code blocks (for syntax-highlighted rendering).</summary>
        public IReadOnlyList<MessageBlock> Blocks => ChatMarkdown.SplitBlocks(DisplayText);

        /// <summary>Raw message text (without the role label) split into text and code blocks.</summary>
        public IReadOnlyList<MessageBlock> TextBlocks => ChatMarkdown.SplitBlocks(Text);

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
