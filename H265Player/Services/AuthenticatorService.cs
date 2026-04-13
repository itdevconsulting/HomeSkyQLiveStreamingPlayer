using H265Player.Models;
using OtpNet;
using QRCoder;

namespace H265Player.Services;

public sealed class AuthenticatorService
{
    private const string Issuer = "H265Player";

    public AuthEnrollmentResponse CreateEnrollment(string email)
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var manualKey = Base32Encoding.ToString(secretBytes);
        var otpAuthUri = BuildOtpAuthUri(email, manualKey);
        return new AuthEnrollmentResponse(
            Configured: true,
            Email: email,
            ManualKey: manualKey,
            OtpAuthUri: otpAuthUri,
            QrCodeDataUrl: BuildQrCodeDataUrl(otpAuthUri));
    }

    public bool VerifyCode(AuthAccount account, string code)
    {
        if (string.IsNullOrWhiteSpace(account.Email) || string.IsNullOrWhiteSpace(account.SecretKey) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            var secretBytes = Base32Encoding.ToBytes(account.SecretKey);
            var totp = new Totp(secretBytes);
            return totp.VerifyTotp(code.Replace(" ", string.Empty), out _, new VerificationWindow(previous: 1, future: 1));
        }
        catch
        {
            return false;
        }
    }

    private static string BuildOtpAuthUri(string email, string manualKey)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{email}");
        var issuer = Uri.EscapeDataString(Issuer);
        var secret = Uri.EscapeDataString(manualKey);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6";
    }

    private static string BuildQrCodeDataUrl(string content)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrData);
        var bytes = qrCode.GetGraphic(12);
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }
}
