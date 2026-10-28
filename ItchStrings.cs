/// Fixed itch.io strings and per-project file settings used by the licensing
/// scripts. The values live in the generated ItchSecrets.Local.cs, scrambled
/// with keys (and an unscramble function) that are random for every project,
/// so builds of different games using this tool don't share the same bytes.
/// Not encryption - just enough to stop quick searches and one-size-fits-all
/// cracking tools.
public static class ItchStrings
{
    public static string AuthUrl => ItchSecrets.Load().AuthUrl;
    public static string ApiBase => ItchSecrets.Load().ApiBase;
    public static string GamesPath => ItchSecrets.Load().GamesPath;
    public static string OwnershipPath => ItchSecrets.Load().OwnershipPath;
    public static string ProfileGamesPath => ItchSecrets.Load().ProfileGamesPath;
    public static string ScopeOwnership => ItchSecrets.Load().ScopeOwnership;
    public static string ScopeProfileGames => ItchSecrets.Load().ScopeProfileGames;

    public static int Port => ItchSecrets.Load().Port;
    public static string FileName => ItchSecrets.Load().FileName;
    public static string SaltPrefix => ItchSecrets.Load().SaltPrefix;
    public static byte[] FileMagic => ItchSecrets.Load().FileMagic;
}
