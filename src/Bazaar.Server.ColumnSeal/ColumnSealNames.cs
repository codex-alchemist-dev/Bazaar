using System.Globalization;
using System.Text;

namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// Validation and the authenticated-data encoding (doc 63 section 9). The associated data is
/// <c>bazaar.colseal.v1|&lt;table&gt;|&lt;column&gt;|&lt;row key&gt;|&lt;key id&gt;</c> and is built so two different inputs never give the same bytes:
/// table, column and scope reject <c>|</c> and control characters, so they cannot contain a separator; the row key may contain
/// anything, so it is written as <c>&lt;UTF-8 byte length&gt;:&lt;row key&gt;</c>; the key id is a fixed-shape GUID (<c>D</c> format).
/// Reading left to right there is exactly one way to split the result.
/// </summary>
internal static class ColumnSealNames
{
    public const string AadLabel = "bazaar.colseal.v1";
    public const string DekAadLabel = "bazaar.colseal.dek.v1";
    public const string BlindInfoLabel = "bazaar.colseal.v1.blind";

    private const int _maxNameLength = 128;
    private const int _maxRowKeyLength = 512;

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void ValidateName(string? value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.Length > _maxNameLength)
            throw Invalid(what);

        foreach (var c in value)
        {
            if (c == '|' || char.IsControl(c) || IsInvisibleSeparator(c))
                throw Invalid(what);
        }

        // Lone surrogates cannot be encoded; reject them here rather than at the encoder.
        try
        {
            _ = _strictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid(what);
        }
    }

    public static void ValidateContext(in ColumnSealContext context)
    {
        ValidateName(context.Table, "table name");
        ValidateName(context.Column, "column name");
        ValidateName(context.Scope, "scope");
        if (string.IsNullOrEmpty(context.RowKey) || context.RowKey.Length > _maxRowKeyLength)
            throw Invalid("row key");
        try
        {
            _ = _strictUtf8.GetByteCount(context.RowKey);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid("row key");
        }
    }

    /// <summary>The associated data for one sealed value. The context must already be valid.</summary>
    public static byte[] BuildAad(in ColumnSealContext context, Guid keyId)
    {
        var rowKeyBytes = _strictUtf8.GetByteCount(context.RowKey);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{AadLabel}|{context.Table}|{context.Column}|{rowKeyBytes}:{context.RowKey}|{keyId:D}");
        return _strictUtf8.GetBytes(text);
    }

    /// <summary>The associated data that binds a wrapped DEK to its scope and id.</summary>
    public static byte[] BuildDekAad(string scope, Guid dekId)
    {
        ValidateName(scope, "scope");
        return _strictUtf8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{DekAadLabel}|{scope}|{dekId:D}"));
    }

    /// <summary>The HKDF info for the blind index key of one scope and column.</summary>
    public static byte[] BuildBlindInfo(string scope, string table, string column)
    {
        ValidateName(scope, "scope");
        ValidateName(table, "table name");
        ValidateName(column, "column name");
        return _strictUtf8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{BlindInfoLabel}|{scope}|{table}|{column}"));
    }

    public static byte[] Utf8(string value) => _strictUtf8.GetBytes(value);

    public static ColumnSealException Invalid(string what) =>
        new(ColumnSealFailure.InvalidInput, $"The {what} is not valid.");

    private static bool IsInvisibleSeparator(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format;
}
