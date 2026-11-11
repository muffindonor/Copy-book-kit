using System.Globalization;
using System.Text;

namespace CopybookKit;

/// <summary>The EBCDIC code pages built into .NET, by their IBM names.</summary>
public static class EbcdicEncodings
{
    /// <summary>IBM numbers that .NET files under a Windows code page number 20000 higher.</summary>
    private static readonly HashSet<int> Offset20000 =
        [273, 277, 278, 280, 284, 285, 290, 297, 420, 423, 424, 833, 838, 871, 880, 905, 924];

    static EbcdicEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Code page 037: US and Canada. The most common mainframe code page.</summary>
    public static Encoding Cp037 => Get(37);

    /// <summary>Code page 500: international Latin-1.</summary>
    public static Encoding Cp500 => Get(500);

    /// <summary>Code page 1047: Latin-1 for open systems, used by z/OS UNIX.</summary>
    public static Encoding Cp1047 => Get(1047);

    /// <summary>Code page 1140: code page 037 with the euro sign.</summary>
    public static Encoding Cp1140 => Get(1140);

    /// <summary>Code page 424: Hebrew.</summary>
    public static Encoding Cp424 => Get(424);

    /// <summary>Gets an EBCDIC code page by its IBM number, such as 37, 273 or 1047.</summary>
    /// <exception cref="ArgumentException">.NET has no such code page.</exception>
    public static Encoding Get(int ibmCodePage)
    {
        int codePage = Offset20000.Contains(ibmCodePage) ? ibmCodePage + 20000 : ibmCodePage;
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            throw new ArgumentException($"Code page {ibmCodePage} is not available.", ex);
        }
    }

    /// <summary>
    /// Gets an encoding by name. Accepts IBM code page names such as <c>cp037</c>,
    /// <c>ibm1047</c> or <c>37</c>, the word <c>ebcdic</c> for code page 037, and any name
    /// .NET knows, such as <c>ascii</c>, <c>latin1</c> or <c>utf-8</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a known encoding.</exception>
    public static Encoding Parse(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string trimmed = name.Trim();
        if (trimmed.Equals("ebcdic", StringComparison.OrdinalIgnoreCase))
        {
            return Cp037;
        }

        string digits = trimmed;
        foreach (string prefix in (string[])["cp", "ibm-", "ibm", "ebcdic-cp-", "ebcdic-"])
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                digits = trimmed[prefix.Length..];
                break;
            }
        }

        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return Get(number);
        }

        try
        {
            return Encoding.GetEncoding(trimmed);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{name}' is not a known encoding.", ex);
        }
    }

    internal static bool IsEbcdic(Encoding encoding)
    {
        byte[] zero = encoding.GetBytes("0");
        return zero.Length == 1 && zero[0] == 0xF0;
    }
}
