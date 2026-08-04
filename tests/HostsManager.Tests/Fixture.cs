using HostsManager.Core;

namespace HostsManager.Tests;

/// <summary>
/// The real hosts file from the machine this was built for, warts and all: a UTF-8 BOM,
/// CRLF endings, Microsoft's documentation header, a block of disabled entries, a
/// paragraph of chat text pasted in by accident, and Docker and Tailscale blocks.
/// Every guarantee is tested against it rather than a tidy synthetic sample.
/// </summary>
public static class Fixture
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-hosts.txt");

    public static byte[] Bytes => File.ReadAllBytes(Path);

    public static (string Text, FileFormat Format) Decoded => FileFormat.Decode(Bytes);

    public static HostsDocument Parse()
    {
        var (text, format) = Decoded;
        return HostsFileParser.Parse(text, format);
    }

    /// <summary>Copies the fixture into a throwaway directory so writer tests never touch the real file.</summary>
    public static string CopyToTemp(out string directory)
    {
        directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HostsManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var target = System.IO.Path.Combine(directory, "hosts");
        File.WriteAllBytes(target, Bytes);
        return target;
    }
}
