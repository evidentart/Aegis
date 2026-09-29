using Microsoft.UI.Xaml.Data;

namespace Aegis;

public sealed class LocalDateTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTimeOffset timestamp
            ? PresentationFormatting.FormatLocalDateTime(timestamp)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
