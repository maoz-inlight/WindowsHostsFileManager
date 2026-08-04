namespace HostsManager.Core;

public sealed class HostsVerificationException : Exception
{
    public HostsVerificationException(string message) : base(message) { }
}

/// <summary>
/// The gate that stands between the line model and the disk.
/// <para>
/// Rendering is the only step that can turn a correct model into a malformed file, so
/// the rendered text is parsed back and compared against the model it came from. A
/// render bug therefore fails here, in memory, instead of reaching the hosts file.
/// </para>
/// </summary>
public static class HostsFileVerifier
{
    public static void Verify(HostsDocument intended, string rendered)
    {
        VerifyStructure(intended, rendered);
        VerifyRoundTrip(intended, rendered);
        VerifyManagedSectionsUntouched(intended);
    }

    private static void VerifyStructure(HostsDocument intended, string rendered)
    {
        if (rendered.Length == 0)
            throw new HostsVerificationException("Refusing to write an empty hosts file.");

        var nul = rendered.IndexOf('\0');
        if (nul >= 0)
            throw new HostsVerificationException($"Rendered file contains a null byte at offset {nul}.");

        var originalEndsWithNewline = EndsWithNewline(intended.OriginalText);
        if (originalEndsWithNewline && !EndsWithNewline(rendered))
            throw new HostsVerificationException("Rendered file lost its trailing newline.");
    }

    private static void VerifyRoundTrip(HostsDocument intended, string rendered)
    {
        // Re-parse with the original header length so classification is deterministic:
        // this compares the render, not the heuristic.
        var reparsed = HostsFileParser.Parse(rendered, intended.Format, leadingDocLines: intended.LeadingDocLines);

        if (reparsed.Lines.Count != intended.Lines.Count)
            throw new HostsVerificationException(
                $"Line count changed during render: expected {intended.Lines.Count}, got {reparsed.Lines.Count}.");

        for (var i = 0; i < intended.Lines.Count; i++)
        {
            var a = intended.Lines[i];
            var b = reparsed.Lines[i];

            if (a.Render() != b.Render())
                throw new HostsVerificationException(
                    $"Line {i + 1} did not survive the round trip.{Environment.NewLine}" +
                    $"  expected: {Quote(a.Render())}{Environment.NewLine}" +
                    $"  actual:   {Quote(b.Render())}");

            if (a.Kind != b.Kind)
                throw new HostsVerificationException(
                    $"Line {i + 1} changed meaning: expected {a.Kind}, got {b.Kind} — {Quote(a.Render())}");

            if (a.Terminator != b.Terminator)
                throw new HostsVerificationException($"Line {i + 1} changed its line ending.");

            if (a.ManagedBy != b.ManagedBy)
                throw new HostsVerificationException(
                    $"Line {i + 1} changed ownership: expected {a.ManagedBy ?? "none"}, got {b.ManagedBy ?? "none"}.");
        }
    }

    /// <summary>
    /// Docker and Tailscale rewrite their own blocks. Editing them here would either be
    /// clobbered or clobber them, so the model refuses the mutation and this confirms it.
    /// </summary>
    private static void VerifyManagedSectionsUntouched(HostsDocument intended)
    {
        var touched = intended.Lines.FirstOrDefault(l => l.IsReadOnly && l.IsModified);
        if (touched is not null)
            throw new HostsVerificationException(
                $"Line {touched.LineNumber} belongs to {touched.ManagedBy} and must not be modified.");
    }

    private static bool EndsWithNewline(string text) =>
        text.EndsWith('\n') || text.EndsWith('\r');

    private static string Quote(string value) =>
        "\"" + value.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
