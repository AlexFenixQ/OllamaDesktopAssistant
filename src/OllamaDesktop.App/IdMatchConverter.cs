using System.Globalization;
using System.Windows.Data;
namespace OllamaDesktop.App;
public sealed class IdMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length==2 && values[0] is string a && values[1] is string b && a==b;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
