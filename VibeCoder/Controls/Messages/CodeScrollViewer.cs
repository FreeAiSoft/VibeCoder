using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VibeCoder.Controls.Messages
{
    /// <summary>
    /// Horizontal-only code ScrollViewer that forwards the mouse wheel to its parent so
    /// hovering over a code block still scrolls the chat list vertically.
    /// </summary>
    public class CodeScrollViewer : ScrollViewer
    {
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (e.Handled)
                return;
            e.Handled = true;

            var parent = Parent as UIElement;
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