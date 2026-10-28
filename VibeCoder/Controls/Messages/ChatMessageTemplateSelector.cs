using System.Windows;
using System.Windows.Controls;
using VibeCoder.Core.Models;
using VibeCoder.Core.Services;

namespace VibeCoder.Controls.Messages
{
    /// <summary>
    /// Selects the per-kind message control template by looking up a DataTemplate whose key
    /// is the control id (from ChatMessageControlId) plus "Template".
    /// </summary>
    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessage message && container is FrameworkElement element)
            {
                var key = ChatMessageControlId.ForKind(message.Kind) + "Template";
                if (element.TryFindResource(key) is DataTemplate template)
                    return template;
            }
            return base.SelectTemplate(item, container);
        }
    }
}