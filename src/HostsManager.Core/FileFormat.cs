using System.Text;

namespace HostsManager.Core;

/// <summary>
/// The byte-level shape of a hosts file: its text encoding, whether it carries a UTF-8
/// BOM, and which newline sequence dominates. All three are preserved verbatim across a
/// save, because normalizing any of them would rewrite lines the user never touched.
/// </summary>
/// <param name="IsUtf8">
/// False when the file is not valid UTF-8 and was read as Latin-1 instead. Every byte
/// 0x00–0xFF maps to exactly one Latin-1 character and back, so such a file still
/// round-trips byte for byte rather than being mangled into replacement characters.
/// </param>
public sealed record FileFormat(bool HasBom, string NewLine, bool IsUtf8 = true)
{
    public static readonly FileFormat Default = new(false, "\r\n");

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    public static bool StartsWithBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];

    /// <summary>
    /// Reads raw bytes and returns the decoded text plus the detected format.
    /// <para>
    /// UTF-8 is tried strictly. A lenient decoder would turn any stray byte from an
    /// ANSI-era editor into U+FFFD, and writing that back would silently corrupt a line
    /// the user never edited — a corruption no later check could catch, because every
    /// subsequent step works on the decoded text rather than the original bytes.
    /// </para>
    /// </summary>
    public static (string Text, FileFormat Format) Decode(byte[] bytes)
    {
        var hasBom = StartsWithBom(bytes);
        var offset = hasBom ? 3 : 0;
        var count = bytes.Length - offset;

        string text;
        bool isUtf8;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, count);
            isUtf8 = true;
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes, offset, count);
            isUtf8 = false;
        }

        return (text, new FileFormat(hasBom, DominantNewLine(text), isUtf8));
    }

    /// <summary>Encodes text back to bytes using the same codec and BOM the file was read with.</summary>
    public byte[] Encode(string text)
    {
        var body = IsUtf8
            ? new UTF8Encoding(false).GetBytes(text)
            : Encoding.Latin1.GetBytes(text);

        if (!HasBom) return body;

        var result = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(result, 0);
        body.CopyTo(result, Utf8Bom.Length);
        return result;
    }

    /// <summary>
    /// True if encoding this text and reading it back yields the same text. Latin-1
    /// covers only U+0000–U+00FF, so a character typed into a comment that falls outside
    /// that range would be written as '?' — this is the gate that catches it.
    /// </summary>
    public bool CanRoundTrip(string text) => Decode(Encode(text)).Text == text;

    private static string DominantNewLine(string text)
    {
        int crlf = 0, bareLf = 0, bareCr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else bareCr++;
            }
            else if (text[i] == '\n') bareLf++;
        }

        if (crlf >= bareLf && crlf >= bareCr) return "\r\n";
        return bareLf >= bareCr ? "\n" : "\r";
    }

    public string Describe()
    {
        var encoding = IsUtf8 ? (HasBom ? "UTF-8 BOM" : "UTF-8") : "Latin-1";
        var newLine = NewLine switch
        {
            "\r\n" => "CRLF",
            "\n" => "LF",
            _ => "CR",
        };

        return $"{encoding} · {newLine}";
    }
}
