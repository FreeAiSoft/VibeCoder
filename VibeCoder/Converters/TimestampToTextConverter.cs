using System;
using System.Globalization;
using System.Windows.Data;
using VibeCoder.Core.Services;

namespace VibeCoder.Converters
{
    /// <summary>
    /// Converts a UTC DateTime to its local "HH:mm:ss" text using ChatTimeFormatter.
    /// </summary>
    public class TimestampToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is DateTime dt ? ChatTimeFormatter.FormatLocalTime(dt) : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}