# Functions

The function table is closed: adding a function is a language version change. A call to any other name is a compile error (`unknown-function`).

<a id="fn-abs"></a>
## abs

`abs(value: number): number`

The absolute value of a number.

Example (from the golden corpus):

```
abs(-4) == 4
```

With an empty context this evaluates to `true`.

<a id="fn-clamp"></a>
## clamp

`clamp(value: number, lo: number, hi: number): number`

Limits a number to the range lo to hi. An error when lo is above hi.

Example (from the golden corpus):

```
clamp(5, 0, 10) == 5
```

With an empty context this evaluates to `true`.

<a id="fn-contains"></a>
## contains

`contains(text: text, part: text): bool`

True when text contains part.

Example (from the golden corpus):

```
contains("bazaar.gc", "gc")
```

With an empty context this evaluates to `true`.

<a id="fn-inSet"></a>
## inSet

`inSet(value: number|text, candidate...: number|text): bool`

True when value equals one of the candidates. Numbers or text, all of the same type.

The first argument is the value and the rest are candidates, one to 16 of them.

Example (from the golden corpus):

```
inSet(display, "cover", "inner")
```

With `display = "inner"` this evaluates to `true`.

<a id="fn-max"></a>
## max

`max(a: number, b: number): number`

The larger of two numbers.

Example (from the golden corpus):

```
max(3, 7) == 7
```

With an empty context this evaluates to `true`.

<a id="fn-min"></a>
## min

`min(a: number, b: number): number`

The smaller of two numbers.

Example (from the golden corpus):

```
min(3, 7) == 3
```

With an empty context this evaluates to `true`.

<a id="fn-startsWith"></a>
## startsWith

`startsWith(text: text, prefix: text): bool`

True when text starts with prefix.

Example (from the golden corpus):

```
startsWith(deviceClass, "fold")
```

With `deviceClass = "foldable"` this evaluates to `true`.
