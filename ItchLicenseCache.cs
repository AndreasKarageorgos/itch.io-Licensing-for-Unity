using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

///Result of checking the offline license file.
public enum LicenseStatus
{
    ///File is untouched, matches this game and computer, and has not expired.
    Valid,

    ///File is genuine but expired, or the system clock was turned back. Needs an online login.
    NeedsOnline,

    ///File is missing, edited, from another computer, or unreadable.
    Invalid
}


/// Encrypted, machine-bound license file saved after a successful itch.io
/// ownership check, so the game can start offline afterwards.
///
/// File: [4 magic][1 version][16 IV][AES-256-CBC ciphertext][32 HMAC-SHA256]
/// The magic bytes, file name and salt are random per project. Keys come from
/// PBKDF2(secret + hardware signature), so a copied or edited file fails. Key derivation runs on a background thread to avoid hitches.
///
/// Expiry: a license is valid for ItchLicenseConfig.OfflineDays after the last
/// online login (0 = forever). The file also remembers the latest time it was
/// used, so turning the system clock back does not extend the period.
///
/// Requires API Compatibility Level .NET Standard 2.1 (Unity 2021.2+).
public static class ItchLicenseCache
{
    private const byte FormatVersion = 2;
    private const int Iterations = 50000;
    private const int IvSize = 16;
    private const int MacSize = 32;
    private const int HeaderSize = 5; // magic + version

    // Real license files are a few hundred bytes. Anything far larger is not
    // ours, and is rejected before it is read into memory.
    private const long MaxFileSize = 64 * 1024;

    // How far the clock may go backwards before it counts as tampering.
    // Allows normal clock corrections (time sync, time zone or DST mistakes).
    private const long ClockToleranceSeconds = 12 * 60 * 60;

    private const long SecondsPerDay = 24 * 60 * 60;

    // Unity APIs (paths, SystemInfo) only work on the main thread, so these
    // are captured there once and reused by the background work.
    private static string cachedPath;
    private static string cachedHardware;

    [Serializable]
    private class LicenseData
    {
        public int version;
        public string gameId;
        public string hardwareHash;
        public long issuedAtUnix;   // last successful online login
        public long lastSeenUnix;   // latest time the license was used (0 in older files)
    }

    private struct Context
    {
        public string Path;
        public string Hardware;
        public string GameId;
        public string Secret;
        public string SaltPrefix;
        public byte[] Magic;
        public int OfflineDays;
    }

    ///Full path of the license file. Main thread only.
    public static string FilePath
    {
        get
        {
            Prepare();
            return cachedPath;
        }
    }

    /// Startup check. True only if the license is fully valid and not expired.
    /// Also records the current time in the file (clock rollback protection).
    /// Call from the main thread.
    public static async Task<bool> HasValidLicenseAsync(string gameId)
    {
        return await CheckAsync(gameId, true) == LicenseStatus.Valid;
    }


    /// Checks the license without changing the file. Use for quiet re-checks.
    /// Call from the main thread.

    public static Task<LicenseStatus> CheckAsync(string gameId, bool recordUse = false)
    {
        Context ctx = CreateContext(gameId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return Task.Run(() => Validate(ctx, now, recordUse));
    }

    /// Writes a new license for this computer, starting a new offline period.
    /// Call only after ownership was verified online. Returns false if the file
    /// could not be written.
    public static Task<bool> SaveAsync(string gameId)
    {
        Context ctx = CreateContext(gameId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return Task.Run(() =>
        {
            try
            {
                DeriveKeys(ctx, out byte[] encKey, out byte[] macKey);

                var data = new LicenseData
                {
                    version = FormatVersion,
                    gameId = ctx.GameId,
                    hardwareHash = ctx.Hardware,
                    issuedAtUnix = now,
                    lastSeenUnix = now
                };

                WriteFile(ctx.Path, ctx.Magic, encKey, macKey, data);
                ItchLog.Info("Offline license saved to " + ctx.Path +
                             (ctx.OfflineDays > 0 ? $" (valid {ctx.OfflineDays} days)." : " (no expiry)."));
                return true;
            }
            catch (Exception e)
            {
                ItchLog.Warn("Could not save offline license: " + e.Message);
                return false;
            }
        });
    }

    ///Deletes the license file (testing, or a "sign out" button).
    public static void Delete()
    {
        try
        {
            Prepare();
            if (File.Exists(cachedPath))
                File.Delete(cachedPath);
        }
        catch (Exception e)
        {
            ItchLog.Warn("Could not delete license file: " + e.Message);
        }
    }

    private static Context CreateContext(string gameId)
    {
        Prepare();

        return new Context
        {
            Path = cachedPath,
            Hardware = cachedHardware,
            GameId = gameId,
            Secret = ItchSecrets.AppSecret,
            SaltPrefix = ItchStrings.SaltPrefix,
            Magic = ItchStrings.FileMagic,
            OfflineDays = ItchLicenseConfig.OfflineDays
        };
    }

    private static void Prepare()
    {
        if (cachedPath == null)
        {
            // The generator only allows plain names; this guards against a
            // hand-edited value pointing outside the game's data folder.
            string name = Path.GetFileName(ItchStrings.FileName ?? "");
            if (string.IsNullOrEmpty(name) || name.StartsWith("."))
                name = "settings.dat";

            cachedPath = Path.Combine(Application.persistentDataPath, name);
        }

        if (cachedHardware == null)
            cachedHardware = ComputeHardwareSignature();
    }

    private static LicenseStatus Validate(Context ctx, long now, bool recordUse)
    {
        try
        {
            if (!File.Exists(ctx.Path))
            {
                ItchLog.Info("No license file found.");
                return LicenseStatus.Invalid;
            }

            if (new FileInfo(ctx.Path).Length > MaxFileSize)
                return Fail("License file is too large.");

            byte[] file = File.ReadAllBytes(ctx.Path);

            if (file.Length < HeaderSize + IvSize + 16 + MacSize)
                return Fail("License file is too short.");

            if (ctx.Magic == null || ctx.Magic.Length != ItchSecrets.FileMagicLength)
                return Fail("License settings are missing. Run Generate Secrets.");

            for (int i = 0; i < ctx.Magic.Length; i++)
                if (file[i] != ctx.Magic[i])
                    return Fail("License file has the wrong format.");

            if (file[4] != FormatVersion)
                return Fail("License file version is not supported.");

            DeriveKeys(ctx, out byte[] encKey, out byte[] macKey);

            int macOffset = file.Length - MacSize;

            byte[] expectedMac;
            using (var hmac = new HMACSHA256(macKey))
                expectedMac = hmac.ComputeHash(file, 0, macOffset);

            if (!FixedTimeEquals(expectedMac, file, macOffset))
                return Fail("License file was modified or belongs to another computer.");

            byte[] iv = new byte[IvSize];
            Buffer.BlockCopy(file, HeaderSize, iv, 0, IvSize);

            int cipherOffset = HeaderSize + IvSize;
            byte[] plain;

            using (Aes aes = Aes.Create())
            {
                aes.Key = encKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    plain = decryptor.TransformFinalBlock(file, cipherOffset, macOffset - cipherOffset);
            }

            LicenseData data = JsonUtility.FromJson<LicenseData>(Encoding.UTF8.GetString(plain));

            if (data == null || data.gameId != ctx.GameId || data.hardwareHash != ctx.Hardware)
                return Fail("License file does not match this game or computer.");

            // Clock turned back before the last login or the last use?
            long latestKnown = Math.Max(data.issuedAtUnix, data.lastSeenUnix);
            if (now < latestKnown - ClockToleranceSeconds)
            {
                ItchLog.Warn("System clock is earlier than the last license use. Online login required.");
                return LicenseStatus.NeedsOnline;
            }

            if (ctx.OfflineDays > 0)
            {
                long expiresAt = data.issuedAtUnix + ctx.OfflineDays * SecondsPerDay;

                if (now >= expiresAt)
                {
                    ItchLog.Info($"Offline license expired ({ctx.OfflineDays} day limit). Online login required.");
                    return LicenseStatus.NeedsOnline;
                }

                ItchLog.Info("Valid offline license, expires " +
                             DateTimeOffset.FromUnixTimeSeconds(expiresAt).ToLocalTime() + ".");
            }
            else
            {
                ItchLog.Info("Valid offline license (no expiry).");
            }

            // Remember the latest time of use, so moving the clock back later is detected.
            if (recordUse && now > data.lastSeenUnix)
            {
                try
                {
                    data.lastSeenUnix = now;
                    WriteFile(ctx.Path, ctx.Magic, encKey, macKey, data);
                }
                catch (Exception e)
                {
                    // Not fatal: the license is still valid.
                    ItchLog.Warn("Could not update license file: " + e.Message);
                }
            }

            return LicenseStatus.Valid;
        }
        catch (Exception e)
        {
            return Fail("Could not read license file: " + e.Message);
        }
    }

    private static void WriteFile(string path, byte[] magic, byte[] encKey, byte[] macKey, LicenseData data)
    {
        if (magic == null || magic.Length != ItchSecrets.FileMagicLength)
            throw new InvalidOperationException("License settings are missing.");

        data.version = FormatVersion;
        byte[] plain = Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));

        byte[] iv = new byte[IvSize];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            rng.GetBytes(iv);

        byte[] cipher;
        using (Aes aes = Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using (ICryptoTransform encryptor = aes.CreateEncryptor())
                cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        }

        byte[] file = new byte[HeaderSize + IvSize + cipher.Length + MacSize];
        Buffer.BlockCopy(magic, 0, file, 0, magic.Length);
        file[4] = FormatVersion;
        Buffer.BlockCopy(iv, 0, file, HeaderSize, IvSize);
        Buffer.BlockCopy(cipher, 0, file, HeaderSize + IvSize, cipher.Length);

        int macOffset = file.Length - MacSize;
        byte[] mac;
        using (var hmac = new HMACSHA256(macKey))
            mac = hmac.ComputeHash(file, 0, macOffset);

        Buffer.BlockCopy(mac, 0, file, macOffset, MacSize);

        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Temp file first, so a crash can't leave a half-written license.
        string tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, file);

        if (File.Exists(path))
            File.Delete(path);

        File.Move(tempPath, path);
    }

    ///
    /// SHA-256 of stable machine properties. Leaves out GPU, RAM and drivers,
    /// so normal upgrades don't invalidate the license.
    /// 
    private static string ComputeHardwareSignature()
    {
        string deviceId = SystemInfo.deviceUniqueIdentifier;

        if (string.IsNullOrEmpty(deviceId) || deviceId == SystemInfo.unsupportedIdentifier)
            deviceId = SystemInfo.deviceName; // weaker fallback

        string raw =
            deviceId + "|" +
            SystemInfo.processorType + "|" +
            SystemInfo.processorCount + "|" +
            SystemInfo.operatingSystemFamily;

        using (SHA256 sha = SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
    }

    private static void DeriveKeys(Context ctx, out byte[] encKey, out byte[] macKey)
    {
        byte[] salt = Encoding.UTF8.GetBytes(ctx.SaltPrefix + ctx.GameId);

        using (var kdf = new Rfc2898DeriveBytes(
            ctx.Secret + ":" + ctx.Hardware, salt, Iterations, HashAlgorithmName.SHA256))
        {
            byte[] keys = kdf.GetBytes(64);
            encKey = new byte[32];
            macKey = new byte[32];
            Buffer.BlockCopy(keys, 0, encKey, 0, 32);
            Buffer.BlockCopy(keys, 32, macKey, 0, 32);
        }
    }

    private static bool FixedTimeEquals(byte[] expected, byte[] buffer, int offset)
    {
        int diff = 0;
        for (int i = 0; i < expected.Length; i++)
            diff |= expected[i] ^ buffer[offset + i];
        return diff == 0;
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static LicenseStatus Fail(string reason)
    {
        ItchLog.Warn(reason);
        return LicenseStatus.Invalid;
    }
}

///
/// Logging that exists only in the Editor and Development Builds.
/// In release builds the calls AND their message strings are removed by the
/// compiler, so there's nothing for a cracker to search for.
/// 
internal static class ItchLog
{
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Info(string message) => Debug.Log("[itch] " + message);

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Warn(string message) => Debug.LogWarning("[itch] " + message);

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    public static void Error(string message) => Debug.LogError("[itch] " + message);
}
