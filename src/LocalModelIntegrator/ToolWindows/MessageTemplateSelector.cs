using System.Windows;
using System.Windows.Controls;
using LocalModelIntegrator.Models;

namespace LocalModelIntegrator.ToolWindows
{
    /// <summary>
    /// Chooses the message template by role: reasoning ("thinking") messages render in a
    /// collapsible expander; everything else uses the normal bubble.
    /// </summary>
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Normal { get; set; }
        public DataTemplate Thinking { get; set; }
        public DataTemplate Tool { get; set; }
        public DataTemplate Notice { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is MessageDisplay message)
            {
                if (message.Role == "thinking")
                    return Thinking ?? Normal;
                if (message.Role == "tool")
                    return Tool ?? Normal;
                if (message.Role == "notice")
                    return Notice ?? Normal;
            }
            return Normal;
        }
    }
}
