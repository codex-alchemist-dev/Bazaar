namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// Where a sealed value lives (doc 63 section 9). The sealer binds all four parts into the authenticated data, so a value
/// cannot be moved to another row, column or table, or opened under another scope.
/// </summary>
/// <param name="Table">Table name. No <c>|</c>, no control characters, 1 to 128 characters.</param>
/// <param name="Column">Plain column name (<c>body</c>, not <c>body_ct</c>). Same rules as <paramref name="Table"/>.</param>
/// <param name="RowKey">
/// The row's stable key as text (a UUID, or a composite such as <c>gc:5:12</c>). Any non-empty text is allowed, including
/// <c>|</c>, because the sealer length-prefixes it. It must be a value that never changes for the life of the row.
/// </param>
/// <param name="Scope">
/// Which DEK family seals the value: a per-scope key (<c>gc</c>, <c>install</c>, a soul id). Same rules as <paramref name="Table"/>.
/// </param>
public readonly record struct ColumnSealContext(string Table, string Column, string RowKey, string Scope);
