namespace AssetProvenanceHelper.Services;

public interface ISecretStore
{
    string? LoadSecret(string name);
    void SaveSecret(string name, string secret);
    void DeleteSecret(string name);
}
