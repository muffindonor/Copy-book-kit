using System.Text;

namespace CopybookKit.Tests;

internal static class TestData
{
    public static string Sample(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Samples", .. parts]);

    /// <summary>Text as EBCDIC code page 037 bytes.</summary>
    public static byte[] Ebcdic(string text) => EbcdicEncodings.Cp037.GetBytes(text);

    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));

    public static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    /// <summary>Opens a reader over bytes held in memory.</summary>
    public static CopybookReader Reader(string copybook, byte[] data, ReadOptions? options = null) =>
        new(new MemoryStream(data), Copybook.Parse(copybook), options);

    public static CopybookReader AsciiReader(string copybook, string data, ReadOptions? options = null)
    {
        options ??= new ReadOptions();
        options.Encoding = Encoding.ASCII;
        return Reader(copybook, Encoding.ASCII.GetBytes(data), options);
    }
}
