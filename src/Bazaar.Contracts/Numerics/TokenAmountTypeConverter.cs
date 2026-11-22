using System.ComponentModel;
using System.Globalization;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// Converts <see cref="TokenAmount"/> from and to its text form, so configuration binding can read an amount from a
/// setting. Uses <see cref="TokenAmountBounds.PostgresNumeric"/>. Invalid text throws <see cref="FormatException"/>.
/// </summary>
public sealed class TokenAmountTypeConverter : TypeConverter
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc />
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

    /// <inheritdoc />
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string text ? TokenAmount.Parse(text) : base.ConvertFrom(context, culture, value);

    /// <inheritdoc />
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string) && value is TokenAmount amount
            ? amount.ToString()
            : base.ConvertTo(context, culture, value, destinationType);
}
