# Security

## Reporting a problem

Please report a security problem privately: open the repository's **Security** tab and choose **Report a vulnerability**. Don't open a public issue for it. You should get an answer within a week.

Only the latest version on NuGet gets fixes.

## What the library promises for untrusted input

A copybook or a data file may come from someone you don't trust. For both, CopybookKit aims to fail with a documented exception, quickly and without reserving much memory:

- A copybook either parses or throws `CopybookException`. A picture string may expand to at most 1 MB, and a copybook to at most 1,000,000 items once its `OCCURS` clauses are multiplied out.
- A data file is either read or throws `RecordFormatException`. A reader refuses a record layout longer than `ReadOptions.MaxRecordLength` (16 MB by default) before it reserves memory for it.
- Control characters in a copybook are read as spaces, so an item name or an error message can't carry a terminal escape sequence.

These are checked by `HardeningTests` and by the random tests in `FuzzTests`.

## What stays your job

- **Output size.** A record is written with every field of its layout, so a copybook with a million fields produces a large line for every record, however short the data is. Limit the output if both the copybook and the data are untrusted.
- **CSV opened in a spreadsheet.** A text field that starts with `=`, `+`, `-` or `@` is run as a formula by spreadsheet programs. Pass `protectFormulas: true` to `WriteCsv`, or `--csv-safe` to the tool, when the data is untrusted and people will open the file in one.
- **Text values on a terminal.** JSON output escapes control characters. CSV output writes text fields as they are.
- **Paths.** The command-line tool reads and writes the paths it is given. Don't pass it paths from an untrusted source.
