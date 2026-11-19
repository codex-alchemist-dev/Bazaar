# Rubedo DSL: language design for layout rules and visibility

Status: DECIDED (owner, 2026-11-13) for the answers in section 11. Owner decided "full DSL in v1, built properly, no temporary shortcuts" (2026-11-13). Name: Rubedo (owner, 2026-11-12). Clearance is open, so the name lives in one constant (`RubedoLanguage.Name`).
Depends on: 41-client-gui-customization-and-dsl.md (sections 4.1, 6, 7, 8 and decision 4 in section 10), 15-security-taint-and-approvals.md (approval tiers).

## 1. Purpose and non-goals

Rubedo states rules inside typed JSON documents, for example a pane's `visibleWhen`, a layout slot's condition, or a command's enabled state. Rules depend on the current device, display, posture and accessibility state.

Non-goals, and the reason for each:
- No general-purpose programming: no loops, no user functions, no recursion. Every rule is a pure expression that always finishes.
- No I/O, clock, randomness, or host calls. The same input gives the same output on every head and on the server.
- No reflection, no dynamic member access, no string-to-code evaluation.
- No capability grants. A rule can hide or show a pane, never add a permission (doc 41 section 6, rule 4).

## 2. Values and types

Three value types, all immutable:

| Type | Literal | Notes |
|---|---|---|
| `bool` | `true`, `false` | |
| `number` | `12`, `0.5`, `600` | Decimal (128-bit), not binary float, so `0.1 + 0.2 == 0.3`. Range ±10^15. Overflow is an error. |
| `string` | `"phone"` | UTF-16, at most 64 characters, no escape sequences in v1 (a `"` cannot appear). |

There are no implicit conversions. Operators need exact types. The compiler infers the type of every expression and rejects a mismatch before any evaluation.

## 3. Context (identifiers)

The host declares the context. Identifiers are a closed, typed set. An unknown identifier is a compile error, so typos fail at load time. v1 set:

| Identifier | Type | Meaning |
|---|---|---|
| `deviceClass` | string | `phone`, `foldable`, `tablet`, `desktop` (doc 41 section 4.8) |
| `display` | string | `single`, `cover`, `inner` |
| `posture` | string | `folded`, `halfOpen`, `unfolded` |
| `widthDp` | number | Window width in dp. Half-open threshold is 600 (owner, 2026-11-12). |
| `heightDp` | number | Window height in dp |
| `textScale` | number | Accessibility text scale, 1.0 by default |
| `reduceMotion` | bool | Platform reduced-motion setting |
| `isOwner` | bool | True when the viewer is the owner (owner-only overrides, decision 6) |
| `colorScheme` | string | `light`, `dark` |

A head supplies every identifier on each evaluation. A missing value is a runtime error, not a silent default.

## 4. Grammar

```
expr      := or
or        := and ( "||" and )*
and       := equality ( "&&" equality )*
equality  := relation ( ("==" | "!=") relation )?
relation  := sum ( ("<" | "<=" | ">" | ">=") sum )?
sum       := product ( ("+" | "-") product )*
product   := unary ( ("*" | "/" | "%") unary )*
unary     := ("!" | "-") unary | call
call      := primary ( "(" [ expr ( "," expr )* ] ")" )?   -- only for identifiers in the function table
primary   := number | string | "true" | "false" | identifier | "(" expr ")"
```

Relational and equality operators do not chain (`a < b < c` is a syntax error). Parentheses are required to combine them.

## 5. Closed function table

Functions are fixed and listed here. Adding one is a language version change, not a document change.

| Function | Signature | Result |
|---|---|---|
| `min(a, b)`, `max(a, b)` | number, number | number |
| `clamp(x, lo, hi)` | number ×3 | number (error when lo > hi) |
| `abs(x)` | number | number |
| `contains(s, t)`, `startsWith(s, t)` | string, string | bool |
| `inSet(x, a, b, ...)` | same type as x, 1 to 16 args | bool |

No other functions exist. A call to an unknown name is a compile error.

## 6. Limits (floors; operators may tighten, never loosen, doc 41 section 6 rule 6)

| Limit | Default |
|---|---|
| Source length | 512 characters |
| Tokens | 256 |
| Nesting depth | 32 |
| Evaluation steps | 1000 (one per AST node visited) |
| String literal | 64 characters |
| `inSet` arguments | 16 |

Exceeding a compile limit is a compile error. Exceeding the step budget at run time is a runtime error. Neither can hang or crash the head.

## 7. Compile and evaluate

- `Compile(source, schema)` returns a compiled, immutable expression with its inferred result type, or a list of errors with a stable code and a character offset. It never throws on bad input.
- The compiled result must be `bool` for a visibility rule. Any other type is a compile error for that use.
- `Evaluate(compiled, context)` returns `Value` or `Error` (code and message). It never throws on bad input.
- Failure policy for a visibility rule: a runtime error hides the pane and raises a notice in the layout issue list. A bad document is rejected with a path and code, and the last good layout stays in force (doc 41 section 6, rule 7).

## 8. Determinism and the golden corpus

- Same source and same context give the same result on every head and on the server. Decimal arithmetic is specified exactly, so there is no platform float drift.
- One golden corpus, JSON cases of `{ source, context, expectedValue | expectedErrorCode }`, lives in the repo and is run by every head's tests. A head that disagrees fails CI.
- The engine is one pure C# implementation. Heads reference it. There is no second evaluator in Avalonia, Android or the terminal.

## 9. Where the engine lives (owner decision, 2026-11-13)

Rubedo is a module, `Bazaar.Rubedo`, loaded through the standard module contract (doc 41 section 8). The engine is a pure library inside that module, with no Avalonia and no I/O reference. The server and every head reference the same library, so there is one evaluator. `Bazaar.Client.Core` holds no DSL code: it is an adapter, and every function is a module.

Owner correction (2026-11-13): an earlier draft offered "inside Client.Core" as an option. That option is withdrawn.

## 10. Document integration

- A layout node gets an optional `visibleWhen` string (a Rubedo source). It replaces the reserved field, which the validator rejects today. This is additive within schema version 1 once the owner approves.
- Commands may use `enabledWhen` the same way (later WP).
- The document's `dslVersion` (integer, 1 for v1) is checked on load. An unknown version is a document-level rejection.

## 11. Questions for the owner (answered 2026-11-13)

Owner answers (2026-11-13, by card):
1. Engine location: its own pure library inside the `Bazaar.Rubedo` module (section 9).
2. Number type: decimal (section 2).
3. String and set functions are in v1 (section 5).
4. Failure policy: a visibility rule that errors hides the pane and raises a notice (fail closed; section 7).

## 12. Work packages after approval

1. Lexer and parser with the grammar above, tests for every syntax error code.
2. Type checker with the closed function table, tests for each type mismatch.
3. Evaluator with decimal arithmetic and budgets, tests for each limit.
4. Golden corpus (JSON) and a runner shared by every head.
5. Wire into layout `visibleWhen`, replacing the reserved field, with validator tests.
6. Owner review of the corpus before the language is marked DECIDED.
