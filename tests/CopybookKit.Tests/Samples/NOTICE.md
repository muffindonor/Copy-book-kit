# Sample data used by the tests

These files are test fixtures. They are not part of the CopybookKit library or its NuGet packages.

| Folder | Source | Licence |
|---|---|---|
| `carddemo/` | [aws-samples/aws-mainframe-modernization-carddemo](https://github.com/aws-samples/aws-mainframe-modernization-carddemo), from `app/data/EBCDIC`, `app/data/ASCII` and `app/cpy` | Apache-2.0, see `carddemo/LICENSE` and `carddemo/NOTICE` |
| `aws-mdu/` | [aws-samples/mainframe-data-utilities](https://github.com/aws-samples/mainframe-data-utilities), from `sample-data` and `LegacyReference` | Apache-2.0, see `aws-mdu/LICENSE` |
| `gnucobol/` | Written for this project's sister project with GnuCOBOL 3.1.2 | MIT, as this repository |

Changes made to the originals:

- `aws-mdu/COBPACK.EBCDIC.2000.bin` is the first 2,000 records (300,000 bytes) of `sample-data/COBPACK.OUTFILE.txt`.
- `aws-mdu/COBPACK.ASCII.2000.txt` is the first 2,000 lines of `sample-data/COBPACK.ASCII.txt`.
- Every other file is an unmodified copy.

All of the data is synthetic sample data published by its authors. None of it describes real people.
