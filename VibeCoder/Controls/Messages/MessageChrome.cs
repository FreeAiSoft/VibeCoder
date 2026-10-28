using System;
using System.Windows;
using System.Windows.Controls;

namespace VibeCoder.Controls.Messages
{
    /// <summary>
    /// Shared chrome around every chat message: colored border, reasoning expander, the
    /// message body (Content), and a footer with the timestamp, an expand/collapse chevron
    /// and a copy button. Copy / chevron / timestamp are implemented here once.
    /// </summary>
    public class MessageChrome : ContentControl
    {
        public static readonly DependencyProperty IsExpandableProperty =
            DependencyProperty.Register(nameof(IsExpandable), typeof(bool), typeof(MessageChrome), new PropertyMetadata(false));

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(MessageChrome), new PropertyMetadata(false));

        public MessageChrome()
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/VibeCoder;component/Controls/Messages/ChatMessageResources.xaml", UriKind.Relative)
            });
            Style = (Style)FindResource(typeof(MessageChrome));
        }

        public bool IsExpandable
        {
            get => (bool)GetValue(IsExpandableProperty);
            set => SetValue(IsExpandableProperty, value);
        }

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        public ChatMessage Message => DataContext as ChatMessage;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (GetTemplateChild("CopyButton") is Button copy)
            {
                copy.Click -= OnCopyClick;
                copy.Click += OnCopyClick;
            }

            if (GetTemplateChild("ChevronButton") is Button chevron)
            {
                chevron.Click -= OnChevronClick;
                chevron.Click += OnChevronClick;
            }
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new ChatMessageRoutedEventArgs(MessageEvents.CopyRequestedEvent, Message));
        }

        private void OnChevronClick(object sender, RoutedEventArgs e)
        {
            IsExpanded = !IsExpanded;
        }
    }
}