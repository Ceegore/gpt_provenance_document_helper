using AssetProvenanceHelper.Services;

namespace AssetProvenanceHelper.Tests;

public sealed class DpapiSecretStorePlatformTests
{
    [Fact]
    public void DpapiSecretStore_NonWindows_ThrowsPlatformNotSupported()
    {
        try
        {
            // Simulate running on non-Windows (Linux / macOS)
            DpapiSecretStore.IsWindowsPlatformProviderForTests = () => false;

            var ex = Assert.Throws<PlatformNotSupportedException>(() =>
            {
                _ = new DpapiSecretStore("test_path");
            });

            Assert.Contains("Windows", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DpapiSecretStore.IsWindowsPlatformProviderForTests = null;
        }
    }
}
