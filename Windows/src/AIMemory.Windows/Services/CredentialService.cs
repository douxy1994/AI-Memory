using Windows.Security.Credentials;

namespace AIMemory.Windows.Services;

public sealed class CredentialService
{
    private const string Resource = "com.aimemory.windows.webdav";
    private readonly PasswordVault _vault = new();

    public void Save(string username, string password)
    {
        RemoveAll();
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
        {
            _vault.Add(new PasswordCredential(Resource, username, password));
        }
    }

    public (string Username, string Password)? Load()
    {
        var credential = _vault.RetrieveAll()
            .FirstOrDefault(value => value.Resource == Resource);
        if (credential is null)
        {
            return null;
        }
        credential.RetrievePassword();
        return (credential.UserName, credential.Password);
    }

    private void RemoveAll()
    {
        foreach (var credential in _vault.RetrieveAll()
                     .Where(value => value.Resource == Resource))
        {
            _vault.Remove(credential);
        }
    }
}
