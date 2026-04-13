namespace H265Player.Models;

public sealed record AuthAccount(
    string Email,
    string SecretKey,
    DateTimeOffset? UpdatedAt);

public sealed record AuthSettings(IReadOnlyList<AuthAccount> Accounts)
{
    public static AuthSettings Empty { get; } = new(Array.Empty<AuthAccount>());

    public bool IsConfigured => Accounts.Count > 0;

    public AuthAccount? FindAccount(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return Accounts.FirstOrDefault(account =>
            string.Equals(account.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record AuthStatusResponse(
    bool TrustedNetwork,
    bool RequiresAuthentication,
    bool IsAuthenticated,
    bool AuthenticatorConfigured,
    string? Email,
    string? EmailHint);

public sealed record AuthEnrollmentRequest(string Email);

public sealed record AuthEnrollmentResponse(
    bool Configured,
    string Email,
    string ManualKey,
    string OtpAuthUri,
    string QrCodeDataUrl);

public sealed record AuthLoginRequest(string Email, string Code);

public sealed record AuthAccountSummary(string Email, DateTimeOffset? UpdatedAt);
