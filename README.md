# DMSLib
.NET Library for DataMover DAT Files

## Column editing

`DMSTable.AddColumn(definition, precedingColumn, defaultValue)` preserves the
field type and numeric precision, updates metadata and field counts, and adds the
default to every row. Pass `null` as `precedingColumn` to insert first. Duplicate
names, invalid anchors, inconsistent layouts and invalid defaults are rejected
before mutation. New names use PeopleSoft's uppercase 18-character field-name limit.

A null `defaultValue` writes the DAT zero-byte null sentinel. A literal space is
a character blank; an empty string is distinct. AddColumn requires explicit
nonempty character/numeric defaults, and accepts null for temporal/long/binary
fields. `B{HEX}` remains supported for legacy binary defaults. Row insert, edit and
delete operations preserve untouched values as bytes; they do not re-encode them
through UTF-8. `GetValueBytes` returns a copy of the original field bytes.

## Regression tests

On Windows with .NET 4.8 build tools and NuGet CLI:

```powershell
nuget restore DMSLib.sln -NonInteractive
msbuild DMSLib.sln /p:Configuration=Release
DMSLib.Tests\bin\Release\DMSLib.Tests.exe
```

The test executable returns 1 on failure and exercises nulls, type mappings,
numeric precision, binary preservation, input rejection and DAT read/write round
trips using synthetic data. The existing viewer remains on .NET Framework 4.8.
