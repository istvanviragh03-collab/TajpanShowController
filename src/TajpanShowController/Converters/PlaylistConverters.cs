using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TajpanShowController;

public sealed class PlayingIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 3 && ReferenceEquals(values[0], values[1]) && values[2] is true ? Visibility.Visible : Visibility.Hidden;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class PlaylistSequenceConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not System.Collections.IList list) return "--";
        var index = list.IndexOf(values[0]); return index < 0 ? "--" : (index + 1).ToString("00", culture);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
