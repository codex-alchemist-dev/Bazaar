# Actions

The action set of view documents is closed, and no action sends a raw wire call. A button refers to a declared action by its id.

| Action | Meaning |
|---|---|
| <a id="action-invoke"></a>`invoke` | Runs a named command from the module manifest, through the same approval tier as the control it mirrors. A view cannot lower a tier. |
| <a id="action-navigate"></a>`navigate` | Navigates to a pane or a GC. The target names the destination. |
| <a id="action-openPane"></a>`openPane` | Opens a pane. The target names the pane. |
| <a id="action-setSetting"></a>`setSetting` | Sets a setting key the manifest declares. The target names the key. |
