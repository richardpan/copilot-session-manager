using System.Security.Cryptography;
using System.Text;
using CopilotSessionManager.Core.Security;
using FluentAssertions;
using DataProtectionScope = CopilotSessionManager.Core.Security.DataProtectionScope;

namespace CopilotSessionManager.Core.Tests.Security;

public class DpapiDataProtectorTests
{
    private const string Purpose = "CopilotSessionManager.Tests.PurposeA";
    private const string OtherPurpose = "CopilotSessionManager.Tests.PurposeB";

    [Fact]
    public void Constructor_rejects_null_purpose()
    {
        var act = () => new DpapiDataProtector(null!);
        act.Should().Throw<ArgumentException>().WithParameterName("purpose");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Constructor_rejects_empty_or_whitespace_purpose(string purpose)
    {
        var act = () => new DpapiDataProtector(purpose);
        act.Should().Throw<ArgumentException>().WithParameterName("purpose");
    }

    [Fact]
    public void Protect_throws_on_null_plaintext()
    {
        // Construction is allowed everywhere; the null check fires before the
        // platform check, so this assertion is meaningful on every OS.
        var protector = new DpapiDataProtector(Purpose);

        var act = () => protector.Protect(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("plaintext");
    }

    [Fact]
    public void Unprotect_throws_on_null_ciphertext()
    {
        var protector = new DpapiDataProtector(Purpose);

        var act = () => protector.Unprotect(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("ciphertext");
    }

    [Fact]
    public void Protect_throws_PlatformNotSupported_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiDataProtector(Purpose);

        var act = () => protector.Protect(new byte[] { 1, 2, 3 });

        act.Should().Throw<PlatformNotSupportedException>();
    }

    [Fact]
    public void Roundtrip_returns_original_bytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiDataProtector(Purpose);
        var plaintext = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");

        var ciphertext = protector.Protect(plaintext);
        var roundtripped = protector.Unprotect(ciphertext);

        ciphertext.Should().NotBeEmpty();
        ciphertext.Should().NotEqual(plaintext, "the protected blob must differ from the plaintext");
        roundtripped.Should().Equal(plaintext);
    }

    [Fact]
    public void Roundtrip_supports_empty_array()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiDataProtector(Purpose);

        var ciphertext = protector.Protect(Array.Empty<byte>());
        var roundtripped = protector.Unprotect(ciphertext);

        roundtripped.Should().NotBeNull();
        roundtripped.Should().BeEmpty();
    }

    [Fact]
    public void Different_purposes_cannot_decrypt_each_other()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var alice = new DpapiDataProtector(Purpose);
        var bob = new DpapiDataProtector(OtherPurpose);
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var ciphertext = alice.Protect(plaintext);

        var act = () => bob.Unprotect(ciphertext);

        act.Should().Throw<CryptographicException>(
            "DPAPI's entropy parameter must isolate purposes from each other");
    }

    [Fact]
    public void Tampered_ciphertext_throws_CryptographicException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiDataProtector(Purpose);
        var ciphertext = protector.Protect(new byte[] { 1, 2, 3, 4, 5 });

        // Flip a byte deep enough to land inside the encrypted body, not the
        // DPAPI header (the very first bytes are a fixed magic that DPAPI
        // validates separately and would surface a different error path).
        var tampered = (byte[])ciphertext.Clone();
        var flipIndex = tampered.Length - 5;
        tampered[flipIndex] ^= 0xFF;

        var act = () => protector.Unprotect(tampered);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Local_machine_scope_roundtrips_successfully()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var machine = new DpapiDataProtector(Purpose, DataProtectionScope.LocalMachine);
        var plaintext = new byte[] { 9, 8, 7, 6, 5 };

        var ciphertext = machine.Protect(plaintext);

        machine.Unprotect(ciphertext).Should().Equal(plaintext);
    }
}
