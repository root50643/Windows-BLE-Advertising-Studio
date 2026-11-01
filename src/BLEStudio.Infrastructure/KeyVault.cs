using System.Security.Cryptography;
using System.Text;

namespace BLEStudio.Infrastructure;

public sealed class KeyVault(string directory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BLEStudio/v1/authorized-research");
    public string Store(Guid profileId, string purpose, byte[] bytes)
    {
        Validate(purpose, bytes);
        Directory.CreateDirectory(directory);
        var envelope = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        var reference = $"{profileId:D}-{purpose}";
        File.WriteAllBytes(Path.Combine(directory, reference + ".dpapi"), envelope);
        return reference;
    }
    public byte[] Load(string reference)
    {
        CheckReference(reference);
        return ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(directory, reference + ".dpapi")), Entropy, DataProtectionScope.CurrentUser);
    }
    public void Delete(string reference) { CheckReference(reference); File.Delete(Path.Combine(directory, reference + ".dpapi")); }
    private static void CheckReference(string reference)
    {
        if (reference.Length < 38 || !Guid.TryParseExact(reference[..36], "D", out _) || reference[36] != '-' || reference[37..] is not ("apple-public" or "apple-private" or "google-eik"))
            throw new FormatException("Invalid key-vault reference.");
    }
    public static void Validate(string purpose, byte[] bytes)
    {
        if (purpose is "apple-public" or "apple-private" && bytes.Length == 28 || purpose == "google-eik" && bytes.Length == 32) return;
        throw new FormatException("Expected a 28-byte Apple test public key or a 32-byte Google secret EIK. No key bytes were logged.");
    }
}
