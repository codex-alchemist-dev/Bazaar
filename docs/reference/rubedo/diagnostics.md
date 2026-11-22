# Diagnostic codes

Codes are stable. The golden corpus asserts them, and tools key on them.

## Expression codes

| Code | Meaning |
|---|---|
| <a id="code-arity"></a>`arity` | A function was called with the wrong number of arguments. |
| <a id="code-bad-character"></a>`bad-character` | The source contains a character the language does not use. |
| <a id="code-bad-number"></a>`bad-number` | A number literal is malformed. |
| <a id="code-bad-range"></a>`bad-range` | A function was given arguments that are out of order, such as `clamp` with `lo` above `hi`. Raised at evaluation. |
| <a id="code-bad-string"></a>`bad-string` | A string literal contains a character it may not (a string has no escape sequences). |
| <a id="code-budget"></a>`budget` | Evaluation ran out of its step budget. Raised at evaluation. |
| <a id="code-chained-comparison"></a>`chained-comparison` | Relational or equality operators were chained, as in `a < b < c`. Add parentheses to combine them. |
| <a id="code-context-type"></a>`context-type` | A value supplied in the context does not have the type the schema declares for that identifier. Raised at evaluation. |
| <a id="code-division-by-zero"></a>`division-by-zero` | A division or modulo had a zero divisor. Raised at evaluation. |
| <a id="code-internal"></a>`internal` | The engine met a syntax node or operator it does not know. It indicates a bug in the engine, not in the source. |
| <a id="code-missing-value"></a>`missing-value` | The context has no value for an identifier the expression needed. Raised at evaluation; a missing value is never defaulted. |
| <a id="code-number-out-of-range"></a>`number-out-of-range` | A number left the range of plus or minus 10^15. Raised at evaluation. |
| <a id="code-result-type"></a>`result-type` | The expression compiled, but its result type is not the one the caller requires, such as a visibility rule that is not `bool`. |
| <a id="code-string-too-long"></a>`string-too-long` | A string literal is longer than the string limit. |
| <a id="code-syntax"></a>`syntax` | The source does not follow the grammar. |
| <a id="code-too-deep"></a>`too-deep` | Parentheses, unary operators or call arguments nest deeper than the depth limit. |
| <a id="code-too-long"></a>`too-long` | The source is longer than the source-length limit. |
| <a id="code-too-many-tokens"></a>`too-many-tokens` | The source has more tokens than the token limit. |
| <a id="code-type"></a>`type` | An operator or function was given operands or arguments of the wrong type. There are no implicit conversions. |
| <a id="code-unknown-function"></a>`unknown-function` | A call names a function that is not in the closed function table. |
| <a id="code-unknown-identifier"></a>`unknown-identifier` | The source names an identifier that is not in the schema. |
| <a id="code-unterminated-string"></a>`unterminated-string` | A string literal is missing its closing quote. |

## View document codes

| Code | Meaning |
|---|---|
| <a id="code-view-bad-data-name"></a>`view.bad_data_name` | A data source name is not a valid identifier. |
| <a id="code-view-bad-field-name"></a>`view.bad_field_name` | A field of a list source is not a valid identifier. |
| <a id="code-view-bad-name"></a>`view.bad_name` | The view name is not dotted lower-case segments, such as `bazaar.gc.feed`. |
| <a id="code-view-data-shadows-context"></a>`view.data_shadows_context` | A data source has the name of a context identifier. |
| <a id="code-view-dsl-version"></a>`view.dsl_version` | The document's `dslVersion` is not one this reader supports. |
| <a id="code-view-item-field-shadows"></a>`view.item_field_shadows` | A field of a list source has the name of something already in scope. |
| <a id="code-view-json"></a>`view.json` | The document is not valid JSON. |
| <a id="code-view-list-key"></a>`view.list_key` | A list's `key` does not name a field of its source. |
| <a id="code-view-list-source"></a>`view.list_source` | A list's `over` does not name a declared list source. |
| <a id="code-view-list-template"></a>`view.list_template` | A list does not have exactly one item template. |
| <a id="code-view-missing-icon-name"></a>`view.missing_icon_name` | An `icon` has no `name` from the icon set. |
| <a id="code-view-missing-label"></a>`view.missing_label` | A `button`, `icon` or `input` has no label. |
| <a id="code-view-shape"></a>`view.shape` | The document's JSON has the wrong shape: a missing or mistyped field. |
| <a id="code-view-tab-child"></a>`view.tab_child` | A child of `tabs` is not a tab. |
| <a id="code-view-tab-page"></a>`view.tab_page` | A tab does not have exactly one page child. |
| <a id="code-view-tab-placement"></a>`view.tab_placement` | A tab appears anywhere other than directly under `tabs`. |
| <a id="code-view-tab-title"></a>`view.tab_title` | A tab has no title. |
| <a id="code-view-tabs-empty"></a>`view.tabs_empty` | A `tabs` node has no tabs. |
| <a id="code-view-unexpected-children"></a>`view.unexpected_children` | A leaf widget has children. |
| <a id="code-view-unknown-action"></a>`view.unknown_action` | A button refers to an action that is not declared. |
| <a id="code-view-unknown-action-kind"></a>`view.unknown_action_kind` | An action has a kind outside the closed set. |
| <a id="code-view-unknown-widget"></a>`view.unknown_widget` | A node names a widget outside the closed set. |
