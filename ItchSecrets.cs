// Public part of the per-project settings. Safe to commit: it contains no values.
//
// Every value that is secret or specific to one game (Client ID, Game ID,
// license secret, offline days, port, license file name, salt, file header)
// is written by Tools > itch.io Licensing > Generate Secrets into
// ItchSecrets.Local.cs, next to this file. That file is git-ignored and must
// never be committed or shared. Without it the project still compiles, the
// game reports that the secrets are not set, and no port is ever opened.

public static partial class ItchSecrets
{
    /// All per-project values. Filled only by the generated ItchSecrets.Local.cs.
    public sealed class LocalValues
    {
        public string ClientId = "";
        public string GameId = "";
        public string AppSecret = "";
        public int OfflineDays;

        public int Port;
        public string FileName = "";
        public string SaltPrefix = "";
        public byte[] FileMagic = new byte[0];

        public string AuthUrl = "";
        public string ApiBase = "";
        public string GamesPath = "";
        public string OwnershipPath = "";
        public string ProfileGamesPath = "";
        public string ScopeOwnership = "";
        public string ScopeProfileGames = "";
    }

    public const int FileMagicLength = 4;

    // Implemented only in the generated ItchSecrets.Local.cs. When that file is
    // missing, the compiler removes the call and every value stays empty.
    static partial void LoadLocal(LocalValues values);

    /// Unscrambles a fresh copy of all values. Cheap; nothing is kept in memory.
    public static LocalValues Load()
    {
        var values = new LocalValues();
        LoadLocal(values);
        return values;
    }

    public static bool IsConfigured
    {
        get
        {
            LocalValues v = Load();
            return
                !string.IsNullOrEmpty(v.ClientId) &&
                !string.IsNullOrEmpty(v.GameId) &&
                !string.IsNullOrEmpty(v.AppSecret) &&
                v.Port > 0 && v.Port <= 65535 &&
                !string.IsNullOrEmpty(v.FileName) &&
                !string.IsNullOrEmpty(v.SaltPrefix) &&
                v.FileMagic != null && v.FileMagic.Length == FileMagicLength &&
                !string.IsNullOrEmpty(v.AuthUrl) &&
                !string.IsNullOrEmpty(v.ApiBase);
        }
    }

    public static string ClientId => Load().ClientId ?? "";
    public static string GameId => Load().GameId ?? "";
    public static string AppSecret => Load().AppSecret ?? "";
}
