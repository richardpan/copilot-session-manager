namespace CopilotSessionManager.Core.Security;

/// <summary>
/// Scope under which a <see cref="IDataProtector"/> binds protected payloads.
/// Mirrors the underlying platform key-protection scope (e.g. Windows DPAPI's
/// <c>DataProtectionScope</c>).
/// </summary>
public enum DataProtectionScope
{
    /// <summary>
    /// Payload can only be unprotected by the same Windows user account that
    /// protected it. Recommended for per-user secrets such as the app DB key.
    /// </summary>
    CurrentUser = 0,

    /// <summary>
    /// Payload can be unprotected by any account on the same machine.
    /// </summary>
    LocalMachine = 1,
}

/// <summary>
/// Symmetric protect/unprotect over opaque byte payloads. Implementations are
/// expected to be thread-safe and synchronous (the underlying platform calls
/// — e.g. DPAPI — are blocking but fast).
/// </summary>
/// <remarks>
/// This abstraction exists so consumers (e.g. the future SQLite app-DB key
/// loader described in ADR-0004) can depend on a small, testable surface
/// rather than the platform DPAPI APIs directly. Different consumers should
/// be wired with different "purpose" strings via the implementation's
/// constructor so their ciphertexts cannot decrypt each other.
/// </remarks>
public interface IDataProtector
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns an opaque ciphertext
    /// blob. Empty input is supported and roundtrips faithfully.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plaintext"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The underlying platform refused to protect the payload.
    /// </exception>
    /// <exception cref="System.PlatformNotSupportedException">
    /// The current OS does not support this protector (e.g. DPAPI on
    /// non-Windows).
    /// </exception>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Decrypts a ciphertext previously returned by <see cref="Protect"/>
    /// using a protector configured with the same purpose and scope.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="ciphertext"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The ciphertext was tampered with, was produced under a different
    /// purpose/scope, or is otherwise undecipherable.
    /// </exception>
    /// <exception cref="System.PlatformNotSupportedException">
    /// The current OS does not support this protector (e.g. DPAPI on
    /// non-Windows).
    /// </exception>
    byte[] Unprotect(byte[] ciphertext);
}
