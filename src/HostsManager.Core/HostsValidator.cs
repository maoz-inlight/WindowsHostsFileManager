using System.Net;
using System.Net.Sockets;

namespace HostsManager.Core;

public sealed record ValidationResult(bool IsValid, string? Error)
{
    public static readonly ValidationResult Ok = new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

public static class HostsValidator
{
    public const int MaxHostnameLength = 253;
    public const int MaxLabelLength = 63;

    /// <summary>
    /// Validates an IP literal. <see cref="IPAddress.TryParse"/> alone is too lax — it
    /// accepts "1" as 0.0.0.1 — so IPv4 additionally has to be a full dotted quad.
    /// </summary>
    public static ValidationResult ValidateIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return ValidationResult.Fail("Enter an IP address.");

        if (!IPAddress.TryParse(ip, out var parsed))
            return ValidationResult.Fail($"'{ip}' is not a valid IP address.");

        if (parsed.AddressFamily == AddressFamily.InterNetwork && ip.Split('.').Length != 4)
            return ValidationResult.Fail($"'{ip}' is not a complete IPv4 address.");

        return ValidationResult.Ok;
    }

    /// <summary>
    /// Validates a hostname. The safety-critical rejections are whitespace, '#' and
    /// control characters — any of those would break the line into something the
    /// resolver reads differently than the user intended.
    /// </summary>
    public static ValidationResult ValidateHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return ValidationResult.Fail("Enter a domain name.");

        if (hostname.Any(char.IsWhiteSpace))
            return ValidationResult.Fail("A domain name cannot contain spaces.");

        if (hostname.Contains('#'))
            return ValidationResult.Fail("A domain name cannot contain '#'.");

        if (hostname.Any(char.IsControl))
            return ValidationResult.Fail("A domain name cannot contain control characters.");

        // A single trailing dot is legal (fully-qualified form) and is used by Tailscale.
        var name = hostname.EndsWith('.') ? hostname[..^1] : hostname;

        if (name.Length == 0)
            return ValidationResult.Fail("Enter a domain name.");

        if (name.Length > MaxHostnameLength)
            return ValidationResult.Fail($"A domain name cannot exceed {MaxHostnameLength} characters.");

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0)
                return ValidationResult.Fail($"'{hostname}' has an empty part between dots.");

            if (label.Length > MaxLabelLength)
                return ValidationResult.Fail($"'{label}' exceeds {MaxLabelLength} characters.");

            if (!IsLabelBoundary(label[0]) || !IsLabelBoundary(label[^1]))
                return ValidationResult.Fail($"'{label}' must start and end with a letter or digit.");

            if (!label.All(IsLabelChar))
                return ValidationResult.Fail($"'{label}' contains characters that are not allowed in a domain name.");
        }

        return ValidationResult.Ok;
    }

    private static bool IsLabelBoundary(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsLabelChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';

    /// <summary>
    /// Rejects anything that would corrupt the file structure if written into an
    /// inline comment: newlines break one line into two.
    /// </summary>
    public static ValidationResult ValidateComment(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return ValidationResult.Ok;

        return comment.Any(c => c == '\r' || c == '\n')
            ? ValidationResult.Fail("A comment cannot span multiple lines.")
            : ValidationResult.Ok;
    }
}
