using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using VibeCoder.Core.AgentTools;
using VibeCoder.Core.Context;
using VibeCoder.Core.Contracts;
using VibeCoder.Core.Services;
using VibeCoder.Services;
using VibeCoder.Services.AgentTools;
using VibeCoder.Controls;
using VibeCoder.Markdown;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.PlatformUI;
using EnvDTE;
using EnvDTE80;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Input;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using VibeCoder.Core.Models;
using MaterialDesignThemes.Wpf;
using MaterialDesignColors;

namespace VibeCoder
{
    /// <summary>
    /// Single extension window: agent chat and settings.
    /// </summary>
    public partial class VibeCoderToolWindowControl : UserControl
    {
        /// <summary>Explicit UI phase of the current agent turn (drives the footer buttons/indicators).</summary>
        private enum AgentPhase
        {
            Idle,
            Streaming,
            Error
        }

        private SettingsData _settings;
        private IAiProvider _ai;
        private IBuildFeedbackService _buildFeedback;
        private OutputWindowService _outputWindow;
        private readonly GitService _git = new GitService();
        private AgentMode _mode = AgentMode.Plan;
        private AgentPhase _phase = AgentPhase.Idle;
        private AgentToolRegistry? _toolRegistry;
        private AgentRunner? _agentRunner;
        private ITokenUsageProvider? _tokenUsage;
        private int _lastEstimatedInputTokens;
        private IMessageNotifier? _messageNotifier;
        private IRetryNotifier? _retryNotifier;
        private IPromptNotifier? _promptNotifier;
        private MicrophoneRecorder _mic;
        private ISpeechToTextService _stt;
        private bool _isRecording;
        private bool _isTranscribing;
        private readonly ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        private readonly ObservableCollection<ChatAttachment> _attachments = new ObservableCollection<ChatAttachment>();

        private ThemeService _themeService;
        private readonly ObservableCollection<ThemeSwatchItem> _themeSwatches = new ObservableCollection<ThemeSwatchItem>();
        private bool _suppressThemeEvents = true;

        private CancellationTokenSource? _chatCts;
        private TaskCompletionSource<string>? _questionTcs;
        private ChatMessage? _activeQuestion;
        private readonly DispatcherTimer _chatStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _settingsToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private DateTime _chatStartedAt;
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
        private readonly ObservableCollection<ChatConversation> _conversations = new ObservableCollection<ChatConversation>();
        private ChatConversation _currentConversation = new ChatConversation();
        private readonly DispatcherTimer _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        private bool _loadingHistory;
        private ChatViewMode _chatViewMode = ChatViewMode.Conversation;
        // Starts true so XAML load events (e.g. ModeToggle ValueChanged) don't
        // overwrite persisted state before RestoreUiState() runs.
        private bool _restoringUiState = true;
        private ChatMessage? _pinnedMessage;
        private ICollectionView? _chatListView;
        private bool _pinnedExpanded;
        private bool _chatAutoScroll = true;
        private const double ChatAutoScrollThreshold = 1.0;

        public VibeCoderToolWindowControl()
        {
            // Force MaterialDesignThemes.Wpf to load before InitializeComponent parses
            // <md:PackIcon>. The type is referenced only in XAML; a direct code reference
            // is required so the extension's assembly resolver can bind the strong-named
            // dependency when it is loaded from our code rather than from the BAML reader.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PackIcon).TypeHandle);
            this.InitializeComponent();
            ConfigureNumericControls();
            _themeService = new ThemeService(this.Resources);
            _settingsToastTimer.Tick += OnSettingsToastTick;
            MainTabs.SelectionChanged += OnMainTabsSelectionChanged;
            ChatMessages.ScrollChanged += OnChatScrollChanged;
            ChatMessages.CopyMessageRequested += OnCopyMessageRequested;
            ChatMessages.QuestionOptionSelected += OnQuestionOptionSelected;
            ChatMessages.QuestionSkipped += OnQuestionSkipped;
            _chatListView = CollectionViewSource.GetDefaultView(_messages);
            _chatListView.Filter = item => !ReferenceEquals(item, _pinnedMessage);
            ChatMessages.ItemsSource = _chatListView;
            AttachmentsList.ItemsSource = _attachments;
            HistoryList.ItemsSource = _conversations;
            LoadSettings();
            PopulateThemeSwatches();
            ApplyThemeFromSettings();
            ShowPendingOptionsWarnings();
            RecreateAiProvider();
            _mic = new MicrophoneRecorder();
            _mic.LevelChanged += OnMicLevelChanged;
            _stt = SpeechToTextFactory.Create(_settings);
            _buildFeedback = new BuildFeedbackService();
            _outputWindow = new OutputWindowService();
            RebuildAgentTools();
            InitializeChatHistory();
            PopulateGallerySamples();
        }

        /// <summary>
        /// Populates the Gallery tab with one sample per chat-message kind (the message
        /// controls rendered in the chat tab) so the tab doubles as a visual reference.
        /// The notification banners live directly in XAML.
        /// </summary>
        private void PopulateGallerySamples()
        {
            GalleryChatMessages.ItemsSource = new List<ChatMessage>
            {
                new ChatMessage { Kind = ChatMessageKind.User, Text = "Fix the null-check in the parser and add a regression test." },
                new ChatMessage { Kind = ChatMessageKind.AgentText, Text = "Here is the plan:\n\n- Add a null guard in `Parse()`\n- Add a regression test" },
                new ChatMessage { Kind = ChatMessageKind.ResultAnswerPlan, Text = "## Plan\n\n1. Investigate\n2. Patch the parser\n3. Run tests" },
                new ChatMessage { Kind = ChatMessageKind.ResultAnswerAct, Text = "Done. Applied the fix and ran the tests — all green." },
                new ChatMessage { Kind = ChatMessageKind.ToolCall, Payload = new ToolCallPayload { ToolName = "read_file", Result = "…contents of Parser.cs…" } },
                new ChatMessage { Kind = ChatMessageKind.ToolBlocked, Payload = new ToolCallPayload { ToolName = "write_file", Blocked = true } },
                new ChatMessage { Kind = ChatMessageKind.Error, Text = "Request failed: 429 rate limit exceeded." },
                new ChatMessage { Kind = ChatMessageKind.SystemNotice, Text = "Connected to OpenAI-compatible provider." },
                new ChatMessage { Kind = ChatMessageKind.Compaction, Text = "Context compacted: 12000 → 8000 tokens." },
                new ChatMessage { Kind = ChatMessageKind.ModeSwitch, Text = "Switched to Act mode." },
                new ChatMessage { Kind = ChatMessageKind.Question, Payload = new QuestionPayload { Question = "Which file should I patch?", Options = new List<string> { "Parser.cs", "Lexer.cs", "Both" } } },
                new ChatMessage { Kind = ChatMessageKind.Subagent, Payload = new SubagentPayload { Item = new SubagentStatusItem { Prompt = "Find all TODO comments", Status = "completed", ToolCalls = 3, LatestToolCall = "grep TODO", Result = "Found 12 TODOs across 4 files." } } },
                new ChatMessage { Kind = ChatMessageKind.DiffEdit, Text = "apply_fix diff (renderer reserved)." },
            };
        }

        /// <summary>
        /// MaterialDesign NumericUpDown/DoubleUpDown inherit from a generic base whose
        /// Minimum/Maximum/ValueStep properties cannot be set from XAML (the VSIX BAML
        /// reader throws NotImplementedException for generic-inherited properties).
        /// Configure those properties here instead, before LoadSettings assigns values.
        /// </summary>
        private void ConfigureNumericControls()
        {
            ConfigureUpDown(ContextWindowUpDown, 1, 1024);
            ConfigureUpDown(KeepRecentUpDown, 1, 1);
            ConfigureUpDown(ToolResultMaxCharsUpDown, 1, 100);
            ConfigureUpDown(MaxIterationsUpDown, 1, 1);
            ConfigureUpDown(SubagentMaxIterationsUpDown, 1, 1);
            ConfigureUpDown(SubagentMaxDepthUpDown, 1, 1);
            ConfigureUpDown(TimeoutSecondsUpDown, 1, 10);
            ConfigureUpDown(RetryTimeoutMinutesUpDown, 1, 1);
            ConfigureUpDown(RetryBaseDelaySecondsUpDown, 1, 1);
            ConfigureUpDown(RetryMaxDelaySecondsUpDown, 1, 5);
            ConfigureUpDown(ProxyPortUpDown, 1, 1);
            ProxyPortUpDown.Maximum = 65535;

            ConfigureUpDown(InputPriceUpDown, 0.0, 0.01);
            ConfigureUpDown(CacheHitPriceUpDown, 0.0, 0.01);
            ConfigureUpDown(OutputPriceUpDown, 0.0, 0.01);
            ConfigureUpDown(CompactThresholdUpDown, 0.0, 0.05, 1.0);
            ConfigureUpDown(ReserveFractionUpDown, 0.0, 0.01, 0.5);
        }

        private static void ConfigureUpDown(NumericUpDown control, int minimum, int step)
        {
            control.Minimum = minimum;
            control.ValueStep = step;
        }

        private static void ConfigureUpDown(DoubleUpDown control, double minimum, double step)
        {
            control.Minimum = minimum;
            control.ValueStep = step;
        }

        private static void ConfigureUpDown(DoubleUpDown control, double minimum, double step, double maximum)
        {
            control.Minimum = minimum;
            control.ValueStep = step;
            control.Maximum = maximum;
        }

        private void LoadSettings()
        {
            _settings = SettingsStore.Load();
            BaseUrlBox.Text = string.IsNullOrWhiteSpace(_settings.BaseUrl) ? "https://api.openai.com/v1" : _settings.BaseUrl;
            ApiKeyBox.Password = _settings.ApiKey ?? "";
            ModelBox.Text = string.IsNullOrWhiteSpace(_settings.Model) ? "gpt-4o-mini" : _settings.Model;
            TemperatureSlider.Value = _settings.Temperature;
            UpdateTemperatureLabel();
            ContextWindowUpDown.Value = _settings.ContextWindowSize > 0 ? _settings.ContextWindowSize : 409600;
            CompactThresholdUpDown.Value = _settings.CompactThreshold;
            ReserveFractionUpDown.Value = _settings.ReserveFraction;
            KeepRecentUpDown.Value = _settings.KeepRecentMessages;
            ToolResultMaxCharsUpDown.Value = _settings.ToolResultMaxChars;
            MaxIterationsUpDown.Value = _settings.MaxIterations;
            AutoCompactSwitch.IsChecked = _settings.AutoCompact;
            UseToolRoleSwitch.IsChecked = _settings.UseToolRole;
            ToolCallingModeCombo.SelectedIndex = string.Equals(_settings.ToolCallingMode, "Native", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            MemoryBankModeCombo.SelectedIndex = MemoryBankModeToIndex(_settings.MemoryBankMode);
            LogFilePathBox.Text = _settings.LogFilePath ?? "";
            ChatLogFilePathBox.Text = _settings.ChatLogFilePath ?? "";
            TimeoutSecondsUpDown.Value = _settings.TimeoutSeconds;
            RetryTimeoutMinutesUpDown.Value = _settings.RetryTimeoutMinutes;
            RetryBaseDelaySecondsUpDown.Value = _settings.RetryBaseDelaySeconds;
            RetryMaxDelaySecondsUpDown.Value = _settings.RetryMaxDelaySeconds;
            InputPriceUpDown.Value = _settings.InputPricePerMillion;
            CacheHitPriceUpDown.Value = _settings.CacheHitPricePerMillion;
            OutputPriceUpDown.Value = _settings.OutputPricePerMillion;
            UserNameBox.Text = _settings.UserName ?? "";
            SkillsPathBox.Text = _settings.SkillsPath ?? "";
            AddressByNameSwitch.IsChecked = _settings.AddressByName;
            AllowSubagentsSwitch.IsChecked = _settings.AllowSubagents;
            SubagentMaxIterationsUpDown.Value = _settings.SubagentMaxIterations;
            SubagentMaxDepthUpDown.Value = _settings.SubagentMaxDepth;
            SpeechToTextProviderCombo.SelectedIndex = SpeechToTextProviderToIndex(_settings.SpeechToTextProvider);
            SpeechToTextModelBox.Text = _settings.SpeechToTextModel ?? "whisper-1";
            SpeechToTextLanguageBox.Text = _settings.SpeechToTextLanguage ?? "ru";
            VoskModelPathBox.Text = _settings.VoskModelPath ?? "";
            SpeechAutoSendSwitch.IsChecked = _settings.SpeechAutoSend;
            ProxyEnabledSwitch.IsChecked = _settings.ProxyEnabled;
            ProxyHostBox.Text = _settings.ProxyHost ?? "";
            ProxyPortUpDown.Value = _settings.ProxyPort > 0 && _settings.ProxyPort <= 65535 ? _settings.ProxyPort : 8080;
            ProxyUsernameBox.Text = _settings.ProxyUsername ?? "";
            ProxyPasswordBox.Password = _settings.ProxyPassword ?? "";

            _suppressThemeEvents = true;
            DarkThemeToggle.IsChecked = ThemeService.IsDark(_settings.BaseTheme);
            SeedHueSlider.Value = _settings.SeedHue;
            ThemeVariantCombo.SelectedIndex = VariantToIndex(_settings.Variant);
            ThemeContrastSlider.Value = _settings.Contrast;
            _suppressThemeEvents = false;
            UpdateSeedHueLabel();
            UpdateContrastLabel();
            UpdateSeedPreview();
        }

        private void OnTemperatureChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateTemperatureLabel();
        }

        private void UpdateTemperatureLabel()
        {
            if (TemperatureValueText != null)
                TemperatureValueText.Text = Math.Round(TemperatureSlider.Value, 1).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private void RecreateAiProvider()
        {
            UnhookTokenUsage();
            UnhookMessageNotifier();
            UnhookRetryNotifier();
            UnhookPromptNotifier();
            _ai = AiProviderFactory.Create(_settings);
            LogService.Info("AI provider recreated: " + (_ai == null ? "null" : _ai.GetType().Name));
            HookTokenUsage();
            HookMessageNotifier();
            HookRetryNotifier();
            HookPromptNotifier();
            UpdateTokenUsageDisplay();
        }

        private void AppendChat(string text)
        {
            AppendChatMessage(new ChatMessage { Kind = ChatMessageKind.SystemNotice, Text = text });
        }

        private void AppendExtensionError(string text)
        {
            AppendChatMessage(new ChatMessage { Kind = ChatMessageKind.Error, Text = text });
        }

        private void AppendToolCall(string toolName, string result, bool blocked)
        {
            AppendChatMessage(new ChatMessage
            {
                Kind = blocked ? ChatMessageKind.ToolBlocked : ChatMessageKind.ToolCall,
                Payload = new ToolCallPayload { ToolName = toolName ?? "", Result = result ?? "", Blocked = blocked }
            });
        }

        private void AppendCompaction(string text)
        {
            AppendChatMessage(new ChatMessage { Kind = ChatMessageKind.Compaction, Text = text });
        }

        private void AppendModeSwitch(string text)
        {
            AppendChatMessage(new ChatMessage { Kind = ChatMessageKind.ModeSwitch, Text = text });
        }

        /// <summary>
        /// Shows an interactive question in the chat (with answer buttons) and waits for the
        /// user's answer. Invoked by the ask_question tool handler, possibly on a background thread.
        /// </summary>
        private async Task<string> AskQuestionAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var message = new ChatMessage
            {
                Kind = ChatMessageKind.Question,
                Payload = new QuestionPayload { Question = question ?? "", Options = options ?? new List<string>() }
            };
            _activeQuestion = message;
            AppendChatMessage(message);

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

        private void OnQuestionOptionSelected(string option)
        {
            if (_questionTcs == null) return;
            if (string.IsNullOrEmpty(option)) return;
            if (_activeQuestion?.Payload is QuestionPayload q)
                q.Answer = option;
            ScheduleHistorySave();
            _questionTcs.TrySetResult(option);
        }

        private void OnQuestionSkipped()
        {
            if (_questionTcs == null) return;
            if (_activeQuestion?.Payload is QuestionPayload q)
            {
                q.Answer = SkipAnswerText;
                q.IsSkipped = true;
            }
            ScheduleHistorySave();
            _questionTcs.TrySetResult(SkipAnswerText);
        }

        private void AppendExtensionError(Exception ex)
        {
            if (ex == null) return;
            LogService.Error(ex);
            AppendExtensionError(ex.Message);
            AppendExtensionError(ex.ToString());
        }

        private void AppendChatMessage(ChatMessage message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendChatMessage(message)));
                return;
            }
            _messages.Add(message);
            if (message.Kind == ChatMessageKind.User && _pinnedMessage == null)
                RefreshPinnedMessage();
            if (_chatAutoScroll)
                ChatMessages.ScrollToEnd();
            if (!_loadingHistory)
                ScheduleHistorySave();
        }


        private void OnCopyMessageRequested(ChatMessage message)
        {
            if (message == null) return;
            try { Clipboard.SetText(FormatMessageForCopy(message)); }
            catch { /* clipboard busy — ignore */ }
        }


        /// <summary>
        /// Forwards the mouse wheel from the code block's horizontal-only ScrollViewer to its
        /// parent, so hovering over a code block still scrolls the chat/spec list vertically.
        /// </summary>
        private void OnCodeScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled)
                return;

            e.Handled = true;

            var parent = (sender as FrameworkElement)?.Parent as UIElement;
            if (parent == null)
                return;

            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = parent
            });
        }

        /// <summary>
        /// Disables chat auto-scroll as soon as the user scrolls away from the bottom
        /// and re-enables it when they scroll back to the bottom.
        /// Content/viewport size changes are ignored so that growing content doesn't
        /// spuriously disable auto-scroll while the user is still at the bottom.
        /// </summary>
        private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            double scrollableHeight = e.ExtentHeight - e.ViewportHeight;
            bool atBottom = e.VerticalOffset >= scrollableHeight - ChatAutoScrollThreshold;
            if (atBottom)
            {
                _chatAutoScroll = true;
            }
            else if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            {
                _chatAutoScroll = false;
            }
        }

        private void OnCopyConversation(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var m in _messages)
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(FormatMessageForCopy(m));
            }
            if (sb.Length == 0) return;
            try { Clipboard.SetText(sb.ToString()); }
            catch { /* clipboard busy — ignore */ }
        }


        private static string FormatMessageForCopy(ChatMessage message)
        {
            var sb = new StringBuilder();
            sb.Append(message.DisplayText);
            if (!string.IsNullOrWhiteSpace(message.Reasoning))
            {
                sb.AppendLine();
                sb.AppendLine("[Reasoning]");
                sb.Append(message.Reasoning);
            }
            return sb.ToString();
        }


        private void HookTokenUsage()
        {
            _tokenUsage = _ai as ITokenUsageProvider;
            if (_tokenUsage != null)
                _tokenUsage.TokenUsageUpdated += OnTokenUsageUpdated;
        }

        private void UnhookTokenUsage()
        {
            if (_tokenUsage != null)
                _tokenUsage.TokenUsageUpdated -= OnTokenUsageUpdated;
            _tokenUsage = null;
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
            // AI message tracing is logged only (not written to the knowledge-base panel),
            // so commit auto-generation and chat traffic no longer pollute the KB "Messages" box.
            LogService.ChatDebug("AI message sent: role=" + (e.Role ?? "") + ", chars=" + (e.Content == null ? 0 : e.Content.Length));
        }

        private void OnPromptAssembled(object? sender, PromptAssembledEventArgs e)
        {
            _lastEstimatedInputTokens = e.EstimatedInputTokens;
            if (!Dispatcher.CheckAccess())
                Dispatcher.BeginInvoke(new Action(UpdateTokenUsageDisplay));
            else
                UpdateTokenUsageDisplay();

            var dump = FormatPromptDump(e);
            LogService.ChatInfo(dump);
            try
            {
                if (_outputWindow != null)
                    _outputWindow.WriteLine(dump);
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
                if (m == null)
                    continue;
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
            // Retry attempts are logged only (not written to the knowledge-base panel).
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


        private void OnTokenUsageUpdated(object? sender, TokenUsage usage)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnTokenUsageUpdated(sender, usage)));
                return;
            }
            UpdateTokenUsageDisplay();
        }

        private void UpdateTokenUsageDisplay()
        {
            var provider = _tokenUsage;
            if (provider == null)
            {
                ChatTokenUsageText.Text = "Context window: —";
                ChatContextFillBar.Value = 0;
                return;
            }
            var usage = provider.TotalTokenUsage;
            var occupied = _lastEstimatedInputTokens;
            var text = "Context window: " + occupied.ToString("N0") +
                       " / " + provider.ContextWindowSize.ToString("N0") + " tokens" +
                       "    total " + usage.TotalTokens.ToString("N0");
            if (usage.CacheHitTokens > 0)
                text += " · cache hit " + usage.CacheHitTokens.ToString("N0");
            if (usage.EstimatedCostUsd > 0)
                text += " · $" + usage.EstimatedCostUsd.ToString("0.0000", CultureInfo.InvariantCulture);
            ChatTokenUsageText.Text = text;

            var ratio = provider.ContextWindowSize > 0
                ? Math.Min(100.0, 100.0 * occupied / provider.ContextWindowSize)
                : 0.0;
            ChatContextFillBar.Value = ratio;
        }

        private void ShowError(Exception ex)
        {
            AppendExtensionError(ex);
        }

        private void ShowNotice(string message, NotificationKind kind = NotificationKind.Info)
        {
            NoticeBar.Kind = kind;
            NoticeBar.Message = message;
            NoticeBar.Visibility = Visibility.Visible;
        }

        private void HideNotice()
        {
            NoticeBar.Visibility = Visibility.Collapsed;
        }

        private void OnNoticeClose(object sender, RoutedEventArgs e)
        {
            HideNotice();
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

        /// <summary>Copies the spec file, unchanged, into the docs folder at the solution root.</summary>

        /// <summary>Rebuilds the list of files shown in the Spec tab's docs expander.</summary>




#pragma warning disable VSTHRD100 // WPF event handlers are async void
        private async void OnSend(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void OnDoItClick(object sender, RoutedEventArgs e)
        {
            InputBox.Text = "Do it!!!";
            await SendMessageAsync();
        }

        private async void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            // Enter — send; Ctrl+Enter — newline (handled by the TextBox itself)
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await SendMessageAsync();
            }
        }

        private void OnClearInput(object sender, RoutedEventArgs e)
        {
            InputBox.Clear();
            InputBox.Focus();
        }

        private async void OnMicClick(object sender, RoutedEventArgs e)
        {
            if (_isTranscribing) return;

            if (!_isRecording)
            {
                try
                {
                    _mic.Start();
                }
                catch (Exception ex)
                {
                    ShowVoiceError("Microphone unavailable: " + ex.Message);
                    return;
                }
                _isRecording = true;
                SetMicState(recording: true);
            }
            else
            {
                byte[] audio;
                try
                {
                    audio = _mic.Stop();
                }
                catch (Exception ex)
                {
                    _isRecording = false;
                    SetMicState(recording: false);
                    ShowVoiceError("Failed to stop recording: " + ex.Message);
                    return;
                }

                _isRecording = false;
                SetMicState(recording: false);

                if (audio == null || audio.Length == 0)
                {
                    AppendSystemMessage("No speech detected.");
                    return;
                }

                _isTranscribing = true;
                SetMicState(recording: false, transcribing: true);
                try
                {
                    var language = (_settings.SpeechToTextLanguage ?? "ru").Trim();
                    var text = await _stt.TranscribeAsync(audio, language, CancellationToken.None);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (_settings.SpeechAutoSend && string.IsNullOrWhiteSpace(InputBox.Text))
                        {
                            InputBox.Text = text;
                            await SendMessageAsync();
                        }
                        else
                        {
                            AppendSelectionToInput(text);
                        }
                    }
                    else
                    {
                        AppendSystemMessage("No speech recognized.");
                    }
                }
                catch (Exception ex)
                {
                    ShowVoiceError("Speech recognition failed: " + ex.Message);
                }
                finally
                {
                    _isTranscribing = false;
                    SetMicState(recording: false);
                }
            }
        }

        private void OnMicLevelChanged(object sender, double level)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMicLevelChanged(sender, level)));
                return;
            }

            if (_isRecording)
                MicButton.Opacity = 0.6 + level * 0.4;
            else
                MicButton.Opacity = 1.0;
        }

        private void SetMicState(bool recording, bool transcribing = false)
        {
            MicButton.ToolTip = transcribing ? "Transcribing…" : (recording ? "Stop recording" : "Start voice input");
            MicButton.Opacity = 1.0;
            MicIcon.Fill = transcribing
                ? Brushes.Orange
                : recording
                    ? Brushes.Red
                    : (Brush)FindResource("Dark.Text");
        }

        private static int SpeechToTextProviderToIndex(string value)
        {
            if (string.Equals(value, "OpenAiWhisper", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(value, "Vosk", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        private static string SpeechToTextProviderIndexToName(int index)
        {
            switch (index)
            {
                case 1: return "OpenAiWhisper";
                case 2: return "Vosk";
                default: return "Auto";
            }
        }

        private void OnAttach(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Attach image or text file",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true)
                return;

            foreach (var fileName in dlg.FileNames)
                TryAddAttachment(fileName);
        }

        private void TryAddAttachment(string fileName)
        {
            try
            {
                var extension = Path.GetExtension(fileName).ToLowerInvariant();
                var bytes = File.ReadAllBytes(fileName);

                if (IsImageExtension(extension))
                {
                    if (bytes.Length > 10 * 1024 * 1024)
                    {
                        AppendChat("Skipped " + Path.GetFileName(fileName) + ": image is larger than 10 MB.");
                        return;
                    }

                    _attachments.Add(new ChatAttachment
                    {
                        FileName = Path.GetFileName(fileName),
                        IsImage = true,
                        MediaType = GetImageMediaType(extension),
                        DataBase64 = Convert.ToBase64String(bytes)
                    });
                    return;
                }

                if (IsProbablyBinary(bytes))
                {
                    AppendChat("Skipped " + Path.GetFileName(fileName) + ": binary files cannot be attached as text.");
                    return;
                }

                _attachments.Add(new ChatAttachment
                {
                    FileName = Path.GetFileName(fileName),
                    IsImage = false,
                    TextContent = DecodeText(bytes)
                });
            }
            catch (Exception ex)
            {
                AppendChat("Could not attach " + fileName + ": " + ex.Message);
            }
            finally
            {
                UpdateAttachmentsVisibility();
            }
        }

        private void OnRemoveAttachment(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button { Tag: ChatAttachment attachment }))
                return;
            _attachments.Remove(attachment);
            UpdateAttachmentsVisibility();
        }

        private void UpdateAttachmentsVisibility()
        {
            AttachmentsList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool IsImageExtension(string extension)
        {
            switch (extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                    return true;
                default:
                    return false;
            }
        }

        private static string GetImageMediaType(string extension)
        {
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".gif":
                    return "image/gif";
                case ".bmp":
                    return "image/bmp";
                case ".webp":
                    return "image/webp";
                default:
                    return "image/png";
            }
        }

        private static bool IsProbablyBinary(byte[] bytes)
        {
            var length = Math.Min(bytes.Length, 8000);
            for (var i = 0; i < length; i++)
            {
                if (bytes[i] == 0)
                    return true;
            }
            return false;
        }

        private static string DecodeText(byte[] bytes)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }



        /// <summary>Deletes all files and subfolders inside the solution's docs folder after confirmation.</summary>



        /// <summary>
        /// Saves an md file. If it already exists — offers full overwrite,
        /// partial fix via diff, or leaving it unchanged.
        /// </summary>


















#pragma warning restore VSTHRD100

        private void StartChatStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(StartChatStatus));
                return;
            }
            SetPhase(AgentPhase.Streaming);
            AgentStatusText.Visibility = Visibility.Visible;
            _chatStartedAt = DateTime.UtcNow;
            _chatStatusTimer.Tick -= OnChatStatusTick;
            _chatStatusTimer.Tick += OnChatStatusTick;
            _chatStatusTimer.Start();
            OnChatStatusTick(null, EventArgs.Empty);
        }

        private void OnChatStatusTick(object? sender, EventArgs e)
        {
            var elapsed = (int)(DateTime.UtcNow - _chatStartedAt).TotalSeconds;
            var status = elapsed < 2 ? "Sending request…" : "Thinking…";
            AgentStatusText.Text = status + " (" + elapsed + " s)";
        }

        private void StopChatStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(StopChatStatus));
                return;
            }
            _chatStatusTimer.Stop();
            _chatStatusTimer.Tick -= OnChatStatusTick;
            AgentStatusText.Text = "";
            AgentStatusText.Visibility = Visibility.Collapsed;
            SetPhase(AgentPhase.Idle);
        }

        /// <summary>Switches the footer to the given phase and updates the dependent controls.</summary>
        private void SetPhase(AgentPhase phase)
        {
            if (_phase == phase)
                return;
            _phase = phase;
            SendButton.IsEnabled = phase != AgentPhase.Streaming;
            DoItButton.IsEnabled = phase != AgentPhase.Streaming;
            CancelButton.Visibility = phase == AgentPhase.Streaming ? Visibility.Visible : Visibility.Collapsed;
            AgentProgress.Visibility = phase == AgentPhase.Streaming ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowChatStatusError(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowChatStatusError(message)));
                return;
            }
            _chatStatusTimer.Stop();
            _chatStatusTimer.Tick -= OnChatStatusTick;
            AgentStatusText.Text = "Error: " + message;
            AgentStatusText.Visibility = Visibility.Visible;
            SetPhase(AgentPhase.Error);
        }

        private void OnCancelSend(object sender, RoutedEventArgs e)
        {
            _chatCts?.Cancel();
        }

        #pragma warning disable VSTHRD100 // WPF event handlers are async void

        private async void OnCommit(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitStatusText.Text = "";
                HideCommitResult();
                LogService.Info("Commit control requested.");
                var solutionDir = GetOpenSolutionDirectory();
                if (solutionDir == null)
                {
                    LogService.Warn("Commit control: no open solution.");
                    ShowCommitWarning("No open solution. Open a solution and retry.");
                    return;
                }

                LogService.Info("Commit control: solutionDir=" + solutionDir);
                var repoRoot = await _git.GetRepositoryRootAsync(solutionDir);
                var branch = await _git.GetCurrentBranchAsync(repoRoot);
                LogService.Info("Commit control: repoRoot=" + repoRoot + ", branch=" + branch);
                if (!string.Equals(branch, CommitBranchName, StringComparison.Ordinal))
                {
                    LogService.Warn("Commit control blocked: current branch is not '" + CommitBranchName + "'.");
                    ShowCommitWarning("Commit is only available on the '" + CommitBranchName + "' branch. Current branch: " + (string.IsNullOrWhiteSpace(branch) ? "(unknown)" : branch));
                    return;
                }

                ShowCommitEditor();
                CommitMessageBox.Focus();
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Commit control: failed to open.");
                ShowCommitWarning("Could not open the commit control: " + ex.Message);
            }
        }

        private async void OnCommitSend(object sender, RoutedEventArgs e)
        {
            var message = CommitMessageBox.Text.Trim();
            if (message.Length == 0)
            {
                CommitStatusText.Text = "Commit message is required.";
                CommitMessageBox.Focus();
                return;
            }

            SetCommitBusy(true);
            CommitStatusText.Text = "Committing…";
            LogService.Info("Commit send requested; message=" + message.Length + " chars");
            try
            {
                CommitStatusText.Text = "Saving documents…";
                if (!await TrySaveAllDocumentsAsync())
                {
                    CommitStatusText.Text = "Some documents could not be saved. Save them manually and retry.";
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
                LogService.Info("Commit send: repoRoot=" + repoRoot + ", branch=" + branch);
                if (!string.Equals(branch, CommitBranchName, StringComparison.Ordinal))
                {
                    ShowCommitWarning("Commit is only available on the '" + CommitBranchName + "' branch. Current branch: " + (string.IsNullOrWhiteSpace(branch) ? "(unknown)" : branch));
                    return;
                }

                await _git.StageAllAsync(repoRoot);
                await _git.CommitAsync(repoRoot, message);

                CommitMessageBox.Clear();
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
                CommitStatusText.Text = "AI provider is not available.";
                return;
            }

            SetCommitBusy(true);
            CommitStatusText.Text = "Generating commit message…";
            LogService.Info("Commit auto-generate requested.");
            try
            {
                CommitStatusText.Text = "Saving documents…";
                if (!await TrySaveAllDocumentsAsync())
                {
                    CommitStatusText.Text = "Some documents could not be saved. Save them manually and retry.";
                    return;
                }

                var solutionDir = GetOpenSolutionDirectory();
                if (solutionDir == null)
                {
                    ShowCommitWarning("No open solution. Open a solution and retry.");
                    return;
                }

                var repoRoot = await _git.GetRepositoryRootAsync(solutionDir);
                LogService.Info("Commit auto-generate: repoRoot=" + repoRoot);

                var message = await GenerateCommitMessageAsync(repoRoot);
                if (string.IsNullOrWhiteSpace(message))
                {
                    CommitStatusText.Text = "No changes detected. If you have unsaved edits in Visual Studio, save them (Ctrl+Shift+S) and retry.";
                    return;
                }

                LogService.Info("Commit auto-generate: AI response=" + message.Length + " chars");
                CommitMessageBox.Text = message;
                CommitStatusText.Text = "";
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Commit auto-generate failed.");
                CommitStatusText.Text = ex.Message;
            }
            finally
            {
                SetCommitBusy(false);
            }
        }

        #pragma warning restore VSTHRD100

        /// <summary>
        /// Saves every open document in Visual Studio so the on-disk files reflect the
        /// current editor contents before a commit message is generated or committed.
        /// Returns false when any document remains unsaved (e.g. an untitled buffer).
        /// </summary>
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

            // No project at all — keep the existing generic fallback.
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

        private void OnCommitClose(object sender, RoutedEventArgs e)
        {
            CloseCommitControl();
        }

        private void ShowCommitWarning(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowCommitWarning(message)));
                return;
            }
            CommitControlHost.Visibility = Visibility.Visible;
            CommitWarningBar.Visibility = Visibility.Visible;
            CommitEditorPanel.Visibility = Visibility.Collapsed;
            CommitWarningBar.Kind = NotificationKind.Warning;
            CommitWarningBar.Message = message;
        }

        private void ShowCommitEditor()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowCommitEditor));
                return;
            }
            CommitControlHost.Visibility = Visibility.Visible;
            CommitWarningBar.Visibility = Visibility.Collapsed;
            CommitEditorPanel.Visibility = Visibility.Visible;
            CommitMessageBox.Clear();
            CommitStatusText.Text = "";
        }

        private void CloseCommitControl()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(CloseCommitControl));
                return;
            }
            CommitControlHost.Visibility = Visibility.Collapsed;
            CommitWarningBar.Visibility = Visibility.Collapsed;
            CommitEditorPanel.Visibility = Visibility.Collapsed;
        }

        private void SetCommitBusy(bool busy)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetCommitBusy(busy)));
                return;
            }
            CommitSendButton.IsEnabled = !busy;
            CommitAutoGenButton.IsEnabled = !busy;
        }

        /// <summary>
        /// Shows the commit result as a colored bar under the toolbar buttons instead of
        /// writing it into the chat: green on success, orange on failure.
        /// </summary>
        private void ShowCommitResult(string message, bool success)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowCommitResult(message, success)));
                return;
            }
            CommitResultBar.Kind = success ? NotificationKind.Success : NotificationKind.Error;
            CommitResultBar.Message = message;
            CommitResultBar.Visibility = Visibility.Visible;
        }

        private void HideCommitResult()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(HideCommitResult));
                return;
            }
            CommitResultBar.Visibility = Visibility.Collapsed;
            CommitResultBar.Message = "";
        }

        private void OnCloseCommitResult(object sender, RoutedEventArgs e)
        {
            HideCommitResult();
        }

        private async void OnNotificationSendToChat(object sender, RoutedEventArgs e)
        {
            if (sender is NotificationBanner banner && !string.IsNullOrWhiteSpace(banner.Message))
                await SubmitErrorToChatAsync(banner.Message);
        }

        private async Task SendMessageAsync()
        {
            var text = InputBox.Text.Trim();
            var attachments = _attachments.ToList();
            if (text.Length == 0 && attachments.Count == 0) return;
            InputBox.Clear();
            _attachments.Clear();
            UpdateAttachmentsVisibility();
            await SendChatTextAsync(text, attachments);
        }

        private async void OnSendErrors(object sender, RoutedEventArgs e)
        {
            try
            {
                var errors = await _buildFeedback.GetCompilationErrorsAsync();
                var text = await BuildCompilationErrorsContextAsync(errors);
                if (string.IsNullOrWhiteSpace(text))
                {
                    AppendChat(await FormatNoCompilationErrorsAsync());
                    return;
                }

                await SendChatTextAsync(text);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        /// <summary>
        /// Builds the complete compiler-error context the agent receives: the error text plus,
        /// for each error, a bounded window around the error line read from the open editor tab
        /// (never from disk). The whole context is assembled deterministically by
        /// <see cref="CompilationErrorContextBuilder"/> and returned as one string, so nothing
        /// reaches the chat until the full context has been built.
        /// </summary>
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

        private async Task SendChatTextAsync(string text)
        {
            await SendChatTextAsync(text, null);
        }

        private async Task SendChatTextAsync(string text, IReadOnlyList<ChatAttachment> attachments)
        {
            if (_chatCts != null) return; // already awaiting the agent's reply
            if (_agentRunner == null) return;

            var images = new List<ChatImagePart>();
            var attachmentNotes = new List<string>();
            var fileBlocks = new StringBuilder();

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    if (attachment == null)
                        continue;

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

            var modeName = _mode.ToString();
            var history = BuildTranscript();
            AppendChatMessage(new ChatMessage { Kind = ChatMessageKind.User, Text = uiText });
            if (string.IsNullOrEmpty(_currentConversation.Title))
                _currentConversation.Title = BuildChatTitle(uiText);

            var thinking = new ChatMessage { Kind = ChatMessageKind.AgentText, Mode = modeName, IsThinking = true, IsPartial = true, Text = "Thinking…" };
            AppendChatMessage(thinking);

            var cts = new CancellationTokenSource();
            _chatCts = cts;
            StartChatStatus();
            LogService.ChatInfo("Agent chat request started (mode=" + modeName + ", input chars=" + promptText.Length + ", images=" + images.Count + ").");
            try
            {
                var progress = new Progress<ChatStreamDelta>(delta =>
                {
                    if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                        thinking.Reasoning += delta.ReasoningDelta;
                });
                var result = await _agentRunner.RunAsync(history, promptText, images, _mode, BuildContextBudget(), cts.Token, progress);

                // Finalize the "Thinking…" placeholder in place: it keeps its streamed
                // reasoning (so the thoughts stay visible) and the final answer is appended
                // as a NEW message after any tool calls.
                thinking.IsThinking = false;
                thinking.IsPartial = false;

                // Fall back to the result's reasoning when nothing was streamed.
                if (string.IsNullOrWhiteSpace(thinking.Reasoning) && !string.IsNullOrWhiteSpace(result.Reasoning))
                    thinking.Reasoning = result.Reasoning;

                if (thinking.HasReasoning)
                {
                    // Keep the placeholder as a reasoning-only block above the tool calls.
                    thinking.Text = string.Empty;
                }
                else
                {
                    // No reasoning to show — drop the empty placeholder.
                    _messages.Remove(thinking);
                }

                // Emit the final answer as a new message so it appears after the tool calls.
                if (!string.IsNullOrWhiteSpace(result.FinalText))
                {
                    var finalMessage = new ChatMessage
                    {
                        Kind = _mode == AgentMode.Act ? ChatMessageKind.ResultAnswerAct : ChatMessageKind.ResultAnswerPlan,
                        Mode = modeName,
                        Text = result.FinalText
                    };
                    AppendChatMessage(finalMessage);
                }
                StopChatStatus();
                LogService.ChatInfo("Agent chat completed (mode=" + modeName + ", answer chars=" + (result.FinalText == null ? 0 : result.FinalText.Length) + ").");
            }
            catch (OperationCanceledException)
            {
                thinking.IsThinking = false;
                if (_messages.Contains(thinking))
                    AppendChat("Request cancelled.");
                StopChatStatus();
                LogService.ChatInfo("Agent chat request cancelled.");
            }
            catch (Exception ex)
            {
                thinking.IsThinking = false;
                AppendExtensionError(ex);
                ShowChatStatusError(ex.Message);
                LogService.ChatError(ex, "Agent chat request failed.");
            }
            finally
            {
                _chatCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// Appends a system notice to the chat without invoking the agent.
        /// Safe to call from any thread.
        /// </summary>
        public void AppendSystemMessage(string text)
        {
            AppendChat(text);
        }

        /// <summary>
        /// Appends an externally produced text (e.g. an editor selection) into the chat input box
        /// without sending it. Safe to call from any thread.
        /// </summary>
        public void AppendSelectionToInput(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendSelectionToInput(text)));
                return;
            }

            ShowConversationView();

            var existing = InputBox.Text;
            InputBox.Text = string.IsNullOrWhiteSpace(existing)
                ? text
                : existing.TrimEnd() + Environment.NewLine + text;
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        /// <summary>
        /// Sends an externally produced message (e.g. an Error List item selected by the user)
        /// to the agent chat. When the agent is still processing a previous request, the message
        /// is placed into the input box instead of being silently dropped.
        /// </summary>
        public async Task SubmitErrorToChatAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_chatCts != null)
            {
                InputBox.Text = text;
                AppendChat("The agent is busy — the message was placed into the input box.");
                return;
            }

            await SendChatTextAsync(text);
        }

        /// <summary>Builds the model transcript from visible chat messages, mapping UI roles to model roles.</summary>
        private List<ChatMessageData> BuildTranscript()
        {
            var transcript = new List<ChatMessageData>();
            foreach (var message in _messages)
            {
                if (string.IsNullOrWhiteSpace(message.Text))
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
                    // Other kinds (tool calls, notices, errors) are UI-only and not part of the model conversation.
                }
            }
            return transcript;
        }

        /// <summary>Builds the context budget from persisted settings, falling back to defaults.</summary>
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

        /// <summary>
        /// Runs a nested agent loop for a sub-agent (spawn_agent). Appends a live
        /// <see cref="ChatMessageKind.Subagent"/> status card and updates it in place
        /// as the sub-agent makes tool calls, then returns the sub-agent's final report.
        /// </summary>
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
            AppendChatMessage(new ChatMessage
            {
                Kind = ChatMessageKind.Subagent,
                Payload = new SubagentPayload { Item = item },
                Mode = _mode.ToString()
            });
            Dispatcher.BeginInvoke(new Action(() => item.Status = "running"));

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
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    item.ToolCalls += 1;
                    item.LatestToolCall = latest;
                }));
            };

            try
            {
                var result = await runner.RunAsync(Array.Empty<ChatMessageData>(), task, _mode, budget, ct);
                var finalText = result?.FinalText ?? "";
                Dispatcher.BeginInvoke(new Action(() => { item.Status = "completed"; item.Result = finalText; }));
                return finalText;
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(new Action(() => { item.Status = "cancelled"; }));
                throw;
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() => { item.Status = "failed"; item.Error = ex.Message; }));
                return "spawn_agent sub-agent failed: " + ex.Message;
            }
        }

        private void RebuildAgentTools()
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
            UpdateMemoryBankUi();
        }

        private void OnAgentRetryOccurred(object? sender, AgentRetryArgs e)
        {
            LogService.ChatWarn("Agent retry: " + (e.Kind ?? "") + " attempt " + e.Attempt + "/" + e.AttemptLimit + " - " + (e.Detail ?? ""));
        }

        private void OnAgentContextCompacted(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnAgentContextCompacted(sender, e)));
                return;
            }
            LogService.ChatInfo("Agent context compacted (summarized) to fit the context window.");
            AppendCompaction("Context compacted (summarized) to fit the context window.");
        }

        private async void OnCompactNow(object sender, RoutedEventArgs e)
        {
            if (_agentRunner == null || _chatCts != null)
                return;

            var transcript = BuildTranscript();
            if (transcript.Count == 0)
            {
                AppendChat("Nothing to compact yet.");
                return;
            }

            try
            {
                var condensed = await _agentRunner.CompactAsync(transcript, BuildContextBudget(), CancellationToken.None);

                _messages.Clear();
                foreach (var m in condensed)
                {
                    var kind = m.Role == "assistant" ? ChatMessageKind.AgentText
                             : m.Role == "user" ? ChatMessageKind.User
                             : ChatMessageKind.SystemNotice;
                    _messages.Add(new ChatMessage { Kind = kind, Text = m.Content });
                }
                AppendCompaction("Context compacted (summarized).");
                RefreshPinnedMessage();
                ScheduleHistorySave();
            }
            catch (Exception ex)
            {
                AppendExtensionError("Compact failed: " + ex.Message);
            }
        }

        private void OnUpdateMemory(object sender, RoutedEventArgs e)
        {
            InputBox.Text = ParseMemoryBankMode() == MemoryBankMode.Convention
                ? "update memory bank: read memory-bank/*.md files with read_files, then update the relevant files with editor."
                : "update memory bank: read the current memory bank with read_memory, then update the relevant files with update_memory.";
            InputBox.Focus();
        }

        private void UpdateMemoryBankUi()
        {
            if (UpdateMemoryButton == null) return;
            var mode = ParseMemoryBankMode();
            UpdateMemoryButton.Visibility = mode == MemoryBankMode.Disabled ? Visibility.Collapsed : Visibility.Visible;
            UpdateMemoryButton.ToolTip = mode == MemoryBankMode.Convention
                ? "Ask the agent to update the project memory bank (convention)"
                : "Ask the agent to update the project memory bank";
        }

        private void OnPermissionsClick(object sender, RoutedEventArgs e)
        {
            AllowReadFilesSwitch.IsChecked = _settings.AllowReadFiles;
            AllowEditFilesSwitch.IsChecked = _settings.AllowEditFiles;
            AllowExecuteCommandSwitch.IsChecked = _settings.AllowExecuteCommand;
            AllowWebFetchSwitch.IsChecked = _settings.AllowWebFetch;
            AllowMcpSwitch.IsChecked = _settings.AllowMcp;
            PermissionsOverlay.Visibility = Visibility.Visible;
        }

        private void OnPermissionsClose(object sender, RoutedEventArgs e)
        {
            PermissionsOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnDocsClick(object sender, RoutedEventArgs e)
        {
            ShowDocsIndex();
            DocsOverlay.Visibility = Visibility.Visible;
        }

        private void OnDocsHome(object sender, RoutedEventArgs e)
        {
            ShowDocsIndex();
        }

        private void OnDocsClose(object sender, RoutedEventArgs e)
        {
            DocsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShowDocsIndex()
        {
            RenderDocsFile(Path.Combine(HelpDocumentation.ResolveDocsDirectory(), "00-index.md"), "Documentation");
        }

        /// <summary>
        /// Renders a shipped documentation markdown file into the built-in Docs viewer.
        /// Relative links are routed back through <see cref="HandleDocsLink"/>.
        /// </summary>
        private void RenderDocsFile(string path, string title)
        {
            DocsTitleText.Text = title;
            if (!File.Exists(path))
            {
                DocsContent.Content = new TextBlock
                {
                    Text = "Documentation file not found: " + path,
                    Foreground = (Brush)FindResource("Dark.Text"),
                    TextWrapping = TextWrapping.Wrap
                };
                return;
            }

            MarkdownRenderer.RelativeLinkNavigator = HandleDocsLink;
            try
            {
                DocsContent.Content = MarkdownRenderer.Render(File.ReadAllText(path));
            }
            finally
            {
                MarkdownRenderer.RelativeLinkNavigator = null;
            }
        }

        /// <summary>
        /// Resolves a relative markdown link against the shipped Docs directory and renders
        /// the target file in the viewer. Returns false when the link cannot be resolved.
        /// </summary>
        private bool HandleDocsLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            // Only relative, local links are supported in the viewer; absolute URLs are
            // already handled by the renderer (http/https/mailto open in the browser).
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

        private void OnPermissionChanged(object sender, RoutedEventArgs e)
        {
            _settings.AllowReadFiles = AllowReadFilesSwitch?.IsChecked ?? true;
            _settings.AllowEditFiles = AllowEditFilesSwitch?.IsChecked ?? true;
            _settings.AllowExecuteCommand = AllowExecuteCommandSwitch?.IsChecked ?? false;
            _settings.AllowWebFetch = AllowWebFetchSwitch?.IsChecked ?? false;
            _settings.AllowMcp = AllowMcpSwitch?.IsChecked ?? false;
            SettingsStore.Save(_settings);
            RebuildAgentTools();
        }

        private AgentPermissions BuildAllowedPermissions()
        {
            var allowed = (AgentPermissions)0;
            if (_settings.AllowReadFiles) allowed |= AgentPermissions.ReadFiles;
            if (_settings.AllowEditFiles) allowed |= AgentPermissions.EditFiles;
            if (_settings.AllowExecuteCommand) allowed |= AgentPermissions.ExecuteCommand;
            if (_settings.AllowWebFetch) allowed |= AgentPermissions.WebFetch;
            if (_settings.AllowMcp) allowed |= AgentPermissions.Mcp;
            if (_settings.AllowSubagents) allowed |= AgentPermissions.Subagents;
            return allowed;
        }

        private static int MemoryBankModeToIndex(string value)
        {
            if (string.Equals(value, "Tool", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(value, "Convention", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0; // Disabled
        }

        private static string MemoryBankIndexToMode(int index)
        {
            if (index == 2) return "Convention";
            if (index == 1) return "Tool";
            return "Disabled";
        }

        private MemoryBankMode ParseMemoryBankMode()
        {
            var value = _settings?.MemoryBankMode;
            if (string.Equals(value, "Tool", StringComparison.OrdinalIgnoreCase)) return MemoryBankMode.Tool;
            if (string.Equals(value, "Convention", StringComparison.OrdinalIgnoreCase)) return MemoryBankMode.Convention;
            return MemoryBankMode.Disabled;
        }

        private AgentToolRegistry BuildToolRegistry(MemoryBankMode memoryBankMode)
        {
            var factory = new AgentToolFactory(
                _ai,
                GetOpenSolutionDirectory,
                () => (Package.GetGlobalService(typeof(SDTE)) as DTE2),
                _buildFeedback,
                AskQuestionAsync,
                () => (SkillsPathBox?.Text ?? "").Trim(),
                memoryBankMode,
                RunSubAgentAsync,
                _settings.AllowSubagents,
                _settings.SubagentMaxDepth);
            return factory.CreateRegistry();
        }

        private void OnAgentToolExecuted(object? sender, AgentToolCall e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnAgentToolExecuted(sender, e)));
                return;
            }
            var detail = string.IsNullOrWhiteSpace(e.DisplayResult) ? e.Result : e.DisplayResult;
            LogService.ChatInfo("Agent tool executed: " + (e.ToolName ?? "") + (e.Blocked ? " (blocked)" : "") + ", result chars=" + (detail == null ? 0 : detail.Length));
            if (string.Equals(e.ToolName, "ask_question", StringComparison.OrdinalIgnoreCase))
                return; // The interactive question message already shows the Q&A.
            if (string.Equals(e.ToolName, "spawn_agent", StringComparison.OrdinalIgnoreCase))
                return; // The sub-agent status card already shows progress and the final report.
            AppendToolCall(e.ToolName, detail, e.Blocked);
        }

        // ------------------------------------------------------------------
        // Chat history (SQLite-backed persistence)
        // ------------------------------------------------------------------

        private void InitializeChatHistory()
        {
            _historySaveTimer.Tick += OnHistorySaveTick;
            this.Unloaded += OnWindowUnloaded;

            try
            {
                ChatHistoryStore.Initialize();
                RefreshHistoryList();
            }
            catch (Exception ex)
            {
                AppendExtensionError("Chat history unavailable: " + ex.Message);
            }

            RestoreUiState();
        }

        /// <summary>Restores the chat view, active conversation, agent mode and active tab from the last session.</summary>
        private void RestoreUiState()
        {
            _restoringUiState = true;
            try
            {
                var state = UiStateStore.Load();

                _mode = state.AgentMode;
                ModeToggle.SelectedValue = _mode == AgentMode.Act ? "Act"
                    : _mode == AgentMode.Help ? "Help"
                    : "Plan";
                ApplyModeBrush();

                SelectTabByHeader(state.ActiveTab);

                if (state.ChatViewMode == ChatViewMode.History)
                {
                    ShowHistoryView();
                }
                else
                {
                    var toOpen = ResolveConversationToOpen(state.LastConversationId);
                    if (toOpen != null)
                        OpenConversation(toOpen.Id, markDirty: false);
                    else
                        StartNewChat(markDirty: false);
                    ShowConversationView();
                }
            }
            finally
            {
                _restoringUiState = false;
            }
        }

        private ChatConversation? ResolveConversationToOpen(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    var stored = ChatHistoryStore.LoadConversation(id);
                    if (stored != null)
                        return stored;
                }
                catch
                {
                    // fall back to the most recent conversation below
                }
            }

            return _conversations.FirstOrDefault();
        }

        private static string MainTabToHeader(MainTab tab)
        {
            switch (tab)
            {
                case MainTab.Settings: return "Settings";
                default: return "Chat";
            }
        }

        private static MainTab HeaderToMainTab(string? header)
        {
            if (string.IsNullOrWhiteSpace(header))
                return MainTab.Chat;

            var value = header.Trim();
            if (string.Equals(value, "Settings", StringComparison.OrdinalIgnoreCase)) return MainTab.Settings;
            return MainTab.Chat;
        }

        private void SelectTabByHeader(MainTab tab)
        {
            var header = MainTabToHeader(tab);

            foreach (var item in MainTabs.Items)
            {
                if (item is TabItem tabItem &&
                    tabItem.Visibility == Visibility.Visible &&
                    string.Equals(tabItem.Tag as string, header, StringComparison.OrdinalIgnoreCase))
                {
                    MainTabs.SelectedItem = tabItem;
                    return;
                }
            }
        }

        private void OnMainTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringUiState)
                return;
            PersistUiState();
        }

        /// <summary>Writes the current chat/tab state so the next session can restore it.</summary>
        private void PersistUiState()
        {
            if (_restoringUiState)
                return;

            UiStateStore.Save(new UiStateData
            {
                ChatViewMode = _chatViewMode,
                LastConversationId = _currentConversation?.Id ?? "",
                AgentMode = _mode,
                ActiveTab = HeaderToMainTab((MainTabs.SelectedItem as TabItem)?.Tag as string)
            });
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            // UI state is already persisted on every explicit change (tab switch, view switch,
            // mode change, conversation open). Persisting again here would re-save whatever
            // transient state the window is in during teardown and can clobber an explicitly
            // chosen "History" view back to "Conversation".
            SaveHistoryNow();
        }

        private void ScheduleHistorySave()
        {
            _historySaveTimer.Stop();
            _historySaveTimer.Start();
        }

        private void OnHistorySaveTick(object? sender, EventArgs e)
        {
            _historySaveTimer.Stop();
            SaveHistoryNow();
        }

        private void SaveHistoryNow()
        {
            if (_currentConversation == null || string.IsNullOrWhiteSpace(_currentConversation.Id))
                return;

            _currentConversation.Messages = _messages.Select(ToHistoryMessage).ToList();
            if (string.IsNullOrEmpty(_currentConversation.Title))
            {
                var firstUser = _currentConversation.Messages.FirstOrDefault(m => m.Kind == ChatMessageKind.User);
                if (firstUser != null)
                    _currentConversation.Title = BuildChatTitle(firstUser.Text);
            }
            _currentConversation.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                ChatHistoryStore.SaveConversation(_currentConversation);
                RefreshHistoryList();
            }
            catch
            {
                // History persistence is best-effort; never block or spam the chat on failure.
            }
        }

        private void StartNewChat(bool markDirty = true)
        {
            _chatCts?.Cancel();
            SaveHistoryNow();

            _loadingHistory = true;
            try
            {
                _messages.Clear();
                _currentConversation = new ChatConversation
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }
            finally
            {
                _loadingHistory = false;
            }

            RefreshPinnedMessage();
            ShowConversationView();
            if (markDirty)
                ScheduleHistorySave();
        }
        private void OpenConversation(string id, bool markDirty = true)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;
            if (id == _currentConversation?.Id)
                return;

            _chatCts?.Cancel();
            SaveHistoryNow();

            ChatConversation? stored;
            try
            {
                stored = ChatHistoryStore.LoadConversation(id);
            }
            catch (Exception ex)
            {
                AppendExtensionError("Failed to open chat: " + ex.Message);
                return;
            }
            if (stored == null)
                return;

            _loadingHistory = true;
            try
            {
                _messages.Clear();
                foreach (var m in stored.Messages)
                {
                    _messages.Add(FromHistoryMessage(m));
                }
                _currentConversation = stored;
                RefreshPinnedMessage();
                _chatAutoScroll = true;
                ChatMessages.ScrollToEnd();
            }
            finally
            {
                _loadingHistory = false;
            }

            if (markDirty)
                ScheduleHistorySave();
            PersistUiState();
        }

        private void RefreshHistoryList()
        {
            try
            {
                _conversations.Clear();
                foreach (var conversation in ChatHistoryStore.ListConversations())
                    _conversations.Add(conversation);
            }
            catch
            {
                // best-effort refresh of the history list
            }
        }

        private void ShowConversationView()
        {
            _chatViewMode = ChatViewMode.Conversation;
            ApplyChatViewMode();
            PersistUiState();
        }

        private void ShowHistoryView()
        {
            _chatViewMode = ChatViewMode.History;
            RefreshHistoryList();
            ApplyChatViewMode();
            PersistUiState();
        }

        private void ApplyChatViewMode()
        {
            var conversation = _chatViewMode == ChatViewMode.Conversation;
            ChatToolbarPanel.Visibility = conversation ? Visibility.Visible : Visibility.Collapsed;
            ChatListContainer.Visibility = conversation ? Visibility.Visible : Visibility.Collapsed;
            ChatSplitter.Visibility = conversation ? Visibility.Visible : Visibility.Collapsed;
            ChatInputPanel.Visibility = conversation ? Visibility.Visible : Visibility.Collapsed;
            PinnedHeaderPanel.Visibility = conversation && _pinnedMessage != null ? Visibility.Visible : Visibility.Collapsed;
            HistoryViewPanel.Visibility = conversation ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnCloseChat(object sender, RoutedEventArgs e)
        {
            SaveHistoryNow();
            ShowHistoryView();
        }

        private void OnShowHistory(object sender, RoutedEventArgs e)
        {
            SaveHistoryNow();
            ShowHistoryView();
        }

        private void OnHistoryCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ChatConversation conversation })
            {
                OpenConversation(conversation.Id);
                ShowConversationView();
            }
        }

        private void OnDeleteHistoryCard(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement { DataContext: ChatConversation conversation }))
                return;

            var result = MessageDialog.Show(
                "Delete chat",
                "Delete this chat?\n\n" + conversation.CardTitle,
                MessageDialogCommandSet.YesNo);
            if (result != MessageDialogCommand.Yes)
                return;

            try
            {
                ChatHistoryStore.DeleteConversation(conversation.Id);
            }
            catch (Exception ex)
            {
                AppendExtensionError("Failed to delete chat: " + ex.Message);
                return;
            }

            if (conversation.Id == _currentConversation?.Id)
                ResetCurrentConversation();

            RefreshHistoryList();
        }

        private void OnClearHistory(object sender, RoutedEventArgs e)
        {
            var result = MessageDialog.Show(
                "Clear history",
                "Delete all chats? This cannot be undone.",
                MessageDialogCommandSet.YesNo);
            if (result != MessageDialogCommand.Yes)
                return;

            try
            {
                ChatHistoryStore.DeleteAllConversations();
            }
            catch (Exception ex)
            {
                AppendExtensionError("Failed to clear chat history: " + ex.Message);
                return;
            }

            ResetCurrentConversation();
            RefreshHistoryList();
        }

        /// <summary>Resets the in-memory conversation to a fresh empty one without persisting it.</summary>
        private void ResetCurrentConversation()
        {
            _historySaveTimer.Stop();

            _loadingHistory = true;
            try
            {
                _messages.Clear();
                _currentConversation = new ChatConversation
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }
            finally
            {
                _loadingHistory = false;
            }

            RefreshPinnedMessage();
            PersistUiState();
        }

        private const double PinnedExpandedHeight = 240.0;

        private void OnPinnedExpandToggle(object sender, RoutedEventArgs e)
        {
            _pinnedExpanded = !_pinnedExpanded;
            UpdatePinnedExpandState();
        }

        private void UpdatePinnedExpandState()
        {
            PinnedSummaryText.Visibility = _pinnedExpanded ? Visibility.Collapsed : Visibility.Visible;
            PinnedTextScroll.Visibility = _pinnedExpanded ? Visibility.Visible : Visibility.Collapsed;
            PinnedTextScroll.MaxHeight = PinnedExpandedHeight;
            PinnedTextScroll.VerticalScrollBarVisibility = _pinnedExpanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            PinnedExpandGlyph.Points = PointCollection.Parse(_pinnedExpanded ? "6,9 12,15 18,9" : "9,18 15,12 9,6");
        }

        private void RefreshPinnedMessage()
        {
            _pinnedMessage = _messages.FirstOrDefault(m => m.Kind == ChatMessageKind.User)
                          ?? _messages.FirstOrDefault();
            PinnedHeaderPanel.DataContext = _pinnedMessage;
            _pinnedExpanded = false;
            UpdatePinnedExpandState();
            if (_chatListView != null)
                _chatListView.Refresh();
            ApplyChatViewMode();
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            StartNewChat();
        }

        private static ChatHistoryMessage ToHistoryMessage(ChatMessage message)
        {
            if (message.Payload is QuestionPayload question)
            {
                return new ChatHistoryMessage
                {
                    Kind = message.Kind,
                    Text = question.Question ?? "",
                    ToolName = question.Answer ?? "",
                    Reasoning = message.Reasoning,
                    Mode = message.Mode
                };
            }

            var tool = message.Payload as ToolCallPayload;
            return new ChatHistoryMessage
            {
                Kind = message.Kind,
                Text = tool != null ? tool.Result : message.Text,
                ToolName = tool?.ToolName ?? "",
                Reasoning = message.Reasoning,
                Mode = message.Mode
            };
        }

        private static ChatMessage FromHistoryMessage(ChatHistoryMessage m)
        {
            if (m.Kind == ChatMessageKind.Question)
            {
                return new ChatMessage
                {
                    Kind = m.Kind,
                    Payload = new QuestionPayload
                    {
                        Question = m.Text ?? "",
                        Answer = m.ToolName ?? "",
                        IsSkipped = string.Equals(m.ToolName, SkipAnswerText, StringComparison.Ordinal)
                    }
                };
            }

            if (m.Kind == ChatMessageKind.ToolCall || m.Kind == ChatMessageKind.ToolBlocked)
            {
                return new ChatMessage
                {
                    Kind = m.Kind,
                    Payload = new ToolCallPayload { ToolName = m.ToolName ?? "", Result = m.Text ?? "", Blocked = m.Kind == ChatMessageKind.ToolBlocked }
                };
            }

            return new ChatMessage { Kind = m.Kind, Text = m.Text, Reasoning = m.Reasoning, Mode = m.Mode };
        }

        private static string BuildChatTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "New chat";
            var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            const int max = 60;
            return singleLine.Length <= max ? singleLine : singleLine.Substring(0, max) + "…";
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            var selected = ModeToggle?.SelectedValue?.ToString();
            _mode = selected == "Act" ? AgentMode.Act
                  : selected == "Help" ? AgentMode.Help
                  : AgentMode.Plan;
            ApplyModeBrush();
            if (_restoringUiState)
                return;
            AppendModeSwitch("Mode: " + _mode);
            PersistUiState();
        }

        private void ApplyModeBrush()
        {
            if (InputBox == null)
                return;
            var key = _mode == AgentMode.Act ? "Mode.ActBrush"
                    : _mode == AgentMode.Help ? "Mode.HelpBrush"
                    : "Mode.PlanBrush";
            InputBox.BorderBrush = (Brush)FindResource(key);
        }

        private void OnRefreshPromptsFromDisk(object sender, RoutedEventArgs e)
        {
            if (_ai is IPromptReloadable reloadable)
            {
                reloadable.ReloadPromptsFromDisk();
                AppendChat("Prompts reloaded from disk: " + PromptPaths.ResolvePromptsDirectory());
            }
            else
            {
                AppendChat("Prompts reload is unavailable for the current provider (stub/offline).");
            }
        }

        private const double SettingsToastMaxHeight = 160.0;

        private void ShowSettingsToast(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowSettingsToast(text)));
                return;
            }
            SettingsToast.Kind = NotificationKind.Success;
            SettingsToast.Message = text ?? "";
            SettingsToast.Visibility = Visibility.Visible;
            _settingsToastTimer.Stop();
            _settingsToastTimer.Start();
            SlideSettingsToast(0, SettingsToastMaxHeight);
        }

        private void HideSettingsToast()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(HideSettingsToast));
                return;
            }
            _settingsToastTimer.Stop();
            SlideSettingsToast(SettingsToast.ActualHeight, 0,
                () => SettingsToast.Visibility = Visibility.Collapsed);
        }

        private void OnSettingsToastTick(object? sender, EventArgs e)
        {
            HideSettingsToast();
        }

        private void OnSettingsToastClose(object sender, RoutedEventArgs e)
        {
            HideSettingsToast();
        }

        private void SlideSettingsToast(double from, double to, Action? onCompleted = null)
        {
            SettingsToast.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            var animation = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            if (onCompleted != null)
                animation.Completed += (_, __) => onCompleted();
            SettingsToast.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
        }

        private void ShowVoiceError(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowVoiceError(text)));
                return;
            }

            VoiceErrorToast.Kind = NotificationKind.Error;
            VoiceErrorToast.Message = text ?? "";
            VoiceErrorToast.Visibility = Visibility.Visible;
            // No auto-hide: the error banner stays until the user closes it.
        }

        private void OnVoiceErrorClose(object sender, RoutedEventArgs e)
        {
            VoiceErrorToast.Visibility = Visibility.Collapsed;
        }

        private void OnSettingsNavChecked(object sender, RoutedEventArgs e)
        {
            // The first radio button is checked in XAML before SettingsSubTabs exists;
            // ignore that initial pass.
            if (SettingsSubTabs == null) return;
            if (sender is RadioButton radioButton && int.TryParse(radioButton.Tag?.ToString(), out int index))
            {
                SettingsSubTabs.SelectedIndex = index;
            }
        }

        // ---- Theme ----

        private void PopulateThemeSwatches()
        {
            _themeSwatches.Clear();
            foreach (var swatch in new SwatchesProvider().Swatches)
            {
                _themeSwatches.Add(new ThemeSwatchItem
                {
                    Name = swatch.Name,
                    Primary = swatch.ExemplarHue.Color,
                    Secondary = swatch.SecondaryExemplarHue?.Color,
                    DisplayBrush = new SolidColorBrush(swatch.ExemplarHue.Color)
                });
            }
            ThemeSwatchesList.ItemsSource = _themeSwatches;
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

        private void ApplyDynamicFromSeed()
        {
            bool isDark = ThemeService.IsDark(_settings.BaseTheme);
            string seedHex = ThemeColorMath.HueToSeedHex(_settings.SeedHue);
            var (primary, secondary) = _themeService.ResolveDynamicScheme(
                ThemeService.ColorFromHex(seedHex),
                ThemeService.ParseVariant(_settings.Variant),
                isDark,
                _settings.Contrast);
            _settings.PrimaryHex = ThemeService.ToHex(primary);
            _settings.SecondaryHex = ThemeService.ToHex(secondary);
            _themeService.ApplyResolvedTheme(isDark, primary, secondary);
            UpdateSeedPreview();
        }

        private void OnThemeModeChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressThemeEvents) return;
            bool isDark = DarkThemeToggle.IsChecked == true;
            _settings.BaseTheme = isDark ? "Dark" : "Light";
            _themeService.ApplyChrome(isDark);
            _themeService.ApplyBaseTheme(isDark);
        }

        private void OnSeedHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateSeedHueLabel();
            if (_suppressThemeEvents) return;
            _settings.SeedHue = SeedHueSlider.Value;
            ApplyDynamicFromSeed();
        }

        private void OnThemeVariantChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressThemeEvents) return;
            _settings.Variant = IndexToVariant(ThemeVariantCombo.SelectedIndex);
            ApplyDynamicFromSeed();
        }

        private void OnThemeContrastChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateContrastLabel();
            if (_suppressThemeEvents) return;
            _settings.Contrast = Math.Round(ThemeContrastSlider.Value, 2);
            ApplyDynamicFromSeed();
        }

        private void OnThemeSwatchClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.DataContext is ThemeSwatchItem item)) return;

            _settings.PrimaryHex = ThemeService.ToHex(item.Primary);
            _themeService.ApplyPrimary(item.Primary);

            if (item.Secondary is Color secondary)
            {
                _settings.SecondaryHex = ThemeService.ToHex(secondary);
                _themeService.ApplySecondary(secondary);
            }
        }

        private void UpdateSeedHueLabel()
        {
            if (SeedHueValueText != null)
                SeedHueValueText.Text = Math.Round(SeedHueSlider.Value).ToString(CultureInfo.InvariantCulture);
        }

        private void UpdateContrastLabel()
        {
            if (ThemeContrastValueText != null)
                ThemeContrastValueText.Text = Math.Round(ThemeContrastSlider.Value, 2).ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void UpdateSeedPreview()
        {
            if (SeedPreview == null) return;
            string seedHex = ThemeColorMath.HueToSeedHex(SeedHueSlider.Value);
            SeedPreview.Background = new SolidColorBrush(ThemeService.ColorFromHex(seedHex));
        }

        private static int VariantToIndex(string variant)
        {
            switch (variant)
            {
                case "Monochrome": return 0;
                case "Neutral": return 1;
                case "TonalSpot": return 2;
                case "Vibrant": return 3;
                case "Expressive": return 4;
                case "Fidelity": return 5;
                case "Content": return 6;
                case "Rainbow": return 7;
                case "FruitSalad": return 8;
                default: return 1;
            }
        }

        private static string IndexToVariant(int index)
        {
            switch (index)
            {
                case 0: return "Monochrome";
                case 1: return "Neutral";
                case 2: return "TonalSpot";
                case 3: return "Vibrant";
                case 4: return "Expressive";
                case 5: return "Fidelity";
                case 6: return "Content";
                case 7: return "Rainbow";
                case 8: return "FruitSalad";
                default: return "Neutral";
            }
        }

        private void OnSaveSettings(object sender, RoutedEventArgs e)
        {
            _settings.BaseUrl = BaseUrlBox.Text.Trim();
            _settings.ApiKey = ApiKeyBox.Password.Trim();
            _settings.Model = ModelBox.Text.Trim();
            _settings.Temperature = Math.Round(TemperatureSlider.Value, 1);
            _settings.SkillsPath = SkillsPathBox.Text.Trim();
            _settings.ContextWindowSize = ContextWindowUpDown != null && ContextWindowUpDown.Value > 0 ? ContextWindowUpDown.Value : 409600;
            _settings.CompactThreshold = CompactThresholdUpDown?.Value ?? 0.85;
            _settings.ReserveFraction = ReserveFractionUpDown?.Value ?? 0.12;
            _settings.KeepRecentMessages = KeepRecentUpDown?.Value ?? 6;
            _settings.ToolResultMaxChars = ToolResultMaxCharsUpDown?.Value ?? 12000;
            _settings.MaxIterations = MaxIterationsUpDown?.Value ?? 8;
            _settings.AutoCompact = AutoCompactSwitch?.IsChecked ?? true;
            _settings.UseToolRole = UseToolRoleSwitch?.IsChecked ?? true;
            _settings.ToolCallingMode = (ToolCallingModeCombo?.SelectedIndex ?? 0) == 1 ? "Native" : "JsonChat";
            _settings.MemoryBankMode = MemoryBankIndexToMode(MemoryBankModeCombo?.SelectedIndex ?? 0);
            _settings.LogFilePath = LogFilePathBox.Text.Trim();
            _settings.ChatLogFilePath = ChatLogFilePathBox.Text.Trim();
            _settings.TimeoutSeconds = TimeoutSecondsUpDown?.Value ?? 300;
            _settings.RetryTimeoutMinutes = RetryTimeoutMinutesUpDown?.Value ?? 30;
            _settings.RetryBaseDelaySeconds = RetryBaseDelaySecondsUpDown?.Value ?? 1;
            _settings.RetryMaxDelaySeconds = RetryMaxDelaySecondsUpDown?.Value ?? 30;
            _settings.InputPricePerMillion = InputPriceUpDown?.Value ?? 0.27;
            _settings.CacheHitPricePerMillion = CacheHitPriceUpDown?.Value ?? 0.07;
            _settings.OutputPricePerMillion = OutputPriceUpDown?.Value ?? 1.10;
            _settings.UserName = UserNameBox.Text.Trim();
            _settings.AddressByName = AddressByNameSwitch?.IsChecked ?? false;
            _settings.AllowSubagents = AllowSubagentsSwitch?.IsChecked ?? false;
            _settings.SubagentMaxIterations = SubagentMaxIterationsUpDown?.Value ?? 4;
            _settings.SubagentMaxDepth = SubagentMaxDepthUpDown?.Value ?? 2;
            _settings.SpeechToTextProvider = SpeechToTextProviderIndexToName(SpeechToTextProviderCombo?.SelectedIndex ?? 0);
            _settings.SpeechToTextModel = (SpeechToTextModelBox?.Text ?? "").Trim();
            _settings.SpeechToTextLanguage = (SpeechToTextLanguageBox?.Text ?? "").Trim();
            _settings.VoskModelPath = (VoskModelPathBox?.Text ?? "").Trim();
            _settings.SpeechAutoSend = SpeechAutoSendSwitch?.IsChecked ?? false;
            _settings.ProxyEnabled = ProxyEnabledSwitch?.IsChecked ?? false;
            _settings.ProxyHost = (ProxyHostBox?.Text ?? "").Trim();
            _settings.ProxyPort = ProxyPortUpDown != null && ProxyPortUpDown.Value > 0 && ProxyPortUpDown.Value <= 65535 ? ProxyPortUpDown.Value : 8080;
            _settings.ProxyUsername = (ProxyUsernameBox?.Text ?? "").Trim();
            _settings.ProxyPassword = ProxyPasswordBox?.Password ?? "";
            _settings.BaseTheme = (DarkThemeToggle?.IsChecked == true) ? "Dark" : "Light";
            _settings.SeedHue = SeedHueSlider?.Value ?? 256;
            _settings.Variant = IndexToVariant(ThemeVariantCombo?.SelectedIndex ?? 1);
            _settings.Contrast = Math.Round(ThemeContrastSlider?.Value ?? 0.5, 2);
            SettingsStore.Save(_settings);
            LogService.Configure(_settings.LogFilePath, _settings.ChatLogFilePath);
            RecreateAiProvider();
            _stt = SpeechToTextFactory.Create(_settings);
            RebuildAgentTools();
            ShowSettingsToast("Settings saved. Provider: " + (_ai is StubAiProvider ? "stub (offline)" : "OpenAI-compatible"));
        }


















        private void ShowPendingOptionsWarnings()
        {
            var warnings = VsOptionsService.LastWarnings;
            if (warnings == null || warnings.Count == 0)
                return;

            ShowOptionsWarning(string.Join(Environment.NewLine, warnings));
        }

        private void ShowOptionsWarning(string message)
        {
            if (OptionsWarningToast == null)
                return;

            OptionsWarningToast.Kind = NotificationKind.Warning;
            OptionsWarningToast.Message = message ?? string.Empty;
            OptionsWarningToast.Visibility = Visibility.Visible;
        }

        private void HideOptionsWarning()
        {
            if (OptionsWarningToast != null)
                OptionsWarningToast.Visibility = Visibility.Collapsed;
        }

        private void OnOptionsWarningClose(object sender, RoutedEventArgs e)
        {
            HideOptionsWarning();
        }


        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            OpenLogFile(LogService.CurrentLogFilePath, "Log file path is not available.", "log");
        }

        private void OnOpenChatLog(object sender, RoutedEventArgs e)
        {
            OpenLogFile(LogService.CurrentChatLogFilePath, "Chat log file path is not available.", "chat log");
        }

        private void OpenLogFile(string logFile, string unavailableMessage, string logKind)
        {
            if (string.IsNullOrWhiteSpace(logFile))
            {
                AppendExtensionError(unavailableMessage);
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                if (!File.Exists(logFile))
                    File.WriteAllText(logFile, "");

                var editor = FindNotepadPlusPlus();
                if (!string.IsNullOrWhiteSpace(editor))
                    System.Diagnostics.Process.Start(new ProcessStartInfo(editor, "\"" + logFile + "\"") { UseShellExecute = true });
                else
                    System.Diagnostics.Process.Start(new ProcessStartInfo("notepad.exe", "\"" + logFile + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendExtensionError("Failed to open " + logKind + ": " + ex.Message);
                LogService.Error(ex, "Failed to open " + logKind + " file.");
            }
        }

        private static string FindNotepadPlusPlus()
        {
            var candidates = new List<string>
            {
                ReadAppPath("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\notepad++.exe"),
                ReadAppPath("HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\notepad++.exe")
            };

            var prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (!string.IsNullOrWhiteSpace(prog)) candidates.Add(Path.Combine(prog, "Notepad++", "notepad++.exe"));
            if (!string.IsNullOrWhiteSpace(progX86)) candidates.Add(Path.Combine(progX86, "Notepad++", "notepad++.exe"));
            if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "Notepad++", "notepad++.exe"));
            if (!string.IsNullOrWhiteSpace(roaming)) candidates.Add(Path.Combine(roaming, "Notepad++", "notepad++.exe"));

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static string ReadAppPath(string registryKeyPath)
        {
            try
            {
                return Registry.GetValue(registryKeyPath, "", null) as string;
            }
            catch
            {
                return null;
            }
        }


    }
}
