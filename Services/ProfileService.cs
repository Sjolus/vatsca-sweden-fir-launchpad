using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static class ProfileService
{
    // EuroScope .prf files are written in Windows-1252; use the same encoding to avoid
    // mangling non-ASCII characters (e.g. Swedish letters like ö, å, ä).
    // RegisterProvider is required in self-contained .NET builds where non-UTF encodings
    // are not loaded automatically.
    private static readonly Encoding PrfEncoding = GetPrfEncoding();

    private static Encoding GetPrfEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    public static readonly (int Value, string Label)[] Ratings =
    {
        (0, "OBS"), (1, "S1"), (2, "S2"), (3, "S3"),
        (4, "C1"), (6, "C3"), (7, "I1"), (8, "I2"),
        (9, "I3"), (10, "SUP"), (11, "ADM"),
    };

    // -------------------------------------------------------------------------
    // State checks
    // -------------------------------------------------------------------------

    public static bool IsConfigured(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.VatsimName) &&
        !string.IsNullOrWhiteSpace(s.VatsimCid)  &&
        s.VatsimRating >= 0                       &&
        CredentialManagerService.Has(CredentialManagerService.TargetVatsim);

    /// <summary>
    /// Returns true when stored settings match what is actually written in the EuroScope files.
    /// Returns true (no mismatch to report) if EuroscopeDataPath is not configured.
    /// Returns false if the profile is not fully configured.
    /// </summary>
    public static bool IsInSync(AppSettings s, string euroscopeDataPath)
    {
        if (!IsConfigured(s)) return false;
        if (string.IsNullOrWhiteSpace(euroscopeDataPath) || !Directory.Exists(euroscopeDataPath))
            return true; // Can't check — assume OK

        var stored   = CredentialManagerService.Load(CredentialManagerService.TargetVatsim) ?? string.Empty;
        var actual   = ReadCurrentState(euroscopeDataPath);
        if (actual is null) return false;

        if (!string.Equals(actual.Name, s.VatsimName, StringComparison.Ordinal))        return false;
        if (!string.Equals(actual.Cid,  s.VatsimCid,  StringComparison.Ordinal))        return false;
        if (!string.Equals(actual.Password, stored,   StringComparison.Ordinal))        return false;
        if (s.VatsimRating != 0 && actual.Rating != s.VatsimRating)                     return false;

        var storedHoppie = CredentialManagerService.Load(CredentialManagerService.TargetHoppie) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(storedHoppie) &&
            !string.Equals(actual.HoppieCode, storedHoppie, StringComparison.Ordinal))  return false;

        if (!string.IsNullOrWhiteSpace(s.ObsCallsign) &&
            !string.Equals(actual.ObsCallsign, s.ObsCallsign.ToUpper(),
                StringComparison.OrdinalIgnoreCase))                                     return false;

        return true;
    }

    // -------------------------------------------------------------------------
    // Apply stored settings to EuroScope files
    // -------------------------------------------------------------------------

    public static void Apply(AppSettings s, string euroscopeDataPath)
    {
        if (!Directory.Exists(euroscopeDataPath)) return;

        var password = CredentialManagerService.Load(CredentialManagerService.TargetVatsim) ?? string.Empty;
        var prfFiles = Directory.GetFiles(euroscopeDataPath, "ES*.prf");
        Logger.Log("APPLY", $"Patching {prfFiles.Length} .prf file(s) in {euroscopeDataPath}");
        foreach (var prf in prfFiles)
            PatchPrf(prf, s, password);

        var hoppieCode = CredentialManagerService.Load(CredentialManagerService.TargetHoppie) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(hoppieCode))
        {
            var pluginsDir = Path.Combine(euroscopeDataPath, "ESAA", "Plugins");
            Directory.CreateDirectory(pluginsDir);
            var primaryHoppie = Path.Combine(pluginsDir, "TopSkyCPDLChoppieCode.txt");
            File.WriteAllText(primaryHoppie, hoppieCode);
            Logger.Log("APPLY", $"Wrote Hoppie code → {primaryHoppie}");

            // Also update any existing copies in secondary locations
            foreach (var extra in new[]
            {
                Path.Combine(euroscopeDataPath, "TopSkyCPDLChoppieCode.txt"),
                Path.Combine(euroscopeDataPath, "Plugins", "TopSkyCPDLChoppieCode.txt"),
            })
            {
                if (File.Exists(extra))
                {
                    File.WriteAllText(extra, hoppieCode);
                    Logger.Log("APPLY", $"Wrote Hoppie code → {extra} (secondary)");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(s.ObsCallsign))
            PatchLoginProfiles(
                Path.Combine(euroscopeDataPath, "ESAA", "Settings"),
                s.ObsCallsign.ToUpper());
    }

    // -------------------------------------------------------------------------
    // Dry-run preview
    // -------------------------------------------------------------------------

    private const string Masked = "••••••••";

    /// <summary>
    /// Returns a human-readable summary of every change that Apply() would make,
    /// showing exact file paths and the actual lines that will be written.
    /// Pass showCredentials=true to reveal passwords in full.
    /// </summary>
    public static string GeneratePreview(
        AppSettings s, string vatsimPassword, string hoppieCode,
        string euroscopeDataPath, bool showCredentials = false)
    {
        var sb          = new StringBuilder();
        var ratingLabel = Ratings.FirstOrDefault(r => r.Value == s.VatsimRating).Label
                          ?? s.VatsimRating.ToString();

        // ── helpers ──────────────────────────────────────────────────────────────

        void SectionHeader(string path, string? note = null)
        {
            sb.AppendLine();
            sb.AppendLine(note is null ? path : $"{path}  [{note}]");
            sb.AppendLine(new string('─', Math.Min(Math.Max(path.Length, 40), 90)));
        }

        // Formats one tab-delimited file field with a before→after diff line.
        void PrfField(string field, string curVal, string newVal, bool secret = false)
        {
            var tag = string.Equals(curVal, newVal, StringComparison.Ordinal)
                      ? "(unchanged)" : "← changed";

            string dc, dn;
            if (secret && !showCredentials)
            {
                dc = string.IsNullOrEmpty(curVal) ? "(not set)" : Masked;
                dn = string.IsNullOrEmpty(newVal) ? "(not set)" : Masked;
            }
            else
            {
                dc = string.IsNullOrEmpty(curVal) ? "(not set)" : curVal;
                dn = newVal;
            }

            var curLine = $"LastSession\t{field}\t{dc}";
            var newLine = $"LastSession\t{field}\t{dn}";
            sb.AppendLine($"  {curLine,-55}  →  {newLine,-55}  {tag}");
        }

        void HoppieFile(string path, string note = "")
        {
            var cur     = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            var tag     = string.Equals(cur, hoppieCode, StringComparison.Ordinal)
                          ? "(unchanged)" : "← changed";
            var dc      = showCredentials ? (string.IsNullOrEmpty(cur) ? "(not set)" : cur)
                                          : (string.IsNullOrEmpty(cur) ? "(not set)" : Masked);
            var dn      = showCredentials ? (string.IsNullOrEmpty(hoppieCode) ? "(not set)" : hoppieCode)
                                          : (string.IsNullOrEmpty(hoppieCode) ? "(not set)" : Masked);
            SectionHeader(path, string.IsNullOrEmpty(note) ? null : note);
            sb.AppendLine($"  {dc}  →  {dn}  {tag}");
        }

        // ── .prf files ───────────────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(euroscopeDataPath) || !Directory.Exists(euroscopeDataPath))
        {
            sb.AppendLine("EuroScope data folder not configured — cannot preview .prf changes.");
        }
        else
        {
            var prfFiles = Directory.GetFiles(euroscopeDataPath, "ES*.prf");
            if (prfFiles.Length == 0)
                sb.AppendLine("No ES*.prf files found in the data folder.");

            foreach (var prf in prfFiles)
            {
                string curName = string.Empty, curCid = string.Empty, curPassword = string.Empty,
                       curRating = string.Empty, curServer = string.Empty;

                foreach (var l in File.ReadLines(prf, PrfEncoding))
                {
                    var p = l.Split('\t');
                    if (p.Length < 3 || p[0] != "LastSession") continue;
                    switch (p[1])
                    {
                        case "realname":    curName     = p[2]; break;
                        case "certificate": curCid      = p[2]; break;
                        case "password":    curPassword = p[2]; break;
                        case "rating":      curRating   = p[2]; break;
                        case "server":      curServer   = p[2]; break;
                    }
                }

                SectionHeader(prf);

                PrfField("realname",    curName,     s.VatsimName);
                PrfField("certificate", curCid,      s.VatsimCid);
                PrfField("password",    curPassword, vatsimPassword, secret: true);

                // Rating: OBS omits the line entirely — compare empty string to empty string
                var ratingCurCmp = string.IsNullOrEmpty(curRating) ? string.Empty : curRating;
                var ratingNewCmp = s.VatsimRating == 0 ? string.Empty : s.VatsimRating.ToString();
                var ratingTag    = string.Equals(ratingCurCmp, ratingNewCmp) ? "(unchanged)" : "← changed";
                var ratingCurDsp = string.IsNullOrEmpty(curRating) ? "(not set)" : $"LastSession\trating\t{curRating}";
                var ratingNewDsp = s.VatsimRating == 0
                    ? "(line omitted — OBS)"
                    : $"LastSession\trating\t{s.VatsimRating} ({ratingLabel})";
                sb.AppendLine($"  {ratingCurDsp,-55}  →  {ratingNewDsp,-55}  {ratingTag}");

                PrfField("server", curServer, "AUTOMATIC");
            }
        }

        // ── Hoppie files ─────────────────────────────────────────────────────────

        if (!string.IsNullOrWhiteSpace(euroscopeDataPath))
        {
            // Primary — always written
            HoppieFile(
                Path.Combine(euroscopeDataPath, "ESAA", "Plugins", "TopSkyCPDLChoppieCode.txt"));

            // Secondary — only updated if they already exist (matching .bat behaviour)
            foreach (var secondary in new[]
            {
                Path.Combine(euroscopeDataPath, "TopSkyCPDLChoppieCode.txt"),
                Path.Combine(euroscopeDataPath, "Plugins", "TopSkyCPDLChoppieCode.txt"),
            })
            {
                if (File.Exists(secondary))
                    HoppieFile(secondary, "also exists — will update");
            }
        }

        // ── LoginProfiles.txt ────────────────────────────────────────────────────

        if (!string.IsNullOrWhiteSpace(euroscopeDataPath))
        {
            var profilesFile = Path.Combine(euroscopeDataPath, "ESAA", "Settings", "LoginProfiles.txt");
            SectionHeader(profilesFile, File.Exists(profilesFile) ? null : "will be created");

            string curLine = string.Empty;
            if (File.Exists(profilesFile))
            {
                foreach (var l in File.ReadLines(profilesFile))
                {
                    if (Regex.IsMatch(l, @"^PROFILE:\w+_OBS:")) { curLine = l.Trim(); break; }
                }
            }

            var newLine = string.IsNullOrWhiteSpace(s.ObsCallsign)
                ? "(no prefix set — skipped)"
                : $"PROFILE:{s.ObsCallsign.ToUpper()}_OBS:300:0";
            var obsTag  = string.Equals(curLine, newLine) ? "(unchanged)" : "← changed";
            var curDsp  = string.IsNullOrEmpty(curLine) ? "(no OBS profile)" : curLine;
            sb.AppendLine($"  {curDsp,-55}  →  {newLine,-55}  {obsTag}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private record ProfileState(
        string Name, string Cid, string Password, int Rating,
        string HoppieCode, string ObsCallsign);

    private static ProfileState? ReadCurrentState(string euroscopeDataPath)
    {
        var prfFiles = Directory.GetFiles(euroscopeDataPath, "ES*.prf");
        if (prfFiles.Length == 0) return null;

        string name = string.Empty, cid = string.Empty, password = string.Empty;
        int rating = 0; // default OBS

        foreach (var line in File.ReadLines(prfFiles[0], PrfEncoding))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[0] != "LastSession") continue;
            switch (parts[1])
            {
                case "realname":    name     = parts[2]; break;
                case "certificate": cid      = parts[2]; break;
                case "password":    password = parts[2]; break;
                case "rating":      int.TryParse(parts[2], out rating); break;
            }
        }

        var hoppieFile = Path.Combine(euroscopeDataPath, "ESAA", "Plugins", "TopSkyCPDLChoppieCode.txt");
        var hoppie     = File.Exists(hoppieFile) ? File.ReadAllText(hoppieFile).Trim() : string.Empty;

        var profilesFile = Path.Combine(euroscopeDataPath, "ESAA", "Settings", "LoginProfiles.txt");
        var obsCallsign  = string.Empty;
        if (File.Exists(profilesFile))
        {
            foreach (var line in File.ReadLines(profilesFile))
            {
                var m = Regex.Match(line, @"^PROFILE:(\w+)_OBS:");
                if (m.Success) { obsCallsign = m.Groups[1].Value; break; }
            }
        }

        return new ProfileState(name, cid, password, rating, hoppie, obsCallsign);
    }

    private static void PatchPrf(string prfPath, AppSettings s, string password)
    {
        Logger.Log("APPLY", $"Patching {prfPath}");
        var lines          = File.ReadAllLines(prfPath, PrfEncoding);
        var output         = new List<string>();
        bool foundRealname = false, foundCert  = false, foundPwd    = false,
             foundRating   = false, foundServer = false;
        bool wasInLastSession = false;
        bool addedMissing     = false;

        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[0] == "LastSession")
            {
                wasInLastSession = true;
                if (parts.Length >= 3)
                {
                    switch (parts[1])
                    {
                        case "realname":
                            output.Add($"LastSession\trealname\t{s.VatsimName}");
                            foundRealname = true;
                            continue;
                        case "certificate":
                            output.Add($"LastSession\tcertificate\t{s.VatsimCid}");
                            foundCert = true;
                            continue;
                        case "password":
                            output.Add($"LastSession\tpassword\t{password}");
                            foundPwd = true;
                            continue;
                        case "rating":
                            if (s.VatsimRating != 0) // OBS: omit rating line entirely
                            {
                                output.Add($"LastSession\trating\t{s.VatsimRating}");
                                foundRating = true;
                            }
                            continue;
                        case "server":
                            output.Add($"LastSession\tserver\tAUTOMATIC");
                            foundServer = true;
                            continue;
                    }
                }
                output.Add(line);
            }
            else
            {
                // Leaving LastSession section — inject any missing entries
                if (wasInLastSession && !addedMissing)
                {
                    InjectMissing(output, s, password, foundRealname, foundCert, foundPwd, foundRating, foundServer);
                    addedMissing = true;
                }
                output.Add(line);
            }
        }

        if (wasInLastSession && !addedMissing)
            InjectMissing(output, s, password, foundRealname, foundCert, foundPwd, foundRating, foundServer);

        if (!wasInLastSession)
            InjectMissing(output, s, password, false, false, false, false, false);

        File.WriteAllLines(prfPath, output, PrfEncoding);
    }

    private static void InjectMissing(List<string> output, AppSettings s, string password,
        bool foundRealname, bool foundCert, bool foundPwd, bool foundRating, bool foundServer)
    {
        if (!foundRealname)                    output.Add($"LastSession\trealname\t{s.VatsimName}");
        if (!foundCert)                        output.Add($"LastSession\tcertificate\t{s.VatsimCid}");
        if (!foundPwd)                         output.Add($"LastSession\tpassword\t{password}");
        if (!foundRating && s.VatsimRating != 0) output.Add($"LastSession\trating\t{s.VatsimRating}");
        if (!foundServer)                      output.Add($"LastSession\tserver\tAUTOMATIC");
    }

    private static void PatchLoginProfiles(string settingsDir, string obsPrefix)
    {
        Directory.CreateDirectory(settingsDir);
        var path = Path.Combine(settingsDir, "LoginProfiles.txt");

        if (!File.Exists(path))
        {
            File.WriteAllLines(path, new[]
            {
                "PROFILE",
                $"PROFILE:{obsPrefix}_OBS:300:0",
                "ATIS2:",
                "ATIS3:",
                "ATIS4:",
                "END",
            });
            Logger.Log("APPLY", $"Created LoginProfiles.txt with OBS profile {obsPrefix}_OBS → {path}");
            return;
        }

        var lines  = File.ReadAllLines(path, PrfEncoding).ToList();
        bool found = false;

        for (int i = 0; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^PROFILE:\w+_OBS:")) continue;
            var old = lines[i];
            lines[i] = $"PROFILE:{obsPrefix}_OBS:300:0";
            Logger.Log("APPLY", $"LoginProfiles.txt: replaced \"{old}\" → \"{lines[i]}\" in {path}");
            found = true;
            break;
        }

        if (!found)
        {
            var endIdx   = lines.FindLastIndex(l => l.TrimStart().StartsWith("END"));
            var insertAt = endIdx >= 0 ? endIdx : lines.Count;
            lines.InsertRange(insertAt, new[]
            {
                $"PROFILE:{obsPrefix}_OBS:300:0",
                "ATIS2:",
                "ATIS3:",
                "ATIS4:",
            });
            Logger.Log("APPLY", $"LoginProfiles.txt: inserted OBS profile {obsPrefix}_OBS into {path}");
        }

        File.WriteAllLines(path, lines, PrfEncoding);
    }
}
