using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ParallelSystemsPlugin.Models.Configs;

namespace ParallelSystemsPlugin.Converters
{
    public class RevitColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var color = value as RgbColor;
            if (color != null)
                return new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));

            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
