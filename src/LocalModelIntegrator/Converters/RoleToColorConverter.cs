using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LocalModelIntegrator.Converters
{
    public class RoleToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var role = value?.ToString();

            Color color = role switch
            {
                "user" => Color.FromRgb(0, 0x7A, 0xCC),
                "assistant" => Color.FromRgb(0x68, 0x21, 0x7A),
                "system" => Color.FromRgb(0x33, 0x99, 0x33),
                "error" => Color.FromRgb(0xFF, 0x00, 0x00),
                _ => Color.FromRgb(0x55, 0x55, 0x55)
            };

            return new SolidColorBrush(color);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
