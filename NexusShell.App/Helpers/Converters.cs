using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NexusShell.App.Helpers
{
    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class EqualValueToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            return value.ToString() == parameter.ToString() ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int code)
            {
                if (code >= 200 && code < 300) return Brushes.LightGreen;
                if (code >= 400) return Brushes.Salmon;
                if (code >= 300) return Brushes.Yellow;
            }
            return Brushes.LightGray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class EnumToCollectionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return null!;
            return Enum.GetValues(value.GetType());
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Converts Dictionary&lt;string,string&gt; to a "Key: Value" multiline string for display.</summary>
    public class DictionaryToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Dictionary<string, string> dict)
                return string.Join("\n", dict.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            return string.Empty;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts bool → Visibility.
    /// Pass ConverterParameter="invert" to reverse the mapping.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
            bool flag = value is bool b && b;
            return (flag ^ invert) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Maps an HTTP method name (string or HttpMethodType) to its conventional color brush.</summary>
    public class MethodColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() switch
            {
                "GET"     => new SolidColorBrush(Color.FromRgb(97,  175, 239)),
                "POST"    => new SolidColorBrush(Color.FromRgb(73,  204, 144)),
                "PUT"     => new SolidColorBrush(Color.FromRgb(252, 161, 48)),
                "PATCH"   => new SolidColorBrush(Color.FromRgb(80,  227, 194)),
                "DELETE"  => new SolidColorBrush(Color.FromRgb(249, 62,  62)),
                "HEAD"    => new SolidColorBrush(Color.FromRgb(144, 164, 174)),
                "OPTIONS" => new SolidColorBrush(Color.FromRgb(144, 164, 174)),
                _         => new SolidColorBrush(Color.FromRgb(144, 164, 174))
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the bound value is a
    /// non-null, non-empty string; <see cref="Visibility.Collapsed"/> otherwise.
    /// Used by <c>InteractiveScreenView</c> to show optional badges/descriptions.
    /// </summary>
    public class NonEmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is string s && !string.IsNullOrWhiteSpace(s))
               ? Visibility.Visible
               : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}

