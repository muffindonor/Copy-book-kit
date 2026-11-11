# CopybookKit

[![CI](https://github.com/muffindonor/Copy-book-kit/actions/workflows/ci.yml/badge.svg)](https://github.com/muffindonor/Copy-book-kit/actions/workflows/ci.yml)
[![CopybookKit on NuGet](https://img.shields.io/nuget/v/CopybookKit?label=CopybookKit)](https://www.nuget.org/packages/CopybookKit)
[![CopybookKit downloads](https://img.shields.io/nuget/dt/CopybookKit?label=downloads)](https://www.nuget.org/packages/CopybookKit)
[![CopybookKit.Tool on NuGet](https://img.shields.io/nuget/v/CopybookKit.Tool?label=CopybookKit.Tool)](https://www.nuget.org/packages/CopybookKit.Tool)
[![CopybookKit.Tool downloads](https://img.shields.io/nuget/dt/CopybookKit.Tool?label=downloads)](https://www.nuget.org/packages/CopybookKit.Tool)

Read mainframe data files in .NET. CopybookKit parses a COBOL copybook at run time and uses it to decode the records of a data file: EBCDIC text, zoned decimal, COMP-3 packed decimal and binary fields, including `REDEFINES`, `OCCURS` and `OCCURS DEPENDING ON`.

It comes as a library with no dependencies, and as a command-line tool that converts a data file to JSON Lines or CSV.

> Status: version 0.1.0. It has been tested against EBCDIC sample files published by AWS, but not yet against files from a production mainframe. See [Limits](#limits).

## The problem it solves

A mainframe file is not self-describing. The bytes `00 12 34 5C` are the number 123.45 only if you know the field is `PIC S9(5)V99 COMP-3`, and that knowledge lives in a COBOL copybook. To get such a file into a database or a data lake you have to work out every field's offset, size and encoding from the copybook, then decode each one.

CopybookKit does that from the copybook itself, so no offsets are written by hand.

## Command-line tool

```
copybook describe CVACT01Y.cpy
```

```
Level  Name                        Offset  Size  Type                         Occurs  Redefines
01     ACCOUNT-RECORD                   0   300  group
05       ACCT-ID                        0    11  zoned decimal(11)
05       ACCT-ACTIVE-STATUS            11     1  text  X(01)
05       ACCT-CURR-BAL                 12    12  signed zoned decimal(12,2)
...
```

```
copybook convert ACCTDATA.PS --copybook CVACT01Y.cpy --limit 1
```

```json
{"ACCT-ID":1,"ACCT-ACTIVE-STATUS":"Y","ACCT-CURR-BAL":194.00,"ACCT-CREDIT-LIMIT":2020.00,"ACCT-OPEN-DATE":"2014-11-20", ...}
```

Common options:

| Option | Meaning |
|---|---|
| `--to json` or `--to csv` | JSON keeps groups and tables nested. CSV flattens them into columns such as `PHONE(2)`. |
| `--encoding cp037` | The file's code page: `cp037` (default), `cp1047`, `cp500`, `cp1140`, `cp424`, another IBM number, or `ascii`. |
| `--recfm fixed` | `fixed` (default), `rdw` for variable-length records with record descriptor words, or `text` for one record per line. |
| `--select "TYPE:A=ACCOUNT-DATA,C=CUSTOMER-DATA"` | For a file with several record types: decode only the layout that matches each record. |
| `--strict` | Fail on the first invalid number. By default it is written as null and counted. |

Run `copybook --help` for the rest.

## Library

```csharp
using CopybookKit;

Copybook copybook = Copybook.Load("CVACT01Y.cpy");
using CopybookReader reader = CopybookReader.Open("ACCTDATA.PS", copybook);

while (reader.Read())
{
    long? id = reader.GetInt64("ACCT-ID");
    decimal? balance = reader.GetDecimal("ACCT-CURR-BAL");
    string? status = reader.GetString("ACCT-ACTIVE-STATUS");
}
```

Other ways to take a record:

```csharp
string json = reader.ToJson();                        // nested, as the copybook is
IReadOnlyList<object?> row = reader.GetValues();      // flat, in the order of reader.Schema.Columns
object? phone = reader.GetValue("PHONE-NUMBER", 2);   // second occurrence of a field in a table
```

Options describe the file:

```csharp
var options = new ReadOptions
{
    Encoding = EbcdicEncodings.Cp1047,
    Format = RecordFormat.VariableRdw,
    InvalidData = InvalidDataPolicy.Throw,
    Selectors =
    {
        new RedefineRule("REC-TYPE", new Dictionary<string, string> { ["A"] = "ACCOUNT-DATA", ["C"] = "CUSTOMER-DATA" }),
    },
};
```

Numbers come back as `long` when the field is a whole number of up to 18 digits, and as `decimal` otherwise, so money is never held in floating point.

## What is supported

| Area | Supported |
|---|---|
| Pictures | `X`, `A`, `9`, `S`, `V`, `P` and repeat counts. Edited pictures such as `ZZ,ZZ9.99` are read as text. |
| Usage | `DISPLAY`, `COMP` / `BINARY` / `COMP-4`, `COMP-5`, `COMP-3` / `PACKED-DECIMAL`, `COMP-1`, `COMP-2`, `INDEX` |
| Signs | In the last or first digit, or `SIGN LEADING` / `TRAILING SEPARATE`. EBCDIC sign zones and the ASCII forms `{ A-I } J-R` and `p-y`. |
| Structure | Groups, `OCCURS`, nested `OCCURS`, `OCCURS DEPENDING ON`, `REDEFINES`, several 01 levels, `USAGE` and `SIGN` on a group |
| Source | Fixed and free format, sequence numbers, comments, continuation lines. Levels 66 and 88 and `VALUE` clauses are skipped. |
| Records | Fixed length, variable length with record descriptor words, one per line |
| Code pages | The EBCDIC code pages built into .NET, including 037, 500, 1047, 1140 and 424, plus ASCII and UTF-8 |
| Floating point | IBM hexadecimal and IEEE 754 |
| Frameworks | .NET 8 and .NET 10. The library is marked compatible with trimming and native AOT. |

Anything that would make an offset wrong is refused with an error that names the line, never guessed at.

## How it is tested

The tests don't only check the decoder against values worked out by hand. They read real EBCDIC files published by other projects and compare the result with an independent source.

| Test data | What it is compared with |
|---|---|
| 9 EBCDIC files from the AWS CardDemo mainframe application, 626 records | The same files published in ASCII. All records match except 2, where the two published data sets hold different values. |
| CardDemo's export file: 500 records in 5 layouts, with `REDEFINES`, `OCCURS`, COMP and COMP-3 | The same values stored as plain digits in the other CardDemo files. All 400 transaction, card and cross-reference records match. 48 of 50 customers and 48 of 50 accounts match; in the other 2 of each, the two files hold different values. |
| 3 EBCDIC files from AWS Mainframe Data Utilities: multiple layouts, variable-length records with a variable table, and every binary and packed size | The output of the AWS tool, a separate implementation in Python. All 2,241 records match. |
| A file written by GnuCOBOL 3.1.2, 500 records | The text the COBOL program wrote it from |

The numbers:

- 316 tests pass on .NET 8 and on .NET 10 on Windows, and on .NET 10 on Linux (`dotnet test`).
- `python scripts/mutation-check.py` adds 34 deliberate bugs to the code one at a time and checks that the tests notice each one. All 34 are caught.

- The full 13.4 MB packed and binary file from AWS Mainframe Data Utilities, 89,280 records, converts to JSON in about 0.8 seconds and to CSV in about 0.7 seconds on a desktop PC, including starting the tool (median of 5 runs). Every record matched the AWS tool's output. The repository holds only its first 2,000 records.

The sample files and their licences are listed in [tests/CopybookKit.Tests/Samples/NOTICE.md](https://github.com/muffindonor/Copy-book-kit/blob/main/tests/CopybookKit.Tests/Samples/NOTICE.md).

## Limits

- **No production mainframe file has been read with it.** The EBCDIC test files are sample data published by AWS.
- `SYNCHRONIZED` is refused, because slack bytes are not implemented.
- `COPY`, `REPLACE` and `EXEC SQL INCLUDE` inside a copybook are refused. Expand nested copybooks first.
- National and double-byte data (`PIC N`, `PIC G`, `DISPLAY-1`) and `POINTER` are refused.
- `OCCURS UNBOUNDED` is refused.
- Numbers of more than 28 digits are refused, because that is what `decimal` holds exactly. IBM compilers allow 31.
- Variable-length files must keep their record descriptor words. Block descriptor words and spanned records are not handled.
- A fixed-length file whose records vary in length through `OCCURS DEPENDING ON`, with no descriptor words, can't be split into records.
- Multi-byte EBCDIC code pages, such as Japanese, are not supported.
- Unsigned packed decimal with no sign half byte (`COMP-6`) is not supported.
- It reads files. It doesn't write them.
- Parquet output is not included.

## Build and test

```bash
dotnet build
```

```bash
dotnet test
```

The tests need only the .NET SDK, with the .NET 8 and .NET 10 runtimes.

Until the packages are on NuGet, install the tool from a local build:

```bash
dotnet pack -c Release -o artifacts
```

```bash
dotnet tool install --global CopybookKit.Tool --add-source artifacts --version 0.1.0
```

## Licence

MIT. See [LICENSE](https://github.com/muffindonor/Copy-book-kit/blob/main/LICENSE). The sample data under `tests/CopybookKit.Tests/Samples` belongs to its authors and is under the Apache-2.0 licence.
