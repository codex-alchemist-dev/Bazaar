# Operators and precedence

Operators need exact types; there are no implicit conversions. Relational and equality operators do not chain, so `a < b < c` is a syntax error (`chained-comparison`). A relation under `==` or `!=` needs parentheses.

## Precedence

From loosest to tightest binding:

| Level | Operators | Operands | Result |
|---|---|---|---|
| 1 | `\|\|` | bool, bool | bool, short-circuits |
| 2 | `&&` | bool, bool | bool, short-circuits |
| 3 | `==` `!=` | two operands of the same type | bool |
| 4 | `<` `<=` `>` `>=` | number, number | bool |
| 5 | `+` `-` | number, number | number |
| 6 | `*` `/` `%` | number, number | number |
| 7 | `!` | bool (prefix) | bool |
| 7 | `-` | number (prefix) | number |
| 8 | call `name(args)` | per the function table | per the function table |

## Grammar

```
expr      := or
or        := and ( "||" and )*
and       := equality ( "&&" equality )*
equality  := operand ( ("==" | "!=") operand )?
relation  := sum ( ("<" | "<=" | ">" | ">=") sum )?
operand   := sum | "(" relation ")"   -- a relation under == or != needs parentheses
sum       := product ( ("+" | "-") product )*
product   := unary ( ("*" | "/" | "%") unary )*
unary     := ("!" | "-") unary | call
call      := primary ( "(" [ expr ( "," expr )* ] ")" )?   -- only for identifiers in the function table
primary   := number | string | "true" | "false" | identifier | "(" expr ")"
```

## Literals

| Type | Literal | Notes |
|---|---|---|
| bool | `true`, `false` | |
| number | `12`, `0.5`, `600` | Decimal, not binary float, so `0.1 + 0.2 == 0.3`. Range plus or minus 1000000000000000. Overflow is an error. |
| text | `"phone"` | At most 64 characters, no escape sequences, so a `"` cannot appear. |
