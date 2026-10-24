using System.Globalization;
using System.Windows.Data;

namespace DiskInspector.App.Views;

public sealed class Not : IValueConverter
{
    public static Not Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
