using System.IO.Hashing;

namespace CodeCompass.Core.Text;

/// <summary>
/// Content hashing for change detection. Uses XxHash128 (not a cryptographic hash): change
/// detection only needs to tell "same bytes" from "different bytes" for the local index, and
/// xxHash is ~10x faster than SHA-256 (it competes with trigram/symbol work for cores during
/// a build) and half the size - 16 bytes / 32 hex chars instead of 32 / 64 - which halves the
/// per-file cost of the snapshot ledger.
/// </summary>
public static class ContentHasher
{
    /// <summary>Hex (uppercase) XxHash128 of the bytes - 32 characters.</summary>
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(XxHash128.Hash(data));
}
