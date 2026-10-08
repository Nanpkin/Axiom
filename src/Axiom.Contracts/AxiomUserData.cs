namespace Axiom.Contracts;

public static class AxiomUserData
{
    // AppData can be redirected to a packaged parent application's private cache.
    // A per-user profile directory gives desktop and development launches one store.
    public static string Root
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile))
                throw new InvalidOperationException("无法确认当前用户的数据目录。");
            return Path.Combine(profile, ".axiom");
        }
    }
}
