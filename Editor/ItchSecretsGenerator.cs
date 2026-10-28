#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;


/// Editor-only window that writes ItchSecrets.Local.cs: the Client ID, Game ID,
/// license secret and offline days, plus values randomized for each project
/// (login port, license file name, salt, file header, and the keys and
/// function used to scramble everything). The file is written next to
/// ItchSecrets.cs together with a .gitignore entry that keeps it out of git.
/// Lives in an Editor folder, so it is never included in builds.
public class ItchSecretsGenerator : EditorWindow
{
    private const string LocalFileName = "ItchSecrets.Local.cs";

    // Below the ephemeral port ranges of Windows (49152+) and Linux (32768+),
    // so the OS is unlikely to hand the port to another connection.
    private const int MinPort = 10000;
    private const int MaxPort = 32767;

    // Fixed itch.io values. These plain strings exist only in the Editor; in
    // builds they appear only inside the generated file, scrambled per project.
    private const string AuthUrl = "https://itch.io/user/oauth";
    private const string ApiBase = "https://api.itch.io";
    private const string GamesPath = "/games/";
    private const string OwnershipPath = "/ownership";
    private const string ProfileGamesPath = "/profile/games";
    private const string ScopeOwnership = "game:view:ownership";
    private const string ScopeProfileGames = "profile:games";

    private string clientId = "";
    private string gameId = "";
    private string appSecret = "";
    private int offlineDays = 30;
    private int port;
    private string fileName = "";
    private string saltPrefix = "";
    private byte[] fileMagic = new byte[0];

    private bool existingSetup;
    private int savedPort;
    private bool showAdvanced;

    [MenuItem("Tools/itch.io Licensing/Generate Secrets")]
    private static void Open()
    {
        var window = GetWindow<ItchSecretsGenerator>(true, "itch.io Secrets");
        window.minSize = new Vector2(520, 460);
        window.LoadCurrent();
    }

    /// Fills the window from the existing ItchSecrets.Local.cs, or, on first
    /// use, with freshly randomized values so nothing is left at a default.
    private void LoadCurrent()
    {
        existingSetup = ItchSecrets.IsConfigured;

        if (existingSetup)
        {
            ItchSecrets.LocalValues v = ItchSecrets.Load();
            clientId = v.ClientId;
            gameId = v.GameId;
            appSecret = v.AppSecret;
            offlineDays = v.OfflineDays;
            port = v.Port;
            fileName = v.FileName;
            saltPrefix = v.SaltPrefix;
            fileMagic = v.FileMagic;
            savedPort = v.Port;
        }
        else
        {
            appSecret = RandomSecret();
            port = RandomPort();
            RandomizeFileValues();
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Writes " + LocalFileName + " next to ItchSecrets.cs. It holds your secrets: never commit " +
            "or share it (a .gitignore entry is added for you). The license secret, port and file " +
            "settings were randomized for this project.",
            MessageType.Info);

        GUILayout.Space(6);
        EditorGUILayout.LabelField("1. Register this callback URL in your itch.io OAuth application",
            EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.SelectableLabel(CallbackUrl(port), EditorStyles.textField,
            GUILayout.Height(EditorGUIUtility.singleLineHeight));
        if (GUILayout.Button("Copy", GUILayout.Width(60)))
            EditorGUIUtility.systemCopyBuffer = CallbackUrl(port);
        EditorGUILayout.EndHorizontal();

        if (existingSetup && port != savedPort)
            EditorGUILayout.HelpBox(
                "The port changed. Update the callback URL on itch.io too, or login will fail.",
                MessageType.Warning);

        GUILayout.Space(6);
        EditorGUILayout.LabelField("2. Enter your itch.io details", EditorStyles.boldLabel);
        clientId = EditorGUILayout.TextField("OAuth Client ID", clientId).Trim();
        gameId = EditorGUILayout.TextField("Game ID (number)", gameId).Trim();

        EditorGUILayout.BeginHorizontal();
        appSecret = EditorGUILayout.TextField("License Secret", appSecret).Trim();
        if (GUILayout.Button("Random", GUILayout.Width(70)))
            appSecret = RandomSecret();
        EditorGUILayout.EndHorizontal();

        offlineDays = Mathf.Max(0, EditorGUILayout.IntField("Offline License Days", offlineDays));
        EditorGUILayout.HelpBox(
            offlineDays == 0
                ? "0 = the offline license never expires."
                : $"After {offlineDays} day(s) the player must log in with itch.io again. " +
                  "Each online login starts a new period.",
            MessageType.None);

        GUILayout.Space(6);
        showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced (randomized per project)", true);
        if (showAdvanced)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.BeginHorizontal();
            port = EditorGUILayout.IntField("Login Port", port);
            if (GUILayout.Button("Random", GUILayout.Width(70)))
                port = RandomPort();
            EditorGUILayout.EndHorizontal();

            fileName = EditorGUILayout.TextField("License File Name", fileName).Trim();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Salt / File Header", saltPrefix + " / " + ToHex(fileMagic));
            if (GUILayout.Button("Random", GUILayout.Width(70)))
                RandomizeFileValues();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Set these once before release. Changing the license secret, file name, salt or header " +
                "later makes every player log in online once more. Changing the port also needs the " +
                "new callback URL on itch.io.",
                MessageType.None);

            EditorGUI.indentLevel--;
        }

        GUILayout.Space(10);

        string problem = Problem();
        if (problem != null)
            EditorGUILayout.HelpBox(problem, MessageType.Warning);

        using (new EditorGUI.DisabledScope(problem != null))
        {
            if (GUILayout.Button("Generate", GUILayout.Height(30)))
                Generate();
        }
    }

    private string Problem()
    {
        if (clientId.Length == 0)
            return "Enter the OAuth Client ID.";
        if (!int.TryParse(gameId, out int id) || id <= 0)
            return "Enter the numeric Game ID.";
        if (appSecret.Length < 24)
            return "The license secret must be at least 24 characters. Click Random.";
        if (port < 1024 || port > 65535)
            return "The login port must be between 1024 and 65535.";
        if (!IsSafeFileName(fileName))
            return "The license file name may only use letters, digits, '.', '-' and '_', " +
                   "must not start with '.', and must be at most 64 characters.";
        if (saltPrefix.Length == 0 || fileMagic == null || fileMagic.Length != ItchSecrets.FileMagicLength)
            return "Click Random next to Salt / File Header.";
        return null;
    }

    private void Generate()
    {
        string publicPath = FindScriptPath("ItchSecrets", "Assets/ItchSecrets.cs");
        string folder = Path.GetDirectoryName(publicPath) ?? "Assets";
        string localPath = Path.Combine(folder, LocalFileName).Replace('\\', '/');

        var values = new ItchSecrets.LocalValues
        {
            ClientId = clientId,
            GameId = gameId,
            AppSecret = appSecret,
            OfflineDays = offlineDays,
            Port = port,
            FileName = fileName,
            SaltPrefix = saltPrefix,
            FileMagic = fileMagic
        };

        EnsureGitIgnore(folder);
        File.WriteAllText(localPath, BuildLocalFile(values), new UTF8Encoding(false));
        AssetDatabase.Refresh();

        Debug.Log($"Wrote {localPath}. Callback URL: {CallbackUrl(port)}. Never commit {LocalFileName}.");
        Close();
    }

    /// Builds the source of ItchSecrets.Local.cs. Every value is scrambled with
    /// its own random seed, and the unscramble function itself uses constants
    /// picked at random for this project, so no two projects share the same
    /// bytes or code constants. The itch.io constants are filled in here.
    public static string BuildLocalFile(ItchSecrets.LocalValues values)
    {
        var w = new LocalFileWriter(Scrambler.CreateRandom());

        w.Text(nameof(values.ClientId), values.ClientId);
        w.Text(nameof(values.GameId), values.GameId);
        w.Text(nameof(values.AppSecret), values.AppSecret);
        w.Number(nameof(values.OfflineDays), values.OfflineDays);
        w.Number(nameof(values.Port), values.Port);
        w.Text(nameof(values.FileName), values.FileName);
        w.Text(nameof(values.SaltPrefix), values.SaltPrefix);
        w.Bytes(nameof(values.FileMagic), values.FileMagic);
        w.Text(nameof(values.AuthUrl), AuthUrl);
        w.Text(nameof(values.ApiBase), ApiBase);
        w.Text(nameof(values.GamesPath), GamesPath);
        w.Text(nameof(values.OwnershipPath), OwnershipPath);
        w.Text(nameof(values.ProfileGamesPath), ProfileGamesPath);
        w.Text(nameof(values.ScopeOwnership), ScopeOwnership);
        w.Text(nameof(values.ScopeProfileGames), ScopeProfileGames);

        return w.Build();
    }

    private sealed class LocalFileWriter
    {
        private readonly Scrambler scrambler;
        private readonly StringBuilder fields = new StringBuilder();
        private readonly StringBuilder assignments = new StringBuilder();
        private int index;

        public LocalFileWriter(Scrambler scrambler)
        {
            this.scrambler = scrambler;
        }

        public void Text(string target, string value)
        {
            string read = Field(Encoding.UTF8.GetBytes(value ?? ""), "S");
            assignments.AppendLine($"        values.{target} = {read};");
        }

        public void Number(string target, int value)
        {
            string read = Field(Encoding.UTF8.GetBytes(value.ToString()), "S");
            assignments.AppendLine($"        values.{target} = int.TryParse({read}, out int n{index}) ? n{index} : 0;");
        }

        public void Bytes(string target, byte[] value)
        {
            string read = Field(value ?? new byte[0], "B");
            assignments.AppendLine($"        values.{target} = {read};");
        }

        private string Field(byte[] plain, string reader)
        {
            uint seed = RandomSeed();
            byte[] scrambled = scrambler.Apply(plain, seed);
            string name = "_v" + index++;

            fields.Append("    private static readonly byte[] ").Append(name).Append(" = { ");
            for (int i = 0; i < scrambled.Length; i++)
            {
                if (i > 0) fields.Append(", ");
                fields.Append("0x").Append(scrambled[i].ToString("X2"));
            }
            fields.AppendLine(" };");

            return $"{reader}({name}, 0x{seed:X8}u)";
        }

        public string Build()
        {
            var file = new StringBuilder();
            file.AppendLine("// GENERATED by Tools > itch.io Licensing > Generate Secrets. Do not edit by hand.");
            file.AppendLine("// Contains your real secrets. NEVER commit or share this file (it is git-ignored).");
            file.AppendLine();
            file.AppendLine("public static partial class ItchSecrets");
            file.AppendLine("{");
            file.Append(fields);
            file.AppendLine();
            file.AppendLine("    static partial void LoadLocal(LocalValues values)");
            file.AppendLine("    {");
            file.Append(assignments);
            file.AppendLine("    }");
            file.AppendLine();
            file.AppendLine("    private static string S(byte[] data, uint seed) => System.Text.Encoding.UTF8.GetString(B(data, seed));");
            file.AppendLine();
            file.AppendLine("    private static byte[] B(byte[] data, uint seed)");
            file.AppendLine("    {");
            file.AppendLine("        byte[] output = new byte[data.Length];");
            file.AppendLine("        uint s = seed;");
            file.AppendLine("        for (int i = 0; i < data.Length; i++)");
            file.AppendLine("        {");
            file.AppendLine("            s ^= s << 13;");
            file.AppendLine("            s ^= s >> 17;");
            file.AppendLine("            s ^= s << 5;");
            file.AppendLine($"            output[i] = (byte)(data[i] ^ (byte)(s >> {scrambler.Shift}) ^ (byte)(i * {scrambler.Mul} + {scrambler.Add}));");
            file.AppendLine("        }");
            file.AppendLine("        return output;");
            file.AppendLine("    }");
            file.AppendLine("}");
            return file.ToString();
        }
    }

    /// Mirror of the B() function written into the generated file.
    private sealed class Scrambler
    {
        public int Shift;
        public int Mul;
        public int Add;

        public static Scrambler CreateRandom()
        {
            return new Scrambler
            {
                Shift = RandomInt(0, 25),         // 0..24
                Mul = RandomInt(1, 128) * 2 + 1,  // odd, 3..255
                Add = RandomInt(0, 256)           // 0..255
            };
        }

        public byte[] Apply(byte[] data, uint seed)
        {
            byte[] output = new byte[data.Length];
            uint s = seed;
            for (int i = 0; i < data.Length; i++)
            {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                output[i] = (byte)(data[i] ^ (byte)(s >> Shift) ^ (byte)(i * Mul + Add));
            }
            return output;
        }
    }

    private void RandomizeFileValues()
    {
        fileName = RandomHex(4) + ".dat";
        saltPrefix = RandomSecret().Substring(0, 12);
        fileMagic = RandomBytes(ItchSecrets.FileMagicLength);
    }

    private static string CallbackUrl(int port) => $"http://127.0.0.1:{port}/callback";

    private static bool IsSafeFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name.StartsWith("."))
            return false;

        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                      c == '.' || c == '-' || c == '_';
            if (!ok)
                return false;
        }

        return true;
    }

    /// Adds the secrets file to a .gitignore in the same folder, so it stays
    /// out of git wherever these scripts are placed. Git applies .gitignore
    /// files in subfolders, and Unity ignores files starting with a dot.
    private static void EnsureGitIgnore(string folder)
    {
        string path = Path.Combine(folder, ".gitignore");
        string[] required = { LocalFileName, LocalFileName + ".meta" };

        string existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var lines = new HashSet<string>();
        foreach (string line in existing.Split('\n'))
            lines.Add(line.Trim());

        var add = new StringBuilder();
        foreach (string entry in required)
            if (!lines.Contains(entry))
                add.Append(entry).Append('\n');

        if (add.Length == 0)
            return;

        if (existing.Length > 0 && !existing.EndsWith("\n"))
            existing += "\n";
        if (existing.Length == 0)
            existing = "# Real itch.io secrets, written by Generate Secrets. Never commit these.\n";

        File.WriteAllText(path, existing + add, new UTF8Encoding(false));
    }

    private static string FindScriptPath(string className, string fallback)
    {
        foreach (string guid in AssetDatabase.FindAssets(className + " t:MonoScript"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileName(assetPath) == className + ".cs")
                return assetPath;
        }

        return fallback;
    }

    private static byte[] RandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);
        return bytes;
    }

    /// Uniform random integer in [min, max).
    private static int RandomInt(int min, int max)
    {
        uint range = (uint)(max - min);
        uint limit = uint.MaxValue - uint.MaxValue % range;
        uint value;
        do
            value = BitConverter.ToUInt32(RandomBytes(4), 0);
        while (value >= limit);
        return min + (int)(value % range);
    }

    private static uint RandomSeed()
    {
        uint seed = BitConverter.ToUInt32(RandomBytes(4), 0);
        return seed != 0 ? seed : 1u;
    }

    private static int RandomPort() => RandomInt(MinPort, MaxPort + 1);

    private static string RandomSecret() =>
        Convert.ToBase64String(RandomBytes(36)).Replace('+', '-').Replace('/', '_');

    private static string RandomHex(int byteCount) => ToHex(RandomBytes(byteCount));

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder();
        if (bytes != null)
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

/// Reminds the developer in the Console when the secrets have not been generated.
[InitializeOnLoad]
internal static class ItchSecretsReminder
{
    static ItchSecretsReminder()
    {
        EditorApplication.delayCall += () =>
        {
            if (!ItchSecrets.IsConfigured)
                Debug.LogWarning("[itch] Licensing secrets are not set. " +
                                 "Open Tools > itch.io Licensing > Generate Secrets.");
        };
    }
}
#endif
