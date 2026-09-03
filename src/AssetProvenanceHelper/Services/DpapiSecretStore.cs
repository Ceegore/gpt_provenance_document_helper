using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssetProvenanceHelper.Services;

public sealed class DpapiSecretStore : ISecretStore
{
    public static Func<bool>? IsWindowsPlatformProviderForTests { get; set; }

    private readonly string _storagePath;
    private readonly object _lock = new();

    public DpapiSecretStore(string? storagePath = null)
    {
        var isWindows = IsWindowsPlatformProviderForTests?.Invoke()
            ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (!isWindows)
        {
            throw new PlatformNotSupportedException("DPAPI secret store is only supported on Windows.");
        }

        if (string.IsNullOrWhiteSpace(storagePath))
        {
            _storagePath = Path.Combine(AppBootstrap.GetStateDirectory(), "secrets.dat");
        }
        else
        {
            _storagePath = Path.GetFullPath(storagePath);
        }
    }

    public string StoragePath => _storagePath;

    public string? LoadSecret(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_lock)
        {
            var dictionary = LoadEncryptedDictionary();
            return dictionary.TryGetValue(name, out var secret) ? secret : null;
        }
    }

    public void SaveSecret(string name, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(secret);

        lock (_lock)
        {
            var dictionary = LoadEncryptedDictionary();
            dictionary[name] = secret;
            SaveEncryptedDictionary(dictionary);
        }
    }

    public void DeleteSecret(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_lock)
        {
            var dictionary = LoadEncryptedDictionary();
            if (dictionary.Remove(name))
            {
                SaveEncryptedDictionary(dictionary);
            }
        }
    }

    private Dictionary<string, string> LoadEncryptedDictionary()
    {
        if (!File.Exists(_storagePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var encryptedBytes = File.ReadAllBytes(_storagePath);
            if (encryptedBytes.Length == 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            var json = Encoding.UTF8.GetString(decryptedBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveEncryptedDictionary(Dictionary<string, string> dictionary)
    {
        var dir = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(dictionary);
        var plainBytes = Encoding.UTF8.GetBytes(json);

        var encryptedBytes = ProtectedData.Protect(
            plainBytes,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        var tempPath = _storagePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(encryptedBytes);
                stream.Flush(true);
            }

            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Preserve original
            }
            throw;
        }
    }
}
