# Identifiers

The host supplies every identifier on each evaluation. An unknown identifier is a compile error (`unknown-identifier`), and a missing value is a runtime error (`missing-value`), never a silent default.

| Identifier | Type | Meaning |
|---|---|---|
| <a id="id-colorScheme"></a>`colorScheme` | text | One of `light`, `dark`. |
| <a id="id-deviceClass"></a>`deviceClass` | text | One of `phone`, `foldable`, `tablet`, `desktop`. |
| <a id="id-display"></a>`display` | text | One of `single`, `cover`, `inner`. |
| <a id="id-heightDp"></a>`heightDp` | number | Window height in dp. |
| <a id="id-isOwner"></a>`isOwner` | bool | True when the viewer is the owner. |
| <a id="id-posture"></a>`posture` | text | One of `folded`, `halfOpen`, `unfolded`. |
| <a id="id-reduceMotion"></a>`reduceMotion` | bool | The platform's reduced-motion setting. |
| <a id="id-textScale"></a>`textScale` | number | Accessibility text scale, 1.0 by default. |
| <a id="id-widthDp"></a>`widthDp` | number | Window width in dp. The half-open threshold is 600. |
