#!/usr/bin/env python3
"""Checks that the tests notice bugs.

Each entry below is a small, deliberate bug. The script copies the repository to a
temporary folder, applies one bug at a time, and runs the tests. A bug the tests
don't notice is reported, and the script exits with 1.

    python scripts/mutation-check.py

It needs the .NET SDK. It never changes the repository itself.
"""
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = "src/CopybookKit/"

# (description, file, text to find, replacement)
MUTANTS = [
    ("packed: negative sign nibble D read as positive", SRC + "Decoding/NumericCodec.cs",
     "case 0xD or 0xB:\n                negative = true;\n                break;\n            default:\n                return DecodeStatus.Invalid;\n        }\n\n        if (negative && !signed)",
     "case 0xD or 0xB:\n                break;\n            default:\n                return DecodeStatus.Invalid;\n        }\n\n        if (negative && !signed)"),
    ("packed: unused half byte not checked", SRC + "Decoding/NumericCodec.cs",
     "if (digits % 2 == 0 && (field[0] & 0xF0) != 0)", "if (digits % 2 == 1 && (field[0] & 0xF0) != 0)"),
    ("packed: last digit dropped", SRC + "Decoding/NumericCodec.cs",
     "magnitude = useWide ? (wide * 10) + lastDigit : (UInt128)((fast * 10) + lastDigit);",
     "magnitude = useWide ? (wide * 10) : (UInt128)(fast * 10);"),
    ("packed: field size off by one", SRC + "Parsing/CopybookParser.cs",
     "item.Size = (picture.Digits / 2) + 1;", "item.Size = (picture.Digits + 1) / 2;"),
    ("zoned EBCDIC: sign zone D read as positive", SRC + "Decoding/NumericCodec.cs",
     "case 0xD or 0xB:\n                    negative = true;\n                    return digit <= 9;",
     "case 0xD or 0xB:\n                    return digit <= 9;"),
    ("zoned ASCII: J-R read as positive", SRC + "Decoding/NumericCodec.cs",
     "digit = (uint)(value - 'J' + 1);\n                negative = true;", "digit = (uint)(value - 'J' + 1);"),
    ("zoned ASCII: } read as 1", SRC + "Decoding/NumericCodec.cs",
     "case (byte)'}':\n                digit = 0;", "case (byte)'}':\n                digit = 1;"),
    ("zoned: separate sign ignored", SRC + "Decoding/NumericCodec.cs",
     "negative = sign == (ebcdic ? 0x60 : (byte)'-');", "negative = false && sign == (ebcdic ? 0x60 : (byte)'-');"),
    ("zoned: blank field read as invalid", SRC + "Decoding/NumericCodec.cs",
     "if (IsFilledWith(field, ebcdic ? EbcdicSpace : AsciiSpace) || IsFilledWith(field, 0))\n        {\n            return DecodeStatus.Blank;",
     "if (IsFilledWith(field, ebcdic ? EbcdicSpace : AsciiSpace) || IsFilledWith(field, 0))\n        {\n            return DecodeStatus.Invalid;"),
    ("binary: signed read as unsigned", SRC + "Decoding/NumericCodec.cs",
     "if (signed && value < 0)", "if (false && signed && value < 0)"),
    ("binary: byte order swapped", SRC + "Decoding/NumericCodec.cs",
     "bits = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(field) : BinaryPrimitives.ReadUInt32BigEndian(field);",
     "bits = littleEndian ? BinaryPrimitives.ReadUInt32BigEndian(field) : BinaryPrimitives.ReadUInt32LittleEndian(field);"),
    ("binary: 5 to 9 digits sized as 8 bytes", SRC + "Parsing/CopybookParser.cs",
     "<= 4 => 2,\n                    <= 9 => 4,", "<= 4 => 2,\n                    <= 8 => 4,"),
    ("IBM float: exponent bias wrong", SRC + "Decoding/NumericCodec.cs",
     "int exponent = (field[0] & 0x7F) - 64;", "int exponent = (field[0] & 0x7F) - 63;"),
    ("scale ignored when making a decimal", SRC + "Decoding/NumericCodec.cs",
     "negative, (byte)Math.Max(scale, 0));", "negative, 0);"),
    ("text: trailing spaces kept", SRC + "Decoding/TextDecoder.cs",
     "return _trim == TextTrim.Both ? text.Trim(' ') : text.TrimEnd(' ');", "return _trim == TextTrim.Both ? text.Trim(' ') : text;"),
    ("REDEFINES: redefinition placed after the item, not over it", SRC + "Reading/RecordWalker.cs",
     "int childStart = child.Redefines is { } redefined ? _starts[redefined.Ordinal] : position;", "int childStart = position;"),
    ("REDEFINES: a ruled-out item's storage is skipped", SRC + "Reading/RecordWalker.cs",
     "if (child.Redefines is null)\n                {\n                    position = Math.Max(position, childStart + (child.Size * child.MaxOccurs));\n                }",
     ""),
    ("REDEFINES: layout gives the redefinition its own space", SRC + "Parsing/CopybookParser.cs",
     "int childStart = child.Redefines?.Offset ?? (int)position;", "int childStart = (int)position;"),
    ("selector: choice leaks into the next record", SRC + "CopybookReader.cs",
     "bool[] inactive = _walker.Inactive;\n        Array.Clear(inactive);", "bool[] inactive = _walker.Inactive;"),
    ("OCCURS DEPENDING ON: always the maximum", SRC + "Reading/RecordWalker.cs",
     "int count = item.DependingOn is { } counter ? ReadCount(item, counter) : item.MaxOccurs;", "int count = item.MaxOccurs;"),
    ("OCCURS DEPENDING ON: count above the maximum allowed", SRC + "Reading/RecordWalker.cs",
     "if (count < 0 || count > table.MaxOccurs)", "if (count < 0)"),
    ("OCCURS: second occurrence overwrites the first", SRC + "Reading/RecordWalker.cs",
     "int shift = slotShift + (i * item.SlotsPerOccurrence);", "int shift = slotShift;"),
    ("OCCURS: group size not multiplied", SRC + "Parsing/CopybookParser.cs",
     "position = Math.Max(position, childStart + ((long)child.Size * child.MaxOccurs));", "position = Math.Max(position, childStart + (long)child.Size);"),
    ("record descriptor word: length read without its own 4 bytes", SRC + "Reading/RecordSource.cs",
     "int dataLength = length - DescriptorLength;", "int dataLength = length;"),
    ("fixed records: partial last record accepted", SRC + "Reading/RecordSource.cs",
     "if (read < _record.Length)\n        {\n            throw", "if (false)\n        {\n            throw"),
    ("text records: short line not padded", SRC + "Reading/RecordSource.cs",
     "if (length < minimumLength)\n            {\n                _line.AsSpan", "if (length < 0)\n            {\n                _line.AsSpan"),
    ("fixed format: columns 73 to 80 read as code", SRC + "Parsing/SourceText.cs",
     "Math.Min(line.Length, LastCodeColumn) - FirstCodeColumn", "line.Length - FirstCodeColumn"),
    ("group USAGE not passed to fields", SRC + "Parsing/CopybookParser.cs",
     "Finish(child, usage, sign);", "Finish(child, Usage.Unspecified, sign);"),
    ("separate sign takes no byte", SRC + "Parsing/CopybookParser.cs",
     "item.Size = picture.Digits + (item.Sign is SignPosition.LeadingSeparate or SignPosition.TrailingSeparate ? 1 : 0);", "item.Size = picture.Digits;"),
    ("edited picture: V counted as a byte", SRC + "Parsing/PictureInfo.cs",
     "if (c is not ('V' or 'P'))", "if (c is not 'P')"),
    ("statistics: invalid fields counted twice", SRC + "CopybookReader.cs",
     "_decoder.Counting = !_counted;\n        _walker.Walk(Record, data, _flatVisitor);", "_decoder.Counting = true;\n        _walker.Walk(Record, data, _flatVisitor);"),
    ("strict mode does not throw", SRC + "Decoding/ValueDecoder.cs",
     "if (_throwOnInvalid)", "if (false)"),
    ("CSV: quotes not doubled", SRC + "CopybookExport.cs",
     "output.Write(text.Replace(\"\\\"\", \"\\\"\\\"\"));", "output.Write(text);"),
    ("JSON: FILLER fields written", SRC + "Reading/RecordWalker.cs",
     "bool visible = item.IsGroup || !item.IsFiller;", "bool visible = true;"),
]


def main():
    work = tempfile.mkdtemp(prefix="copybookkit-mutation-")
    copy = os.path.join(work, "repo")
    shutil.copytree(ROOT, copy, ignore=shutil.ignore_patterns("bin", "obj", ".git", ".vs", "artifacts"))
    test = ["dotnet", "test", "tests/CopybookKit.Tests", "-c", "Release", "-f", "net10.0", "--nologo", "-v", "q"]
    try:
        if subprocess.run(test, cwd=copy, capture_output=True, text=True).returncode != 0:
            print("The tests fail before any bug is added. Fix that first.")
            return 1

        missed = []
        for number, (description, path, find, replacement) in enumerate(MUTANTS, 1):
            full = os.path.join(copy, path)
            with open(full, encoding="utf-8", newline="") as source:
                original = source.read()
            text = original.replace("\r\n", "\n")
            if text.count(find) != 1:
                print(f"{number:2}  NOT APPLIED  {description} (found {text.count(find)} times)")
                missed.append(description)
                continue

            with open(full, "w", encoding="utf-8", newline="") as source:
                source.write(text.replace(find, replacement))
            result = subprocess.run(test, cwd=copy, capture_output=True, text=True)
            with open(full, "w", encoding="utf-8", newline="") as source:
                source.write(original)

            output = result.stdout + result.stderr
            if result.returncode == 0:
                outcome = "MISSED"
                missed.append(description)
            elif "error CS" in output:
                outcome = "DID NOT COMPILE"
                missed.append(description)
            else:
                outcome = "caught"
            print(f"{number:2}  {outcome:15}  {description}", flush=True)

        print(f"\n{len(MUTANTS) - len(missed)} of {len(MUTANTS)} deliberate bugs were caught by the tests.")
        return 1 if missed else 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
