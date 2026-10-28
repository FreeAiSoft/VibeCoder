using System.Windows;

namespace VibeCoder.Controls.Messages
{
    /// <summary>
    /// Bubbling routed event raised by MessageChrome when its copy button is clicked.
    /// ChatMessagesControl forwards it to its host-facing CopyMessageRequested event.
    /// </summary>
    public static class MessageEvents
    {
        public static readonly RoutedEvent CopyRequestedEvent =
            EventManager.RegisterRoutedEvent(nameof(CopyRequestedEvent), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MessageChrome));
    }

    public sealed class ChatMessageRoutedEventArgs : RoutedEventArgs
    {
        public ChatMessageRoutedEventArgs(RoutedEvent routedEvent, ChatMessage message) : base(routedEvent) { Message = message; }
        public ChatMessage Message { get; }
    }
}