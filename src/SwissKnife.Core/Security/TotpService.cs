using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;

namespace SwissKnife.Core.Security;

public sealed record TotpEnrollment(string SecretBase32, string ProtectedSecret, IReadOnlyList<string> RecoveryCodes, string ProtectedRecoveryCodes);

/// <summary>API-013: MFA por TOTP (RFC 6238) para administradores locais, sem dependência de provedor externo.</summary>
public sealed class TotpService(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("SwissKnife.Mfa.v1");

    public TotpEnrollment GenerateEnrollment()
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secretBase32 = Base32Encoding.ToString(secretBytes);
        var recoveryCodes = Enumerable.Range(0, 8)
            .Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant())
            .ToArray();

        return new TotpEnrollment(
            secretBase32,
            _protector.Protect(secretBase32),
            recoveryCodes,
            _protector.Protect(string.Join(",", recoveryCodes)));
    }

    public bool VerifyCode(string protectedSecret, string code)
    {
        var secret = _protector.Unprotect(protectedSecret);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
    }

    public bool VerifyRecoveryCode(string protectedRecoveryCodes, string code, out string remainingProtectedCodes)
    {
        var codes = _protector.Unprotect(protectedRecoveryCodes).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var remaining = codes.Where(c => !c.Equals(code, StringComparison.OrdinalIgnoreCase)).ToArray();
        remainingProtectedCodes = _protector.Protect(string.Join(",", remaining));
        return remaining.Length < codes.Length;
    }

    public string BuildOtpAuthUri(string secretBase32, string accountLabel) =>
        $"otpauth://totp/SwissKnife:{Uri.EscapeDataString(accountLabel)}?secret={secretBase32}&issuer=SwissKnife&digits=6&period=30";
}
