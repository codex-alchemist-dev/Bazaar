# Widgets

The widget set of view documents is closed. A node names a widget by its wire name; an unknown name is `view.unknown_widget`.

| Widget | Children | Role |
|---|---|---|
| <a id="widget-badge"></a>`badge` | none | Count or status chip. |
| <a id="widget-button"></a>`button` | none | Calls a named action. Needs a label. |
| <a id="widget-divider"></a>`divider` | none | Separator. |
| <a id="widget-grid"></a>`grid` | many | Two-dimensional grid, column count prop. |
| <a id="widget-icon"></a>`icon` | none | Icon from a closed icon set. Needs a label. |
| <a id="widget-input"></a>`input` | none | Text input bound to a value; the write goes through an action. Needs a label. |
| <a id="widget-list"></a>`list` | one template | Keyed list over a list data source, with one item template. |
| <a id="widget-placeholder"></a>`placeholder` | none | Failure card shown in place of something that could not render. |
| <a id="widget-row"></a>`row` | many | Horizontal row, wraps by flag. |
| <a id="widget-scroll"></a>`scroll` | one | Scrolling region. |
| <a id="widget-stack"></a>`stack` | many | Vertical stack. |
| <a id="widget-tabs"></a>`tabs` | tabs | Named tabs. Its children are `tab` items, which are structural and not a widget. |
| <a id="widget-text"></a>`text` | none | Text with a style token; a localisation key or literal. |
