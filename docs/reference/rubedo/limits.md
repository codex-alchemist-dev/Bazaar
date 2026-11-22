# Limits

These are floors. A caller may tighten them through `RubedoOptions`, never loosen them.

| Limit | Default | Diagnostic |
|---|---|---|
| Source length | 512 characters | `too-long` |
| Tokens | 256 | `too-many-tokens` |
| Nesting depth | 32 | `too-deep` |
| Evaluation steps | 1000, one per syntax node visited | `budget` |
| String literal | 64 characters | `string-too-long` |
| Number magnitude | 1000000000000000 | `number-out-of-range` |
| `inSet` candidates | 16 after the first argument | `arity` |
