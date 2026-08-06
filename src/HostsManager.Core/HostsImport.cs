namespace HostsManager.Core;

/// <summary>An editable mapping read from another hosts file.</summary>
public sealed record HostsImportEntry(
    string Ip,
    IReadOnlyList<string> Hostnames,
    string? Comment,
    bool IsEnabled,
    string Body,
    string DisablePrefix,
    string? GroupName = null);

/// <summary>The importable portion of another hosts file, plus anything deliberately ignored.</summary>
public sealed record HostsImportFile(
    IReadOnlyList<HostsImportEntry> Entries,
    int UnparseableLineCount,
    int ManagedEntryCount);

/// <summary>Summary of a merge after duplicate hostnames have been removed.</summary>
public sealed record HostsMergeResult(
    int AddedEntries,
    int AddedHostnames,
    int SkippedDuplicateHostnames);

/// <summary>
/// Reads another hosts file without letting its byte format leak into the current file.
/// Only ordinary editable mappings are candidates: comments belong to the source file,
/// and Docker/Tailscale mappings remain owned by those tools rather than being copied as
/// user entries.
/// </summary>
public static class HostsImportReader
{
    public static HostsImportFile Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Hosts file not found at {path}.", path);

        return Parse(File.ReadAllBytes(path));
    }

    public static HostsImportFile Parse(byte[] bytes,
        IReadOnlyList<ManagedSectionMarker>? markers = null)
    {
        var (text, format) = FileFormat.Decode(bytes);
        var document = HostsFileParser.Parse(text, format, markers);

        var entries = document.Entries
            .Where(line => !line.IsReadOnly)
            .Select(line => new HostsImportEntry(
                line.Ip!,
                line.Hostnames.ToArray(),
                line.InlineComment,
                line.IsEnabled,
                line.Body,
                line.DisablePrefix,
                line.GroupName))
            .ToArray();

        return new HostsImportFile(
            entries,
            document.UnparseableLines.Count(),
            document.Entries.Count(line => line.IsReadOnly));
    }
}
