using System.IO;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Ruse.App;

/// <summary>SHA-256 checks for downloaded model files.</summary>
internal static class ModelIntegrity
{
    /// <summary>Returns a lowercase SHA-256 from a bare hash or an HTTP ETag, or null.</summary>
    public static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string hash = value.Trim().Trim('"');
        if (hash.StartsWith("W/", StringComparison.Ordinal))
        {
            hash = hash[2..].Trim('"');
        }

        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            return null;
        }

        return hash.ToLowerInvariant();
    }

    public static string? FromETag(EntityTagHeaderValue? etag) => NormalizeSha256(etag?.Tag);

    public static string Finish(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    public static void Verify(string actual, string? expected, string name)
    {
        if (!string.IsNullOrEmpty(expected) &&
            !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The downloaded {name} failed its SHA-256 check. Delete it and try again.");
        }
    }
}
