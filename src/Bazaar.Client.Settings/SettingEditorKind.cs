namespace Bazaar.Client.Settings;

/// <summary>Which editor a setting row gets, decided by the CLR type of the registry key.</summary>
public enum SettingEditorKind
{
    /// <summary>A true or false switch.</summary>
    Toggle,

    /// <summary>A pick from the enum's names (wire form: snake_case).</summary>
    Choice,

    /// <summary>A number; the registry's validator supplies any range, and its refusal text is shown.</summary>
    Number,

    /// <summary>Free text.</summary>
    Text,

    /// <summary>A value with no editor (a list): shown, never edited here.</summary>
    ReadOnlyValue,
}
