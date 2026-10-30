# Not links

Empty: [[]] and [[ ]] and [[|only alias]] and [[#]].
Newline: [[first half
second half]] stays text, then [[ok after newline]].
Brackets: [[a]b]] and [[a[b]] stay text.
Unclosed: [[never closed
Escaped: \[[x]] stays text.
Standard: [text](https://example.com) and [text][ref] and ![alt](img.png).

[ref]: https://example.com/ref
