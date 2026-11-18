namespace Bazaar.Diagnostics.Redaction;

/// <summary>
/// Redacts one field of a capture. <see cref="Redactor"/> is the implementation; the seam lets the capture code
/// be tested when redaction fails.
/// </summary>
public interface IFieldRedactor
{
    /// <summary>Redacts one field. The result text is safe to store whatever the status.</summary>
    RedactionResult Redact(RedactionClass fieldClass, string? input);
}
