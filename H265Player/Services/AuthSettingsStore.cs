using System.Text.Json;
using H265Player.Models;
using Microsoft.AspNetCore.DataProtection;

namespace H265Player.Services;

public sealed class AuthSettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly IDataProtector _protector;
    private AuthSettings _settings;

    public AuthSettingsStore(IHostEnvironment environment, IDataProtectionProvider dataProtectionProvider)
    {
        _path = AppPaths.File("auth-settings.json");
        _protector = dataProtectionProvider.CreateProtector("H265Player.AuthSettings.v1");
        _settings = Load();
    }

    public AuthSettings Get()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    public IReadOnlyList<AuthAccountSummary> GetSummaries()
    {
        lock (_gate)
        {
            return _settings.Accounts
                .Select(account => new AuthAccountSummary(account.Email, account.UpdatedAt))
                .OrderBy(account => account.Email, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public async Task SaveAsync(AuthSettings settings, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _settings = Normalize(settings);
        }

        await PersistAsync(cancellationToken);
    }

    public async Task UpsertAsync(AuthAccount account, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var normalized = NormalizeAccount(account);
            var accounts = _settings.Accounts
                .Where(existing => !string.Equals(existing.Email, normalized.Email, StringComparison.OrdinalIgnoreCase))
                .Concat([normalized])
                .OrderBy(existing => existing.Email, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _settings = new AuthSettings(accounts);
        }

        await PersistAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string email, CancellationToken cancellationToken = default)
    {
        var removed = false;
        lock (_gate)
        {
            var accounts = _settings.Accounts
                .Where(account => !string.Equals(account.Email, email.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            removed = accounts.Length != _settings.Accounts.Count;
            if (removed)
            {
                _settings = new AuthSettings(accounts);
            }
        }

        if (removed)
        {
            await PersistAsync(cancellationToken);
        }

        return removed;
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        PersistedAuthSettings persisted;
        lock (_gate)
        {
            persisted = new PersistedAuthSettings
            {
                Accounts = _settings.Accounts
                    .Select(account => new PersistedAuthAccount
                    {
                        Email = account.Email,
                        ProtectedSecretKey = _protector.Protect(account.SecretKey),
                        UpdatedAt = account.UpdatedAt
                    })
                    .ToList()
            };
        }

        var json = JsonSerializer.Serialize(persisted, _jsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    private AuthSettings Load()
    {
        if (!File.Exists(_path))
        {
            return AuthSettings.Empty;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<PersistedAuthSettings>(File.ReadAllText(_path));
            if (loaded is null)
            {
                return AuthSettings.Empty;
            }

            if (loaded.Accounts is { Count: > 0 })
            {
                var accounts = loaded.Accounts
                    .Select(ToAccount)
                    .Where(account => account is not null)
                    .Cast<AuthAccount>()
                    .ToArray();
                return new AuthSettings(accounts);
            }

            if (!string.IsNullOrWhiteSpace(loaded.Email))
            {
                var account = ToAccount(new PersistedAuthAccount
                {
                    Email = loaded.Email,
                    ProtectedSecretKey = loaded.ProtectedSecretKey,
                    SecretKey = loaded.SecretKey,
                    UpdatedAt = loaded.UpdatedAt
                });
                if (account is not null)
                {
                    return new AuthSettings([account]);
                }
            }

            return AuthSettings.Empty;
        }
        catch
        {
            return AuthSettings.Empty;
        }
    }

    private AuthSettings Normalize(AuthSettings settings)
    {
        var accounts = settings.Accounts
            .Select(NormalizeAccount)
            .Where(account => !string.IsNullOrWhiteSpace(account.Email) && !string.IsNullOrWhiteSpace(account.SecretKey))
            .GroupBy(account => account.Email, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(account => account.UpdatedAt ?? DateTimeOffset.MinValue).First())
            .OrderBy(account => account.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new AuthSettings(accounts);
    }

    private static AuthAccount NormalizeAccount(AuthAccount account) =>
        new(account.Email.Trim(), account.SecretKey.Trim(), account.UpdatedAt ?? DateTimeOffset.UtcNow);

    private AuthAccount? ToAccount(PersistedAuthAccount persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted.Email))
        {
            return null;
        }

        var secret = string.Empty;
        if (!string.IsNullOrWhiteSpace(persisted.ProtectedSecretKey))
        {
            secret = _protector.Unprotect(persisted.ProtectedSecretKey);
        }
        else if (!string.IsNullOrWhiteSpace(persisted.SecretKey))
        {
            secret = persisted.SecretKey;
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return new AuthAccount(persisted.Email.Trim(), secret, persisted.UpdatedAt);
    }

    private sealed class PersistedAuthSettings
    {
        public string? Email { get; set; }
        public string? ProtectedSecretKey { get; set; }
        public string? SecretKey { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public List<PersistedAuthAccount>? Accounts { get; set; }
    }

    private sealed class PersistedAuthAccount
    {
        public string? Email { get; set; }
        public string? ProtectedSecretKey { get; set; }
        public string? SecretKey { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
