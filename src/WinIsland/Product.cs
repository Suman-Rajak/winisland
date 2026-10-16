namespace WinIsland;

/// <summary>
/// The product's name, in one place (the assistant's is <see cref="Services.Assistant.Name"/>). Shown in
/// Settings, the tray and logs; the installed package's name is in Package.appxmanifest. The code's own
/// namespace is still WinIsland, the project's working name.
/// </summary>
internal static class Product
{
    public const string Name = "Glyde";

    /// <summary>The name the app went by before; its data is moved across once.</summary>
    public const string FormerName = "WinIsland";
}
