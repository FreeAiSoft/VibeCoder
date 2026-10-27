using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VibeCoder.Controls
{
    /// <summary>
    /// Reusable scrolling chat list: a <see cref="ScrollViewer"/> with a 5px scrollbar
    /// hosting an <see cref="ItemsControl"/> of <see cref="ChatMessage"/> entries.
    /// Message interactions (copy, question answer/skip) are surfaced as events so the
    /// host can keep its own business logic.
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
        }

        /// <summary>Collection of messages rendered by the inner <see cref="ItemsControl"/>.</summary>
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

        private void OnCopyMessage(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ChatMessage message })
                CopyMessageRequested?.Invoke(message);
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

        /// <summary>
        /// Forwards the mouse wheel from the code block's horizontal-only ScrollViewer to its
        /// parent, so hovering over a code block still scrolls the chat list vertically.
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
    }
}
