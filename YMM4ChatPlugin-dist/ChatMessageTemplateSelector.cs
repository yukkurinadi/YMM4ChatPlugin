using System.Windows;
using System.Windows.Controls;

namespace YMM4ChatPlugin
{
    public class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? NormalTemplate { get; set; }
        public DataTemplate? SystemTemplate { get; set; }
        public DataTemplate? JoinLeaveTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessage msg)
            {
                return msg.Type switch
                {
                    ChatMessageType.Normal => NormalTemplate,
                    ChatMessageType.System => SystemTemplate,
                    ChatMessageType.JoinLeave => JoinLeaveTemplate,
                    _ => NormalTemplate
                };
            }
            return NormalTemplate;
        }
    }
}