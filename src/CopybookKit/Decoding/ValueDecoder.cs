namespace CopybookKit.Decoding;

/// <summary>A decoded field. <see cref="Type"/> is <see cref="ValueType.None"/> for null.</summary>
internal ref struct FieldValue
{
    public ValueType Type;
    public long Int64;
    public decimal Decimal;
    public double Double;

    /// <summary>Valid until the next field is decoded.</summary>
    public ReadOnlySpan<char> Text;
}

/// <summary>Decodes one field at a time according to the read options, and keeps the statistics.</summary>
internal sealed class ValueDecoder(ReadOptions options, ReadStatistics statistics)
{
    private const int MaxHexBytes = 24;

    private readonly TextDecoder _text = new(options.Encoding, options.Trim);
    private readonly bool _ebcdic = EbcdicEncodings.IsEbcdic(options.Encoding);
    private readonly bool _littleEndian = options.BinaryByteOrder == ByteOrder.LittleEndian;
    private readonly bool _ibmFloat =
        (options.FloatFormat ?? (EbcdicEncodings.IsEbcdic(options.Encoding) ? FloatFormat.IbmHex : FloatFormat.Ieee)) == FloatFormat.IbmHex;

    private readonly bool _throwOnInvalid = options.InvalidData == InvalidDataPolicy.Throw;

    public long RecordNumber { get; set; }

    /// <summary>False while a record is decoded a second time, so that nothing is counted twice.</summary>
    public bool Counting { get; set; }

    /// <summary>The byte a space takes in the file, for padding short lines.</summary>
    public byte Space => _ebcdic ? (byte)0x40 : (byte)' ';

    public void Decode(DataItem item, ReadOnlySpan<byte> field, bool present, out FieldValue value)
    {
        value = default;
        if (!present)
        {
            if (Counting)
            {
                statistics.MissingFields++;
            }

            return;
        }

        DecodeStatus status = DecodeCore(item, field, ref value);
        if (status == DecodeStatus.Ok)
        {
            return;
        }

        value = default;
        if (status == DecodeStatus.Blank)
        {
            if (Counting)
            {
                statistics.BlankNumericFields++;
            }

            return;
        }

        if (_throwOnInvalid)
        {
            throw new RecordFormatException(Describe(item, field));
        }

        if (Counting)
        {
            statistics.InvalidFields++;
            if (statistics.Issues.Count < ReadStatistics.MaxIssues)
            {
                statistics.AddIssue(Describe(item, field));
            }
        }
    }

    /// <summary>Decodes without counting or throwing. Returns false for blank, invalid or missing data.</summary>
    public bool TryDecode(DataItem item, ReadOnlySpan<byte> field, out FieldValue value)
    {
        value = default;
        if (DecodeCore(item, field, ref value) == DecodeStatus.Ok)
        {
            return true;
        }

        value = default;
        return false;
    }

    private DecodeStatus DecodeCore(DataItem item, ReadOnlySpan<byte> field, ref FieldValue value)
    {
        UInt128 magnitude;
        bool negative;
        DecodeStatus status;
        switch (item.Kind)
        {
            case ItemKind.Text:
                value.Text = _text.Decode(field);
                value.Type = ValueType.Text;
                return DecodeStatus.Ok;

            case ItemKind.Float or ItemKind.Double:
                value.Double = _ibmFloat ? NumericCodec.IbmFloat(field) : NumericCodec.IeeeFloat(field, _littleEndian);
                value.Type = ValueType.Double;
                return DecodeStatus.Ok;

            case ItemKind.ZonedDecimal:
                status = NumericCodec.Zoned(field, item.Sign, _ebcdic, out magnitude, out negative);
                break;

            case ItemKind.PackedDecimal:
                status = NumericCodec.Packed(field, item.Digits, item.IsSigned, out magnitude, out negative);
                break;

            case ItemKind.Binary:
                status = NumericCodec.Binary(field, item.IsSigned, _littleEndian, out magnitude, out negative);
                break;

            default:
                throw new InvalidOperationException($"{item.Name} is a group and has no value.");
        }

        if (status != DecodeStatus.Ok)
        {
            return status;
        }

        if (item.ValueType == ValueType.Int64)
        {
            if (!NumericCodec.TryToInt64(magnitude, negative, item.Scale, out value.Int64))
            {
                return DecodeStatus.Invalid;
            }

            value.Type = ValueType.Int64;
            return DecodeStatus.Ok;
        }

        if (!NumericCodec.TryToDecimal(magnitude, negative, item.Scale, out value.Decimal))
        {
            return DecodeStatus.Invalid;
        }

        value.Type = ValueType.Decimal;
        return DecodeStatus.Ok;
    }

    private string Describe(DataItem item, ReadOnlySpan<byte> field)
    {
        string hex = Convert.ToHexString(field[..Math.Min(field.Length, MaxHexBytes)]);
        string more = field.Length > MaxHexBytes ? "..." : string.Empty;
        string kind = item.Kind switch
        {
            ItemKind.ZonedDecimal => "zoned decimal",
            ItemKind.PackedDecimal => "packed decimal",
            _ => "binary",
        };
        return $"Record {RecordNumber}: {item.Name} is not a valid {kind} number (bytes {hex}{more}).";
    }
}
