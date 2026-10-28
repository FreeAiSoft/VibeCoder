using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VibeCoder.Controls.Messages;

namespace VibeCoder.Controls
{
    /// <summary>
    /// Reusable scrolling chat list: a ScrollViewer hosting an ItemsControl of ChatMessage
    /// entries. Each kind is rendered by its own DataTemplate (selected by
    /// ChatMessageTemplateSelector); the shared chrome lives in MessageChrome. Interactions
    /// (copy, question answer/skip, checkpoint compare/restore) are surfaced as events.
    /// </summary>
    public partial class ChatMessagesControl : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(ChatMessagesControl),
            new PropertyMetadata(null));

        public ChatMessagesControl()
        {
            InitializeComponent();
            MessageScroll.ScrollChanged += OnMessageScrollChanged;
            AddHandler(MessageEvents.CopyRequestedEvent, new RoutedEventHandler(OnCopyRequestedRouted));
        }

        /// <summary>Collection of messages rendered by the inner ItemsControl.</summary>
        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        /// <summary>Raised when the inner scroll viewer scrolls; arguments carry offset/extent/viewport.</summary>
        public event ScrollChangedEventHandler? ScrollChanged;

        /// <summary>Raised when the user clicks the copy button of a message.</summary>
        public event Action<ChatMessage>? CopyMessageRequested;

        /// <summary>Raised when the user picks an answer option of a question message.</summary>
        public event Action<string>? QuestionOptionSelected;

        /// <summary>Raised when the user skips a question message.</summary>
        public event Action? QuestionSkipped;

        /// <summary>Raised when the user clicks the Compare button of a checkpoint indicator.</summary>
        public event Action<CheckpointPayload>? CheckpointCompareRequested;

        /// <summary>Raised when the user clicks the Restore button of a checkpoint indicator.</summary>
        public event Action<CheckpointPayload>? CheckpointRestoreRequested;

        public void ScrollToEnd()
        {
            MessageScroll.ScrollToEnd();
        }

        private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            ScrollChanged?.Invoke(this, e);
        }

        private void OnCopyRequestedRouted(object sender, RoutedEventArgs e)
        {
            if (e is ChatMessageRoutedEventArgs args)
                CopyMessageRequested?.Invoke(args.Message);
        }

        private void OnQuestionOptionClick(object sender, RoutedEventArgs e)
        {
            var option = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(option))
                return;
            QuestionOptionSelected?.Invoke(option);
        }

        private void OnQuestionSkip(object sender, RoutedEventArgs e)
        {
            QuestionSkipped?.Invoke();
        }

        private void OnCheckpointCompare(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ChatMessage { Payload: CheckpointPayload payload } })
                CheckpointCompareRequested?.Invoke(payload);
        }

        private void OnCheckpointRestore(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ChatMessage { Payload: CheckpointPayload payload } })
                CheckpointRestoreRequested?.Invoke(payload);
        }
    }
}