using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Agent.Core.AgentTools;
using Agent.Core.Checkpoints;
using Agent.Core.Context;
using Agent.Core.Contracts;
using Agent.Core.Models;
using Agent.Core.Services;
using Agent.Models;
using Agent.Toolkit.Wpf.Controls;
using Agent.Toolkit.Wpf.Markdown;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VibeCoder.Core.Services;
using VibeCoder.Services;
using VibeCoder.Services.AgentTools;
using VibeCoder.Services.Checkpoints;

namespace VibeCoder
{
    /// <summary>
    /// VS-specific implementation of the shared <see cref="IChatHost"/> seam: owns the agent loop,
    /// commit control, build errors, docs viewer, theme and tool registry, and delegates the generic
    /// chat UI to the shared <see cref="ChatControl"/>.
    /// </summary>
    public sealed class VibeCoderChatHost : IChatHost
    {
        private readonly ChatControl _view;
        private readonly ChatModel _chat;
        private SettingsData _settings;
        private readonly ContextUsageBinder _contextUsageBinder;
        private IAiProvider _ai;
        private IBuildFeedbackService _buildFeedback;
        private OutputWindowService _outputWindow;
        private readonly GitService _git = new GitService();
        private readonly CheckpointServiceFactory _checkpoints = new CheckpointServiceFactory();
        private AgentToolRegistry? _toolRegistry;
        private AgentRunner? _agentRunner;
        private IMessageNotifier? _messageNotifier;
        private IRetryNotifier? _retryNotifier;
        private IPromptNotifier? _promptNotifier;
        private readonly ThemeService _themeService;

        private CancellationTokenSource? _chatCts;
        private TaskCompletionSource<string>? _questionTcs;
        private ChatMessage? _activeQuestion;
        private readonly DispatcherTimer _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        private readonly DispatcherTimer _reasoningFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly StringBuilder _reasoningBuffer = new StringBuilder();
        private ChatMessage? _currentThinking;

        private const string CommitBranchName = "dev";
        private const string SkipAnswerText = "User skipped the question.";
        private const string CommitMessageSystemPrompt =
            "You are an expert at writing concise git commit messages. " +
            "Summarize the provided changes into a single-line commit message in the format \"type(scope): summary\". " +
            "Write the summary in Russian. " +
            "Respond with only the commit message text — no markdown, no quotes, no extra explanation.";
        private const string CommitOtherSummarySystemPrompt =
            "You are an expert at writing concise git commit messages. " +
            "Summarize the changes below into a single concise line in the imperative mood. " +
            "Write the summary in Russian. " +
            "Respond with only the summary text — no markdown, no quotes, no extra explanation.";
        private const int CommitDiffMaxChars = 8000;
        private const int ErrorContextLines = 15;
        private const int MaxErrorsWithContext = 10;

        // Commit editor controls (built in code, injected into ChatControl.CommitEditorHost).
        private FrameworkElement _commitEditor;
        private TextBlock _commitWarningBar;
        private StackPanel _commitEditorPanel;
        private TextBox _commitMessageBox;
        private Button _commitSendButton;
        private Button _commitAutoGenButton;
        private TextBlock _commitStatusText;
        private TextBlock _commitResultBar;

        public VibeCoderChatHost(ChatControl view, ResourceDictionary windowResources)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _chat = new ChatModel(new ChatHistoryStore(), new UiStateStore());
            _contextUsageBinder = new ContextUsageBinder(_chat, SynchronizationContext.Current);
            _themeService = new ThemeService(windowResources);

            _historySaveTimer.Tick += OnHistorySaveTick;
            _reasoningFlushTimer.Tick += OnReasoningFlushTick;

            LoadSettings();
            ApplyThemeFromSettings();
            RecreateAiProvider();
            _buildFeedback = new BuildFeedbackService();
            _outputWindow = new OutputWindowService();
            RebuildAgentTools();
            InitializeChatHistory();

            BuildCommitEditor();
        }

        public ChatModel Chat => _chat;
        public SettingsData Settings => _settings;

        private void LoadSettings()
        {
            _settings = VibeCoder.Services.SettingsStore.Load();
        }

        public void SaveSettings(SettingsData data)
        {
            _settings = data ?? _settings;
            VibeCoder.Services.SettingsStore.Save(_settings);
            LogService.Configure(_settings.LogFilePath, _settings.ChatLogFilePath);
            RecreateAiProvider();
            RebuildAgentTools();
        }

        private void RecreateAiProvider()
        {
            _contextUsageBinder.Detach();
            UnhookMessageNotifier();
            UnhookRetryNotifier();
            UnhookPromptNotifier();
            _ai = VibeCoder.Services.AiProviderFactory.Create(_settings);
            LogService.Info("AI provider recreated: " + (_ai == null ? "null" : _ai.GetType().Name));
            _contextUsageBinder.Attach(_ai as ITokenUsageProvider, BuildContextBudget());
            HookMessageNotifier();
            HookRetryNotifier();
            HookPromptNotifier();
        }

        private void HookMessageNotifier()
        {
            _messageNotifier = _ai as IMessageNotifier;
            if (_messageNotifier != null)
                _messageNotifier.MessageSent += OnAiMessageSent;
        }

        private void UnhookMessageNotifier()
        {
            if (_messageNotifier != null)
                _messageNotifier.MessageSent -= OnAiMessageSent;
            _messageNotifier = null;
        }

        private void HookRetryNotifier()
        {
            _retryNotifier = _ai as IRetryNotifier;
            if (_retryNotifier != null)
                _retryNotifier.RetryAttempt += OnRetryAttempt;
        }

        private void UnhookRetryNotifier()
        {
            if (_retryNotifier != null)
                _retryNotifier.RetryAttempt -= OnRetryAttempt;
            _retryNotifier = null;
        }

        private void HookPromptNotifier()
        {
            _promptNotifier = _ai as IPromptNotifier;
            if (_promptNotifier != null)
                _promptNotifier.PromptAssembled += OnPromptAssembled;
        }

        private void UnhookPromptNotifier()
        {
            if (_promptNotifier != null)
                _promptNotifier.PromptAssembled -= OnPromptAssembled;
            _promptNotifier = null;
        }

        private void OnAiMessageSent(object? sender, AiMessageEventArgs e)
        {
            LogService.ChatDebug("AI message sent: role=" + (e.Role ?? "") + ", chars=" + (e.Content == null ? 0 : e.Content.Length));
        }

        private void OnPromptAssembled(object? sender, PromptAssembledEventArgs e)
        {
            var dump = FormatPromptDump(e);
            LogService.ChatInfo(dump);
            try
            {
                _outputWindow?.WriteLine(dump);
            }
            catch
            {
                // Logging to the output pane must never break an outgoing request.
            }
        }

        private static string FormatPromptDump(PromptAssembledEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== FULL PROMPT ASSEMBLED =====");
            sb.AppendLine("Estimated input tokens: " + e.EstimatedInputTokens);
            sb.AppendLine("System prompt (" + (e.SystemPrompt == null ? 0 : e.SystemPrompt.Length) + " chars):");
            sb.AppendLine("-----");
            sb.AppendLine(e.SystemPrompt ?? "");
            sb.AppendLine("-----");

            var messages = e.Messages ?? new List<ChatMessageData>();
            sb.AppendLine("Messages (" + messages.Count + "):");
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null) continue;
                var role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role;
                sb.AppendLine("[" + i + "] role=" + role + " (" + (m.Content == null ? 0 : m.Content.Length) + " chars):");
                sb.AppendLine(m.Content ?? "");
                sb.AppendLine("-----");
            }

            sb.AppendLine("===== END FULL PROMPT =====");
            return sb.ToString();
        }

        private void OnRetryAttempt(object? sender, RetryAttemptEventArgs e)
        {
            LogService.ChatWarn(
                "Provider retry #" + e.Attempt +
                " status=" + (e.StatusCode.HasValue ? e.StatusCode.Value.ToString() : "no-response") +
                " reason=" + (e.ReasonPhrase ?? "").Replace("\r", " ").Replace("\n", " ") +
                " willRetry=" + e.WillRetry +
                " waitBeforeRetry=" + e.WaitBeforeRetry +
                " elapsed=" + e.Elapsed +
                " remaining=" + e.Remaining +
                " body=" + (e.Body ?? "").Replace("\r", " ").Replace("\n", " "));
        }

        private void ApplyThemeFromSettings()
        {
            bool isDark = ThemeService.IsDark(_settings.BaseTheme);
            _themeService.ApplyChrome(isDark);
            _themeService.ApplyResolvedTheme(
                isDark,
                ThemeService.ColorFromHex(_settings.PrimaryHex),
                ThemeService.ColorFromHex(_settings.SecondaryHex));
        }

        private void InitializeChatHistory()
        {
            _chat.Run();
            if (!string.IsNullOrEmpty(_chat.LastError))
                _view.AppendExtensionError(_chat.LastError);

            RestoreUiState();
        }

        private void RestoreUiState()
        {
            _view.RestoringUiState = true;
            try
            {
                var state = _chat.LoadUiState();
                _chat.RestoreMode(state.AgentMode);

                if (state.ChatViewMode == ChatViewMode.History)
                {
                    _view.ShowHistoryView();
                }
                else
                {
                    var toOpen = _chat.ResolveConversationToOpen(state.LastConversationId);
                    if (toOpen != null)
                        OpenConversation(toOpen.Id, markDirty: false);
                    else
                        StartNewChat(markDirty: false);
                    _view.ShowConversationView();
                }
            }
            finally
            {
                _view.RestoringUiState = false;
            }
        }

        private void OnHistorySaveTick(object? sender, EventArgs e)
        {
            _historySaveTimer.Stop();
            _chat.SaveHistoryNow();
        }

        private void ScheduleHistorySave()
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(ScheduleHistorySave));
                return;
            }
            _historySaveTimer.Stop();
            _historySaveTimer.Start();
        }

        // ------------------------------------------------------------------
        // IChatHost
        // ------------------------------------------------------------------

        public bool IsAgentRunning => _chatCts != null;

        public async Task SendChatAsync(string text, IReadOnlyList<ChatAttachment> attachments)
        {
            var action = ChatSendRouter.Decide(
                agentBusy: _chatCts != null,
                hasActiveQuestion: _questionTcs != null,
                hasText: !string.IsNullOrWhiteSpace(text));

            if (action == ChatSendAction.AnswerQuestion)
            {
                AnswerActiveQuestion((text ?? "").Trim());
                return;
            }

            if (action == ChatSendAction.Drop)
            {
                _view.AppendChat("The agent is busy — the message was placed into the input box.");
                _view.SetInputText(text ?? "");
                return;
            }

            await SendChatTextAsync(text, attachments);
        }

        public void CancelAgentRun()
        {
            _chatCts?.Cancel();
        }

        public Task ResumeAgentRunAsync()
        {
            _view.AppendChat("Resume is not available for the last run.");
            return Task.CompletedTask;
        }

        public void StartNewChat()
        {
            StartNewChat(markDirty: true);
        }

        private void StartNewChat(bool markDirty)
        {
            _chatCts?.Cancel();
            _chat.StartNewChat(markDirty);
            _view.ShowConversationView();
            if (markDirty)
                ScheduleHistorySave();
        }

        public void OpenConversation(string id)
        {
            OpenConversation(id, markDirty: true);
        }

        private void OpenConversation(string id, bool markDirty)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;
            if (id == _chat.ActiveConversationId)
                return;

            _chatCts?.Cancel();
            if (!_chat.OpenConversation(id, markDirty))
            {
                if (!string.IsNullOrEmpty(_chat.LastError))
                    _view.AppendExtensionError(_chat.LastError);
                return;
            }

            if (markDirty)
                ScheduleHistorySave();
        }

        public void OnConversationRemoved()
        {
            _chatCts?.Cancel();
            _questionTcs = null;
            _activeQuestion = null;
            _historySaveTimer.Stop();
        }

        public void RequestMemoryUpdate()
        {
            _view.SetInputText(ParseMemoryBankMode() == MemoryBankMode.Convention
                ? "update memory bank: read memory-bank/*.md files with read_files, then update the relevant files with editor."
                : "update memory bank: read the current memory bank with read_memory, then update the relevant files with update_memory.");
        }

        public void OpenCreateRobot()
        {
            _view.AppendChat("Create-robot is not available in the VS extension yet.");
        }

        public void RebuildAgentTools()
        {
            var memoryBankMode = ParseMemoryBankMode();
            var registry = BuildToolRegistry(memoryBankMode);
            _toolRegistry = registry;
            if (_agentRunner != null)
            {
                _agentRunner.ToolExecuted -= OnAgentToolExecuted;
                _agentRunner.ContextCompacted -= OnAgentContextCompacted;
                _agentRunner.RetryOccurred -= OnAgentRetryOccurred;
            }
            var toolCallingMode = string.Equals(_settings?.ToolCallingMode, "Native", StringComparison.OrdinalIgnoreCase)
                ? ToolCallingMode.Native
                : ToolCallingMode.JsonChat;
            var allowedPermissions = BuildAllowedPermissions();
            _agentRunner = new AgentRunner(_ai, registry, mode => AgentPromptBuilder.Build(mode, registry, toolCallingMode, allowedPermissions, _settings?.UserName ?? "", _settings?.AddressByName ?? false, memoryBankMode), toolCallingMode: toolCallingMode, allowedPermissions: allowedPermissions);
            _agentRunner.ToolExecuted += OnAgentToolExecuted;
            _agentRunner.ContextCompacted += OnAgentContextCompacted;
            _agentRunner.RetryOccurred += OnAgentRetryOccurred;
        }

        private AgentToolRegistry BuildToolRegistry(MemoryBankMode memoryBankMode)
        {
            var factory = new AgentToolFactory(
                _ai,
                GetOpenSolutionDirectory,
                () => (Package.GetGlobalService(typeof(SDTE)) as DTE2),
                _buildFeedback,
                AskQuestionAsync,
                () => _settings?.SkillsPath,
                memoryBankMode,
                RunSubAgentAsync,
                _settings.AllowSubagents,
                _settings.SubagentMaxDepth,
                SaveCheckpointAsync,
                _settings.CheckpointsEnabled);
            return factory.CreateRegistry();
        }

        private AgentPermissions BuildAllowedPermissions()
        {
            return AgentSettingsMapper.BuildPermissions(_settings);
        }

        private MemoryBankMode ParseMemoryBankMode()
        {
            return AgentSettingsMapper.ParseMemoryBankMode(_settings?.MemoryBankMode);
        }

        private string? GetOpenSolutionDirectory()
        {
            try
            {
                var dte = (Package.GetGlobalService(typeof(SDTE)) as DTE2);
                if (dte?.Solution == null || !dte.Solution.IsOpen)
                    return null;
                var slnPath = dte.Solution.FullName;
                return string.IsNullOrWhiteSpace(slnPath) ? null : Path.GetDirectoryName(slnPath);
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Agent loop
        // ------------------------------------------------------------------

        private async Task SendChatTextAsync(string text, IReadOnlyList<ChatAttachment> attachments)
        {
            if (_chatCts != null) return;
            if (_agentRunner == null) return;

            TrySaveBaselineCheckpoint();

            var images = new List<ChatImagePart>();
            var attachmentNotes = new List<string>();
            var fileBlocks = new StringBuilder();

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    if (attachment == null) continue;

                    if (attachment.IsImage)
                    {
                        images.Add(attachment.ToImagePart());
                        attachmentNotes.Add("Image: " + attachment.FileName);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(attachment.TextContent))
                        {
                            fileBlocks.AppendLine();
                            fileBlocks.AppendLine("Attached file: " + attachment.FileName);
                            fileBlocks.AppendLine("```");
                            fileBlocks.AppendLine(attachment.TextContent);
                            fileBlocks.AppendLine("```");
                        }
                        attachmentNotes.Add("File: " + attachment.FileName);
                    }
                }
            }

            var promptText = (text ?? "").Trim();
            if (fileBlocks.Length > 0)
            {
                promptText = string.IsNullOrWhiteSpace(promptText)
                    ? fileBlocks.ToString().TrimEnd()
                    : promptText.TrimEnd() + Environment.NewLine + fileBlocks.ToString().TrimEnd();
            }

            var uiText = (text ?? "").Trim();
            if (attachmentNotes.Count > 0)
            {
                uiText = string.IsNullOrWhiteSpace(uiText)
                    ? string.Join("  ", attachmentNotes)
                    : uiText + Environment.NewLine + string.Join("  ", attachmentNotes);
            }

            if (string.IsNullOrWhiteSpace(promptText) && images.Count == 0)
                return;

            var modeName = _chat.Mode.ToString();
            var history = BuildTranscript();
            _chat.EnsureActiveConversation();
            _view.AppendChatMessage(new ChatMessage { Kind = ChatMessageKind.User, Text = uiText });

            _currentThinking = null;
            _reasoningBuffer.Clear();
            _reasoningFlushTimer.Stop();

            var cts = new CancellationTokenSource();
            _chatCts = cts;
            StartChatStatus();
            LogService.ChatInfo("Agent chat request started (mode=" + modeName + ", input chars=" + promptText.Length + ", images=" + images.Count + ").");
            try
            {
                var progress = new Progress<ChatStreamDelta>(delta =>
                {
                    if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                    {
                        _reasoningBuffer.Append(delta.ReasoningDelta);
                        _reasoningFlushTimer.Stop();
                        _reasoningFlushTimer.Start();
                    }
                });
                var result = await _agentRunner.RunAsync(history, promptText, images, _chat.Mode, BuildContextBudget(), cts.Token, progress);

                FlushReasoningNow();
                var thinking = _currentThinking;
                if (thinking != null)
                {
                    thinking.IsThinking = false;
                    thinking.IsPartial = false;

                    if (string.IsNullOrWhiteSpace(thinking.Reasoning) && !string.IsNullOrWhiteSpace(result.Reasoning))
                        thinking.Reasoning = result.Reasoning;

                    if (thinking.HasReasoning)
                        thinking.Text = string.Empty;
                    else
                        _chat.Messages.Remove(thinking);
                }

                if (!string.IsNullOrWhiteSpace(result.FinalText))
                {
                    _view.AppendChatMessage(new ChatMessage
                    {
                        Kind = _chat.Mode == AgentMode.Act ? ChatMessageKind.ResultAnswerAct : ChatMessageKind.ResultAnswerPlan,
                        Mode = modeName,
                        Text = result.FinalText
                    });
                }
                StopChatStatus();
                LogService.ChatInfo("Agent chat request finished.");
                ScheduleHistorySave();
            }
            catch (OperationCanceledException)
            {
                FlushReasoningNow();
                var cancelledThinking = _currentThinking;
                if (cancelledThinking != null)
                {
                    cancelledThinking.IsThinking = false;
                    cancelledThinking.IsPartial = false;
                }
                StopChatStatus();
                _view.AppendChat("Run cancelled.");
                ScheduleHistorySave();
            }
            catch (Exception ex)
            {
                FlushReasoningNow();
                if (_currentThinking != null)
                {
                    _currentThinking.IsThinking = false;
                    _currentThinking.IsPartial = false;
                }
                StopChatStatus();
                _view.AppendExtensionError(ex.Message);
                _view.AppendExtensionError(ex.ToString());
                LogService.Error(ex, "Agent chat request failed.");
                ScheduleHistorySave();
            }
            finally
            {
                _chatCts = null;
            }
        }

        private List<ChatMessageData> BuildTranscript()
        {
            return TranscriptBuilder.Build(_chat.Messages);
        }

        private ContextBudget BuildContextBudget()
        {
            return new ContextBudget
            {
                ContextWindowSize = _settings.ContextWindowSize > 0 ? _settings.ContextWindowSize : 409600,
                CompactThresholdFraction = _settings.CompactThreshold,
                ReserveFraction = _settings.ReserveFraction,
                KeepRecentMessages = _settings.KeepRecentMessages,
                ToolResultMaxChars = _settings.ToolResultMaxChars,
                UseToolRole = _settings.UseToolRole,
                MaxIterations = _settings.MaxIterations,
                AutoCompact = _settings.AutoCompact
            };
        }

        private ContextBudget BuildSubAgentBudget()
        {
            var budget = BuildContextBudget();
            budget.MaxIterations = _settings.SubagentMaxIterations > 0 ? _settings.SubagentMaxIterations : 4;
            budget.AutoCompact = false;
            return budget;
        }

        private void StartChatStatus()
        {
            _chat.Phase = AgentPhase.Streaming;
            _chat.StartStatus();
        }

        private void StopChatStatus()
        {
            _chat.StopStatus();
            _chat.Phase = AgentPhase.Idle;
        }

        private async Task<string> AskQuestionAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var message = new ChatMessage
            {
                Kind = ChatMessageKind.Question,
                Payload = new QuestionPayload { Question = question ?? "", Options = options ?? new List<string>() }
            };
            _activeQuestion = message;
            _view.AppendChatMessage(message);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _questionTcs = tcs;
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    return await tcs.Task;
                }
                finally
                {
                    if (tcs.Task.IsCanceled && _activeQuestion?.Payload is QuestionPayload q && string.IsNullOrEmpty(q.Answer))
                        q.Answer = "Cancelled.";
                    _activeQuestion = null;
                    _questionTcs = null;
                }
            }
        }

        private void AnswerActiveQuestion(string text)
        {
            if (_questionTcs == null) return;
            if (string.IsNullOrEmpty(text)) return;
            if (_activeQuestion?.Payload is QuestionPayload q)
                q.Answer = text;
            ScheduleHistorySave();
            _questionTcs.TrySetResult(text);
        }

        /// <summary>
        /// Sends an externally produced message (e.g. an Error List item selected by the user)
        /// to the agent chat, deferring to the input box when the agent is busy.
        /// </summary>
        public async Task SubmitErrorToChatAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_chatCts != null)
            {
                _view.SetInputText(text);
                _view.AppendChat("The agent is busy — the message was placed into the input box.");
                return;
            }

            await SendChatTextAsync(text, null);
        }

        /// <summary>Appends a system notice to the chat (used by the external VS commands).</summary>
        public void AppendSystemMessage(string text) => _view.AppendSystemMessage(text);

        /// <summary>Appends text to the chat input box (used by the external VS commands).</summary>
        public void AppendSelectionToInput(string text) => _view.AppendInputText(text);

        private async Task<string> RunSubAgentAsync(string systemPrompt, string task, CancellationToken ct)
        {
            var registry = _toolRegistry;
            if (registry == null)
                return "spawn_agent unavailable: the tool registry is not initialized.";

            var toolCallingMode = string.Equals(_settings?.ToolCallingMode, "Native", StringComparison.OrdinalIgnoreCase)
                ? ToolCallingMode.Native
                : ToolCallingMode.JsonChat;
            var allowedPermissions = BuildAllowedPermissions();
            var budget = BuildSubAgentBudget();

            var item = new SubagentStatusItem { Prompt = task };
            _view.AppendChatMessage(new ChatMessage
            {
                Kind = ChatMessageKind.Subagent,
                Payload = new SubagentPayload { Item = item },
                Mode = _chat.Mode.ToString()
            });
            _view.Dispatcher.BeginInvoke(new Action(() => item.Status = "running"));

            var runner = new AgentRunner(
                _ai,
                registry,
                m => AgentPromptBuilder.Build(m, registry, toolCallingMode, allowedPermissions),
                budget,
                toolCallingMode: toolCallingMode,
                allowedPermissions: allowedPermissions);

            runner.ToolExecuted += (s, e) =>
            {
                var toolName = e.ToolName ?? "";
                var display = e.DisplayResult;
                var latest = string.IsNullOrWhiteSpace(display) ? toolName : toolName + ": " + display;
                _view.Dispatcher.BeginInvoke(new Action(() =>
                {
                    item.ToolCalls += 1;
                    item.LatestToolCall = latest;
                }));
            };

            try
            {
                var result = await runner.RunAsync(Array.Empty<ChatMessageData>(), task, _chat.Mode, budget, ct);
                var finalText = result?.FinalText ?? "";
                _view.Dispatcher.BeginInvoke(new Action(() => { item.Status = "completed"; item.Result = finalText; }));
                return finalText;
            }
            catch (OperationCanceledException)
            {
                _view.Dispatcher.BeginInvoke(new Action(() => { item.Status = "cancelled"; }));
                throw;
            }
            catch (Exception ex)
            {
                _view.Dispatcher.BeginInvoke(new Action(() => { item.Status = "failed"; item.Error = ex.Message; }));
                return "spawn_agent sub-agent failed: " + ex.Message;
            }
        }

        private void TrySaveBaselineCheckpoint()
        {
            _ = SaveCheckpointAsync("task start", allowEmpty: true, CancellationToken.None);
        }

        private async Task<string> SaveCheckpointAsync(string message, bool allowEmpty, CancellationToken ct)
        {
            if (!_settings.CheckpointsEnabled) return null;

            var ws = GetOpenSolutionDirectory();
            if (string.IsNullOrWhiteSpace(ws)) return null;

            var taskId = _chat.ActiveChatConversation?.Id ?? Guid.NewGuid().ToString("N");
            try
            {
                var result = await Task.Run(async () =>
                {
                    var service = _checkpoints.GetOrCreate(taskId, ws);
                    if (!service.IsInitialized)
                        await service.InitializeAsync(ct).ConfigureAwait(false);
                    return await service.SaveAsync(message, allowEmpty, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

                if (result != null && !result.IsEmpty && !string.IsNullOrWhiteSpace(result.CommitHash))
                {
                    var service = _checkpoints.GetOrCreate(taskId, ws);
                    AppendCheckpointIndicator(taskId, ws, result.CommitHash, message, service.Checkpoints.Count);
                }

                return result?.CommitHash;
            }
            catch (Exception ex)
            {
                LogService.Warn("Checkpoint save failed: " + ex.Message);
                return null;
            }
        }

        private void AppendCheckpointIndicator(string taskId, string workspaceDir, string commitHash, string message, int sequence)
        {
            _view.AppendChatMessage(new ChatMessage
            {
                Kind = ChatMessageKind.Checkpoint,
                Payload = new CheckpointPayload
                {
                    TaskId = taskId,
                    WorkspaceDir = workspaceDir,
                    CommitHash = commitHash,
                    Message = message,
                    Sequence = sequence,
                    CreatedAtUtc = DateTime.UtcNow,
                    HasChanges = true,
                    HasDiff = true,
                }
            });
        }

        private void OnAgentRetryOccurred(object? sender, AgentRetryArgs e)
        {
            LogService.ChatWarn("Agent retry: " + (e.Kind ?? "") + " attempt " + e.Attempt + "/" + e.AttemptLimit + " - " + (e.Detail ?? ""));
        }

        private void OnAgentContextCompacted(object? sender, EventArgs e)
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(() => OnAgentContextCompacted(sender, e)));
                return;
            }
            LogService.ChatInfo("Agent context compacted (summarized) to fit the context window.");
            _view.AppendCompaction("Context compacted (summarized) to fit the context window.");
        }

        public async Task CompactNowAsync()
        {
            if (_agentRunner == null || _chatCts != null)
                return;

            var transcript = BuildTranscript();
            if (transcript.Count == 0)
            {
                _view.AppendChat("Nothing to compact yet.");
                return;
            }

            try
            {
                var condensed = await _agentRunner.CompactAsync(transcript, BuildContextBudget(), CancellationToken.None);

                var compacted = condensed.Select(m => new ChatMessage
                {
                    Kind = m.Role == "assistant" ? ChatMessageKind.AgentText
                         : m.Role == "user" ? ChatMessageKind.User
                         : ChatMessageKind.SystemNotice,
                    Text = m.Content
                }).ToList();
                _chat.ApplyCompactedTranscript(compacted);
                _view.AppendCompaction("Context compacted (summarized).");
                ScheduleHistorySave();
            }
            catch (Exception ex)
            {
                _view.AppendExtensionError("Compact failed: " + ex.Message);
            }
        }

        private void OnAgentToolExecuted(object? sender, AgentToolCall e)
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(() => OnAgentToolExecuted(sender, e)));
                return;
            }
            var detail = string.IsNullOrWhiteSpace(e.DisplayResult) ? e.Result : e.DisplayResult;
            LogService.ChatInfo("Agent tool executed: " + (e.ToolName ?? "") + (e.Blocked ? " (blocked)" : "") + ", result chars=" + (detail == null ? 0 : detail.Length));
            if (string.Equals(e.ToolName, "ask_question", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(e.ToolName, "spawn_agent", StringComparison.OrdinalIgnoreCase))
                return;
            var displayText = e.Blocked
                ? detail
                : ToolCallDisplayFormatter.Format(e.ToolName ?? "", null, e.Result, e.DisplayResult);
            _view.AppendToolCall(e.ToolName, displayText, e.Blocked);
        }

        private void OnAgentTurnStarted(object? sender, EventArgs e)
        {
            if (_chatCts == null)
                return;
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(() => OnAgentTurnStarted(sender, e)));
                return;
            }

            FlushReasoningNow();
            var prev = _currentThinking;
            if (prev != null)
            {
                prev.IsThinking = false;
                prev.IsPartial = false;
                if (prev.HasReasoning)
                    prev.Text = string.Empty;
                else
                    _chat.Messages.Remove(prev);
            }

            var thinking = new ChatMessage
            {
                Kind = ChatMessageKind.AgentText,
                Mode = _chat.Mode.ToString(),
                IsThinking = true,
                IsPartial = true,
                Text = "Thinking…"
            };
            _currentThinking = thinking;
            _reasoningBuffer.Clear();
            _view.AppendChatMessage(thinking);
        }

        private void FlushReasoningNow()
        {
            _reasoningFlushTimer.Stop();
            if (_currentThinking != null && _reasoningBuffer.Length > 0)
                _currentThinking.Reasoning = _reasoningBuffer.ToString();
        }

        private void OnReasoningFlushTick(object? sender, EventArgs e)
        {
            FlushReasoningNow();
        }

        // ------------------------------------------------------------------
        // Docs viewer
        // ------------------------------------------------------------------

        public void ShowDocsIndex()
        {
            RenderDocsFile(Path.Combine(HelpDocumentation.ResolveDocsDirectory(), "00-index.md"), "Documentation");
        }

        private void RenderDocsFile(string path, string title)
        {
            _view.DocsTitleTextControl.Text = title;
            if (!File.Exists(path))
            {
                _view.DocsContentControl.Content = new TextBlock
                {
                    Text = "Documentation file not found: " + path,
                    Foreground = (Brush)_view.FindResource("Dark.Text"),
                    TextWrapping = TextWrapping.Wrap
                };
                return;
            }

            MarkdownRenderer.RelativeLinkNavigator = HandleDocsLink;
            try
            {
                _view.DocsContentControl.Content = MarkdownRenderer.Render(File.ReadAllText(path));
            }
            finally
            {
                MarkdownRenderer.RelativeLinkNavigator = null;
            }
        }

        private bool HandleDocsLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                return false;

            var dir = HelpDocumentation.ResolveDocsDirectory();
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(dir, url));
            }
            catch
            {
                return false;
            }

            var root = Path.GetFullPath(dir);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!File.Exists(target))
                return false;

            RenderDocsFile(target, Path.GetFileName(target));
            return true;
        }

        // ------------------------------------------------------------------
        // Build errors
        // ------------------------------------------------------------------

        public async void SendBuildErrors()
        {
            try
            {
                var errors = await _buildFeedback.GetCompilationErrorsAsync();
                var text = await BuildCompilationErrorsContextAsync(errors);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _view.AppendChat(await FormatNoCompilationErrorsAsync());
                    return;
                }

                await SendChatTextAsync(text, null);
            }
            catch (Exception ex)
            {
                _view.AppendExtensionError(ex.Message);
            }
        }

        private async Task<string> BuildCompilationErrorsContextAsync(IReadOnlyList<BuildError> errors)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (Package.GetGlobalService(typeof(SDTE)) as DTE2);

            return await CompilationErrorContextBuilder.BuildAsync(
                errors,
                async (file, line, ctx) =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (dte == null) throw new InvalidOperationException("Visual Studio is not available.");
                    var doc = await VsDocumentService.OpenOrActivateAsync(dte, file).ConfigureAwait(true);
                    if (doc == null) throw new InvalidOperationException("could not open in the editor");
                    return VsDocumentService.ReadDocumentWindow(doc, line, ctx);
                },
                MaxErrorsWithContext,
                ErrorContextLines).ConfigureAwait(true);
        }

        private async Task<string> FormatNoCompilationErrorsAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("The Visual Studio Error List is empty.");

            string output;
            try
            {
                output = await _buildFeedback.GetOutputWindowTextAsync();
            }
            catch (Exception ex)
            {
                output = "Output window unavailable: " + ex.Message;
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                sb.AppendLine();
                sb.AppendLine("Build output:");
                sb.AppendLine(output);
            }

            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------
        // Commit control
        // ------------------------------------------------------------------

        private void BuildCommitEditor()
        {
            _commitWarningBar = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = Visibility.Collapsed,
                Foreground = Brushes.Orange
            };

            _commitMessageBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 70,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            _commitAutoGenButton = new Button { Content = "Auto-generate", Margin = new Thickness(0, 0, 8, 0) };
            _commitAutoGenButton.Click += OnCommitAutoGenerate;

            _commitSendButton = new Button { Content = "Commit" };
            _commitSendButton.Click += OnCommitSend;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(_commitAutoGenButton);
            buttons.Children.Add(_commitSendButton);

            _commitStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

            _commitResultBar = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            _commitEditorPanel = new StackPanel();
            _commitEditorPanel.Children.Add(_commitMessageBox);
            _commitEditorPanel.Children.Add(buttons);
            _commitEditorPanel.Children.Add(_commitStatusText);
            _commitEditorPanel.Children.Add(_commitResultBar);

            var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += OnCommitClose;

            var root = new StackPanel { Margin = new Thickness(8) };
            root.Children.Add(close);
            root.Children.Add(_commitWarningBar);
            root.Children.Add(_commitEditorPanel);

            _commitEditor = root;
            _view.CommitEditorHost.Content = root;
            _view.CommitEditorHost.Visibility = Visibility.Collapsed;
        }

        public async void OpenCommitControl()
        {
            try
            {
                _commitStatusText.Text = "";
                HideCommitResult();
                LogService.Info("Commit control requested.");
                var solutionDir = GetOpenSolutionDirectory();
                if (solutionDir == null)
                {
                    LogService.Warn("Commit control: no open solution.");
                    ShowCommitWarning("No open solution. Open a solution and retry.");
                    return;
                }

                var repoRoot = await _git.GetRepositoryRootAsync(solutionDir);
                var branch = await _git.GetCurrentBranchAsync(repoRoot);
                if (!string.Equals(branch, CommitBranchName, StringComparison.Ordinal))
                {
                    ShowCommitWarning("Commit is only available on the '" + CommitBranchName + "' branch. Current branch: " + (string.IsNullOrWhiteSpace(branch) ? "(unknown)" : branch));
                    return;
                }

                ShowCommitEditor();
                _commitMessageBox.Focus();
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Commit control: failed to open.");
                ShowCommitWarning("Could not open the commit control: " + ex.Message);
            }
        }

        private async void OnCommitSend(object sender, RoutedEventArgs e)
        {
            var message = _commitMessageBox.Text.Trim();
            if (message.Length == 0)
            {
                _commitStatusText.Text = "Commit message is required.";
                _commitMessageBox.Focus();
                return;
            }

            SetCommitBusy(true);
            _commitStatusText.Text = "Committing…";
            try
            {
                _commitStatusText.Text = "Saving documents…";
                if (!await TrySaveAllDocumentsAsync())
                {
                    _commitStatusText.Text = "Some documents could not be saved. Save them manually and retry.";
                    return;
                }

                var solutionDir = GetOpenSolutionDirectory();
                if (solutionDir == null)
                {
                    ShowCommitWarning("No open solution. Open a solution and retry.");
                    return;
                }

                var repoRoot = await _git.GetRepositoryRootAsync(solutionDir);
                var branch = await _git.GetCurrentBranchAsync(repoRoot);
                if (!string.Equals(branch, CommitBranchName, StringComparison.Ordinal))
                {
                    ShowCommitWarning("Commit is only available on the '" + CommitBranchName + "' branch. Current branch: " + (string.IsNullOrWhiteSpace(branch) ? "(unknown)" : branch));
                    return;
                }

                await _git.StageAllAsync(repoRoot);
                await _git.CommitAsync(repoRoot, message);

                _commitMessageBox.Clear();
                CloseCommitControl();
                LogService.Info("Commit send: success (" + CommitBranchName + ").");
                ShowCommitResult("Committed to " + CommitBranchName + ": " + message, true);
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Commit send failed.");
                ShowCommitResult(ex.Message, false);
            }
            finally
            {
                SetCommitBusy(false);
            }
        }

        private async void OnCommitAutoGenerate(object sender, RoutedEventArgs e)
        {
            if (_ai == null)
            {
                _commitStatusText.Text = "AI provider is not available.";
                return;
            }

            SetCommitBusy(true);
            _commitStatusText.Text = "Generating commit message…";
            try
            {
                _commitStatusText.Text = "Saving documents…";
                if (!await TrySaveAllDocumentsAsync())
                {
                    _commitStatusText.Text = "Some documents could not be saved. Save them manually and retry.";
                    return;
                }

                var solutionDir = GetOpenSolutionDirectory();
                if (solutionDir == null)
                {
                    ShowCommitWarning("No open solution. Open a solution and retry.");
                    return;
                }

                var repoRoot = await _git.GetRepositoryRootAsync(solutionDir);
                var message = await GenerateCommitMessageAsync(repoRoot);
                if (string.IsNullOrWhiteSpace(message))
                {
                    _commitStatusText.Text = "No changes detected. If you have unsaved edits in Visual Studio, save them (Ctrl+Shift+S) and retry.";
                    return;
                }

                _commitMessageBox.Text = message;
                _commitStatusText.Text = "";
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Commit auto-generate failed.");
                _commitStatusText.Text = ex.Message;
            }
            finally
            {
                SetCommitBusy(false);
            }
        }

        private void OnCommitClose(object sender, RoutedEventArgs e)
        {
            CloseCommitControl();
        }

        private async Task<string> SummarizeChangesAsync(string content, string systemPrompt)
        {
            if (content.Length > CommitDiffMaxChars)
                content = content.Substring(0, CommitDiffMaxChars) + "\n… (diff truncated)";
            var messages = new List<ChatMessageData>
            {
                new ChatMessageData { Role = "user", Content = content }
            };
            var response = await _ai.ChatAsync(messages, systemPrompt);
            var text = response?.Answer?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("The AI provider returned an empty message.");
            return text;
        }

        private async Task<string?> GenerateCommitMessageAsync(string repoRoot)
        {
            var changedFiles = await _git.GetChangedFilesAsync(repoRoot);
            if (changedFiles.Count == 0)
                return null;

            var plan = CommitMessageBuilder.GroupChangedFiles(repoRoot, changedFiles);
            if (plan.Projects.Count == 0 && plan.OtherFiles.Count == 0)
                return null;

            if (plan.Projects.Count == 0)
            {
                var diff = await _git.GetDiffForMessageAsync(repoRoot);
                if (string.IsNullOrWhiteSpace(diff))
                    return null;
                return await SummarizeChangesAsync(diff, CommitMessageSystemPrompt);
            }

            var parts = new List<string>();
            foreach (var project in plan.Projects)
            {
                var diff = await _git.GetDiffForPathAsync(repoRoot, project.RelativePath);
                var material = BuildProjectChangeMaterial(project.Name, diff, project.Files);
                var summary = await SummarizeChangesAsync(material, CommitOtherSummarySystemPrompt);
                parts.Add(CommitMessageBuilder.FormatProjectLine(project.Name, summary));
            }
            if (plan.OtherFiles.Count > 0)
            {
                var summary = await SummarizeChangesAsync(BuildOtherChangeMaterial(plan.OtherFiles), CommitOtherSummarySystemPrompt);
                parts.Add(CommitMessageBuilder.FormatChoreLine(summary));
            }
            return CommitMessageBuilder.JoinLines(parts);
        }

        private async Task<bool> TrySaveAllDocumentsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = (Package.GetGlobalService(typeof(SDTE)) as DTE2);
            if (dte?.Documents == null)
                return true;

            try
            {
                dte.ExecuteCommand("File.SaveAll");
            }
            catch (Exception ex)
            {
                LogService.Warn("TrySaveAllDocumentsAsync: File.SaveAll failed: " + ex.Message);
            }

            var unsaved = new List<string>();
            foreach (Document doc in dte.Documents)
            {
                try
                {
                    if (!doc.Saved)
                    {
                        var name = string.IsNullOrWhiteSpace(doc.Name) ? doc.FullName : doc.Name;
                        if (string.IsNullOrWhiteSpace(name))
                            name = "(untitled)";
                        unsaved.Add(name);
                    }
                }
                catch
                {
                    // Ignore documents whose state cannot be read.
                }
            }

            if (unsaved.Count > 0)
            {
                LogService.Warn("TrySaveAllDocumentsAsync: unsaved documents remain: " + string.Join(", ", unsaved));
                return false;
            }

            return true;
        }

        private static string BuildProjectChangeMaterial(string projectName, string diff, IReadOnlyList<string> files)
        {
            var sb = new StringBuilder();
            sb.Append("Project: ").AppendLine(projectName);
            if (!string.IsNullOrWhiteSpace(diff))
            {
                sb.AppendLine("Diff:");
                sb.AppendLine(diff.TrimEnd());
            }
            sb.AppendLine("Changed files:");
            foreach (var f in files)
                sb.AppendLine(f);
            return sb.ToString();
        }

        private static string BuildOtherChangeMaterial(IReadOnlyList<string> files)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Changed files (outside projects):");
            foreach (var f in files)
                sb.AppendLine(f);
            return sb.ToString();
        }

        private void ShowCommitWarning(string message)
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(() => ShowCommitWarning(message)));
                return;
            }
            _view.CommitEditorHost.Visibility = Visibility.Visible;
            _commitWarningBar.Visibility = Visibility.Visible;
            _commitEditorPanel.Visibility = Visibility.Collapsed;
            _commitWarningBar.Text = message;
        }

        private void ShowCommitEditor()
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(ShowCommitEditor));
                return;
            }
            _view.CommitEditorHost.Visibility = Visibility.Visible;
            _commitWarningBar.Visibility = Visibility.Collapsed;
            _commitEditorPanel.Visibility = Visibility.Visible;
            _commitMessageBox.Clear();
            _commitStatusText.Text = "";
        }

        private void CloseCommitControl()
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(CloseCommitControl));
                return;
            }
            _view.CommitEditorHost.Visibility = Visibility.Collapsed;
            _commitWarningBar.Visibility = Visibility.Collapsed;
            _commitEditorPanel.Visibility = Visibility.Collapsed;
        }

        private void SetCommitBusy(bool busy)
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(() => SetCommitBusy(busy)));
                return;
            }
            _commitSendButton.IsEnabled = !busy;
            _commitAutoGenButton.IsEnabled = !busy;
        }

        private void ShowCommitResult(string message, bool success)
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(() => ShowCommitResult(message, success)));
                return;
            }
            _commitResultBar.Foreground = success ? Brushes.Green : Brushes.OrangeRed;
            _commitResultBar.Text = message;
            _commitResultBar.Visibility = Visibility.Visible;
        }

        private void HideCommitResult()
        {
            if (!_view.Dispatcher.CheckAccess())
            {
                _view.Dispatcher.BeginInvoke(new Action(HideCommitResult));
                return;
            }
            _commitResultBar.Visibility = Visibility.Collapsed;
            _commitResultBar.Text = "";
        }
    }
}

