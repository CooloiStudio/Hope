using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hope.Desktop.Converters;

/// <summary>布尔值与 Visibility 之间的转换器；可设置 Inverted 反转逻辑。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Inverted) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
