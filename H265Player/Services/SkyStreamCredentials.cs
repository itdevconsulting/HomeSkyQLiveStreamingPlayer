using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text.RegularExpressions;

namespace H265Player.Services;

internal static class SkyStreamCredentials
{
    public const string ServerName = "sky.xcal.tv";
    public const int Port = 8091;
    public const string AuthSalt = "biT43y";

    private static readonly Regex CertBlock = new(
        "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Lazy<Loaded> Value = new(Load);

    public static string FingerprintHex => Value.Value.FingerprintHex;

    public static SslStreamCertificateContext CertificateContext => Value.Value.Context;

    public static string ComputeAuthToken(string pairingCode, string controllerNonce, string stbNonce)
    {
        var stage1 = SHA256.HashData(
            Convert.FromHexString(FingerprintHex)
                .Concat(System.Text.Encoding.UTF8.GetBytes(pairingCode))
                .Concat(System.Text.Encoding.UTF8.GetBytes(controllerNonce))
                .ToArray());

        var stage2 = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(stbNonce)
                .Concat(stage1)
                .Concat(System.Text.Encoding.UTF8.GetBytes(AuthSalt))
                .ToArray());

        return Convert.ToBase64String(stage2);
    }

    private static Loaded Load()
    {
        var certPem = ReadResource("sky-stream-client.pem");
        var keyPem = ReadResource("sky-stream-client.key");
        var blocks = CertBlock.Matches(certPem).Select(match => match.Value).ToList();
        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("Sky Stream client certificate chain is missing.");
        }

        using var leaf = X509Certificate2.CreateFromPem(blocks[0], keyPem);
        var usable = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pfx), (string?)null);
        var extras = new X509Certificate2Collection();
        foreach (var block in blocks.Skip(1))
        {
            extras.Add(X509Certificate2.CreateFromPem(block));
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(usable.RawData)).ToLowerInvariant();
        var context = SslStreamCertificateContext.Create(usable, extras);
        return new Loaded(fingerprint, context);
    }

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(SkyStreamCredentials).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"Embedded Sky Stream credential '{fileName}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Sky Stream credential '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Loaded(string FingerprintHex, SslStreamCertificateContext Context);
}
