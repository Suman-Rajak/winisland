using Windows.Security.Credentials;

namespace WinIsland.Services;

/// <summary>
/// Secrets (API keys) in Windows' credential locker rather than in settings.json: encrypted with your
/// Windows sign-in, and only readable as you.
/// </summary>
internal static class SecretStore
{
    private const string Resource = Product.Name;

    public static string? Get(string name)
    {
        if (Read(Resource, name) is { } secret)
        {
            return secret;
        }

        // Saved under the app's former name: bring it across. (Only the plain build can see those; an
        // installed app's locker is its own, so a renamed package starts without its keys.)
        if (Read(Product.FormerName, name) is { } former)
        {
            Set(name, former);
            return former;
        }
        return null;
    }

    private static string? Read(string resource, string name)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(resource, name);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            return null; // not stored (the vault throws for that)
        }
    }

    /// <summary>Stores a secret, or removes it when empty.</summary>
    public static void Set(string name, string? value)
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(Resource, name));
        }
        catch
        {
            // wasn't there
        }
        if (!string.IsNullOrWhiteSpace(value))
        {
            vault.Add(new PasswordCredential(Resource, name, value.Trim()));
        }
    }
}
