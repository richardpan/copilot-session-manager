using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CopilotSessionManager.Core.Security;

/// <summary>
/// <see cref="IDataProtector"/> backed by Windows DPAPI
/// (<see cref="ProtectedData"/>). The constructor's <c>purpose</c> string is
/// SHA-256 hashed and passed as DPAPI's optional entropy parameter so that
/// two protectors configured with different purposes cannot decrypt each
/// other's ciphertexts — even though they share the same underlying user
/// (or machine) key.
/// </summary>
/// <remarks>
/// DPAPI is Windows-only. Calls to <see cref="Protect"/> /
/// <see cref="Unprotect"/> on non-Windows platforms throw
/// <see cref="PlatformNotSupportedException"/> with a clear message rather
/// than crashing deep inside the BCL. Construction itself is allowed on any
/// platform so containers / tests can compose objects without conditional DI
/// wiring.
/// </remarks>
public sealed class DpapiDataProtector : IDataProtector
{
    private const string PlatformErrorMessage =
        "DpapiDataProtector requires Windows DPAPI; the current platform is not supported.";

    private readonly byte[] _entropy;
    private readonly DataProtectionScope _scope;

    /// <summary>
    /// Creates a protector bound to <paramref name="purpose"/> (used as
    /// DPAPI entropy after SHA-256 hashing) and <paramref name="scope"/>
    /// (defaults to <see cref="DataProtectionScope.CurrentUser"/>).
    /// </summary>
    /// <param name="purpose">
    /// Non-empty, non-whitespace logical purpose (e.g.
    /// <c>"CopilotSessionManager.AppDb.v1"</c>). Two protectors with
    /// different purposes produce mutually unreadable ciphertexts.
    /// </param>
    /// <param name="scope">
    /// Whether the payload should be bound to the current user or to the
    /// local machine. Defaults to per-user, which is what ADR-0004 requires.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="purpose"/> is <c>null</c>, empty, or whitespace.
    /// </exception>
    public DpapiDataProtector(
        string purpose,
        DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException(
                "Purpose must be a non-empty, non-whitespace string.",
                nameof(purpose));
        }

        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
        _scope = scope;
    }

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(PlatformErrorMessage);
        }

        return ProtectedData.Protect(plaintext, _entropy, MapScope(_scope));
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(PlatformErrorMessage);
        }

        return ProtectedData.Unprotect(ciphertext, _entropy, MapScope(_scope));
    }

    [SupportedOSPlatform("windows")]
    private static System.Security.Cryptography.DataProtectionScope MapScope(DataProtectionScope scope) =>
        scope switch
        {
            DataProtectionScope.CurrentUser =>
                System.Security.Cryptography.DataProtectionScope.CurrentUser,
            DataProtectionScope.LocalMachine =>
                System.Security.Cryptography.DataProtectionScope.LocalMachine,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope."),
        };
}
