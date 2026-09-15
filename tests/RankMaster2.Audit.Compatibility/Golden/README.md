# The golden v1 file

`rankmaster_db.v1.json` is what `JsonCatalog.Save` must keep writing, byte for byte, for the
library described in `V1SchemaTests.GoldenLibrary`. `V1SchemaTests.The_v1_schema_is_byte_for_byte_unchanged`
compares against it.

Two things are removed before the comparison, because they are legitimately not reproducible and
nothing else is:

* `lastUpdated` is set to `0`. It is a wall clock.
* the members of `images` are sorted by ordinal key. The writer emits them in directory
  enumeration order, which the filesystem chooses.

Everything else is compared exactly: the indentation, the escaping of non-ASCII names, the spelling
of every number, the order of the fields inside a row, and which fields exist at all. Each value is
re-emitted from its own parsed element rather than reformatted, so a change to any of those changes
these bytes.

## What to do when this test fails

Do not regenerate the file. A diff here means the on-disk format changed, and the desktop app and
Rank Master 1 read that format. Decide first whether the change is one anybody intended.

## Two harmless-looking details worth knowing

`"mu": 25` and `"sigma": 6` are written without a decimal point, where SPEC.md § Persistence spells
the same value `25.0` in its example. These are the same JSON number and every parser reads them
identically; it is a difference in the example, not in the schema.

The Unicode filename is stored escaped: `Ärger am Fluß — фото №7.jpg` is written as
`\u00C4rger am Flu\u00DF \u2014 \u0444\u043E\u0442\u043E \u21167.jpg`, because
`System.Text.Json` escapes non-ASCII by default. Note `\u21167`: that is `№` (U+2116) followed by
the digit 7 — a JSON escape consumes exactly four hex digits, so it is unambiguous, but it is the
kind of thing worth having pinned in a golden file. Any JSON reader, including Rank Master 1's,
unescapes it back to the same name.
