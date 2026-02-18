using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace BooruManager.Converters;

public class BoolToDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            if (parameter is string paramStr && paramStr.Contains(':'))
            {
                var parts = paramStr.Split(':');
                if (double.TryParse(parts[0], out var trueValue) && double.TryParse(parts[1], out var falseValue))
                {
                    return boolValue ? trueValue : falseValue;
                }
            }
            return boolValue ? 1.0 : 0.0;
        }
        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
