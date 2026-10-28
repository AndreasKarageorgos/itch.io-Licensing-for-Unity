using System;

/// Days an offline license stays valid after an online login (0 = never expires).
/// Set it in Tools > itch.io Licensing > Generate Secrets; the value is stored
/// in the generated ItchSecrets.Local.cs.
public static class ItchLicenseConfig
{
    public static int OfflineDays => Math.Max(0, ItchSecrets.Load().OfflineDays);
}
