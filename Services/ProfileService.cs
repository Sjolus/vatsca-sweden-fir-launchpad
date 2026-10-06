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

    private const string VatEfsDllName = "VatEFS.dll";

    // Canonical screen names for plugin draw permissions, used when a .prf has no
    // existing display rows to copy from. Verified across all five sample .prf files.
    private static readonly string[] DefaultDisplays =
    {
        "Ground Radar display",
        "Standard ES radar screen",
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
    /// Compares selected stored fields with the first ES*.prf and primary support files.
    /// A missing or unconfigured data folder returns true because no comparison is possible.
    /// Does not verify every profile; returns false if the controller profile is incomplete.
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
        if (!Directory.Exists(euroscopeDataPath))
            throw new DirectoryNotFoundException("The EuroScope data folder is unavailable. No profile files were updated.");

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

        // VatEFS plugin — only managed when a path is configured. Empty path = untracked.
        if (!string.IsNullOrWhiteSpace(s.VatEfsPath))
        {
            var dll = Path.Combine(s.VatEfsPath, VatEfsDllName);
            if (s.PatchVatEfs)
            {
                if (File.Exists(dll))
                {
                    foreach (var prf in prfFiles)
                        EnsureVatEfsInPrf(prf, dll);
                }
                else
                {
                    Logger.Log("APPLY", $"VatEFS toggle on but {dll} missing — skipping plugin patch");
                }
            }
            else
            {
                foreach (var prf in prfFiles)
                    RemoveVatEfsFromPrf(prf);
            }
        }
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

        // ── VatEFS plugin (per .prf) ─────────────────────────────────────────────

        if (!string.IsNullOrWhiteSpace(euroscopeDataPath) && !string.IsNullOrWhiteSpace(s.VatEfsPath))
        {
            var dll      = Path.Combine(s.VatEfsPath, VatEfsDllName);
            var prfFiles = Directory.Exists(euroscopeDataPath)
                ? Directory.GetFiles(euroscopeDataPath, "ES*.prf")
                : Array.Empty<string>();

            foreach (var prf in prfFiles)
            {
                var lines     = File.ReadAllLines(prf, PrfEncoding);
                int existing  = -1;
                string? curDll = null;
                var existingDisplays = new SortedSet<int>();

                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    if (p.Length < 3 || p[0] != "Plugins") continue;
                    var mm = Regex.Match(p[1], @"^Plugin(\d+)$");
                    if (mm.Success && IsVatEfsPluginPath(p[2]))
                    {
                        existing = int.Parse(mm.Groups[1].Value);
                        curDll   = p[2];
                    }
                }
                if (existing >= 0)
                {
                    foreach (var line in lines)
                    {
                        var p = line.Split('\t');
                        if (p.Length < 3 || p[0] != "Plugins") continue;
                        var mm = Regex.Match(p[1], $@"^Plugin{existing}Display(\d+)$");
                        if (mm.Success && int.TryParse(mm.Groups[1].Value, out var k))
                            existingDisplays.Add(k);
                    }
                }

                var screens = DiscoverDisplayScreens(lines);
                var note    = s.PatchVatEfs ? "manage: ON — ensuring present"
                                            : "manage: OFF — ensuring absent";
                SectionHeader(prf, note);

                if (s.PatchVatEfs)
                {
                    if (!File.Exists(dll))
                    {
                        sb.AppendLine($"  (DLL not found at {dll} — patch will be skipped)");
                        continue;
                    }

                    int slot = existing >= 0 ? existing : MaxPluginIndex(lines) + 1;
                    var mainTag = existing < 0
                        ? "(will be added)"
                        : string.Equals(curDll, dll, StringComparison.OrdinalIgnoreCase)
                            ? "(unchanged)" : "← path will change";
                    var mainCur = existing < 0 ? "(not present)" : $"Plugins\tPlugin{slot}\t{curDll}";
                    var mainNew = $"Plugins\tPlugin{slot}\t{dll}";
                    sb.AppendLine($"  {mainCur,-55}  →  {mainNew,-55}  {mainTag}");

                    for (int k = 0; k < screens.Count; k++)
                    {
                        var dispCur = existingDisplays.Contains(k)
                            ? $"Plugins\tPlugin{slot}Display{k}\t{screens[k]}"
                            : "(not present)";
                        var dispNew = $"Plugins\tPlugin{slot}Display{k}\t{screens[k]}";
                        var dispTag = existingDisplays.Contains(k) ? "(unchanged)" : "(will be added)";
                        sb.AppendLine($"  {dispCur,-55}  →  {dispNew,-55}  {dispTag}");
                    }
                }
                else
                {
                    if (existing < 0)
                    {
                        sb.AppendLine("  (no VatEFS entry present — nothing to remove)");
                        continue;
                    }
                    sb.AppendLine($"  Plugins\tPlugin{existing}\t{curDll}  ← will be removed");
                    foreach (var k in existingDisplays)
                        sb.AppendLine($"  Plugins\tPlugin{existing}Display{k}\t...  ← will be removed");

                    int higher = lines.Count(l =>
                    {
                        var p = l.Split('\t');
                        if (p.Length < 3 || p[0] != "Plugins") return false;
                        var mm = Regex.Match(p[1], @"^Plugin(\d+)");
                        return mm.Success && int.Parse(mm.Groups[1].Value) > existing;
                    });
                    if (higher > 0)
                        sb.AppendLine($"  (and {higher} higher-indexed plugin line(s) will be renumbered down by 1)");
                }
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static int MaxPluginIndex(IEnumerable<string> lines)
    {
        int max = -1;
        foreach (var line in lines)
        {
            var p = line.Split('\t');
            if (p.Length < 3 || p[0] != "Plugins") continue;
            var m = Regex.Match(p[1], @"^Plugin(\d+)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return max;
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

    // -------------------------------------------------------------------------
    // VatEFS plugin management
    // -------------------------------------------------------------------------

    // Match by basename so a plugin entry counts as VatEFS regardless of where the
    // user has the DLL installed. Survives folder moves and pre-existing manual installs.
    private static bool IsVatEfsPluginPath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(Path.GetFileName(value.Trim()), VatEfsDllName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Discovers the screen-name list to use for plugin draw permissions in the given .prf.
    /// Walks existing PluginNDisplayK rows in order of first appearance, deduplicating; if
    /// no display rows exist anywhere in the file, falls back to the canonical defaults.
    /// </summary>
    private static List<string> DiscoverDisplayScreens(IEnumerable<string> lines)
    {
        var screens = new List<string>();
        foreach (var line in lines)
        {
            var p = line.Split('\t');
            if (p.Length < 3 || p[0] != "Plugins") continue;
            if (!Regex.IsMatch(p[1], @"^Plugin\d+Display\d+$")) continue;
            if (!screens.Contains(p[2])) screens.Add(p[2]);
        }
        if (screens.Count == 0) screens.AddRange(DefaultDisplays);
        return screens;
    }

    private static void EnsureVatEfsInPrf(string prfPath, string dllPath)
    {
        var lines        = File.ReadAllLines(prfPath, PrfEncoding).ToList();
        int slot         = -1;       // existing VatEFS slot, -1 if none
        int slotMainAt   = -1;       // line index of the slot's main entry
        int maxIndex     = -1;
        int lastPluginAt = -1;
        string? curPath  = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var p = lines[i].Split('\t');
            if (p.Length < 3 || p[0] != "Plugins") continue;
            lastPluginAt = i;

            var m = Regex.Match(p[1], @"^Plugin(\d+)$");
            if (!m.Success) continue;   // skip Display rows for index tracking

            var n = int.Parse(m.Groups[1].Value);
            if (n > maxIndex) maxIndex = n;

            if (IsVatEfsPluginPath(p[2]))
            {
                slot       = n;
                slotMainAt = i;
                curPath    = p[2];
            }
        }

        var screens = DiscoverDisplayScreens(lines);
        bool changed = false;

        if (slot < 0)
        {
            // Fresh insert at end of Plugins block.
            slot = maxIndex + 1;
            var block = new List<string> { $"Plugins\tPlugin{slot}\t{dllPath}" };
            for (int k = 0; k < screens.Count; k++)
                block.Add($"Plugins\tPlugin{slot}Display{k}\t{screens[k]}");

            var insertAt = lastPluginAt >= 0 ? lastPluginAt + 1 : lines.Count;
            lines.InsertRange(insertAt, block);
            Logger.Log("APPLY", $"VatEFS added to {prfPath} as Plugin{slot} with {screens.Count} display perm(s)");
            changed = true;
        }
        else
        {
            // Update path if it has drifted (manual install, different folder, etc.).
            if (!string.Equals(curPath, dllPath, StringComparison.OrdinalIgnoreCase))
            {
                lines[slotMainAt] = $"Plugins\tPlugin{slot}\t{dllPath}";
                Logger.Log("APPLY", $"VatEFS path updated in {prfPath} (slot Plugin{slot}): \"{curPath}\" → \"{dllPath}\"");
                changed = true;
            }

            // Ensure each required Display row exists for this slot.
            var have = new HashSet<int>();
            int slotLastAt = slotMainAt;
            for (int i = 0; i < lines.Count; i++)
            {
                var p = lines[i].Split('\t');
                if (p.Length < 3 || p[0] != "Plugins") continue;
                var m = Regex.Match(p[1], $@"^Plugin{slot}Display(\d+)$");
                if (!m.Success) continue;
                if (int.TryParse(m.Groups[1].Value, out var k))
                {
                    have.Add(k);
                    if (i > slotLastAt) slotLastAt = i;
                }
            }

            int added = 0;
            for (int k = 0; k < screens.Count; k++)
            {
                if (have.Contains(k)) continue;
                slotLastAt++;
                lines.Insert(slotLastAt, $"Plugins\tPlugin{slot}Display{k}\t{screens[k]}");
                added++;
            }
            if (added > 0)
            {
                Logger.Log("APPLY", $"VatEFS in {prfPath} (slot Plugin{slot}) — added {added} missing display perm(s)");
                changed = true;
            }
        }

        if (changed)
            File.WriteAllLines(prfPath, lines, PrfEncoding);
        else
            Logger.Log("APPLY", $"VatEFS already complete in {prfPath} (slot Plugin{slot}) — no change");
    }

    private static void RemoveVatEfsFromPrf(string prfPath)
    {
        var lines = File.ReadAllLines(prfPath, PrfEncoding).ToList();

        // Find every VatEFS slot index (defensive — usually 0 or 1, but tolerate corruption).
        var slotsToRemove = new SortedSet<int>();
        foreach (var line in lines)
        {
            var p = line.Split('\t');
            if (p.Length < 3 || p[0] != "Plugins") continue;
            var m = Regex.Match(p[1], @"^Plugin(\d+)$");
            if (m.Success && IsVatEfsPluginPath(p[2]))
                slotsToRemove.Add(int.Parse(m.Groups[1].Value));
        }

        if (slotsToRemove.Count == 0)
        {
            Logger.Log("APPLY", $"VatEFS not present in {prfPath} — nothing to remove");
            return;
        }

        // Renumber surviving plugin indices to keep Plugin0..PluginM dense. For a slot K,
        // its new index is K minus the count of removed slots strictly less than K.
        int Renumber(int original)
        {
            int shift = 0;
            foreach (var rs in slotsToRemove)
            {
                if (rs < original) shift++;
                else break;
            }
            return original - shift;
        }

        var output = new List<string>(lines.Count);
        int removedLines = 0;

        foreach (var line in lines)
        {
            var p = line.Split('\t');
            if (p.Length < 3 || p[0] != "Plugins") { output.Add(line); continue; }

            var mainMatch = Regex.Match(p[1], @"^Plugin(\d+)$");
            if (mainMatch.Success)
            {
                var n = int.Parse(mainMatch.Groups[1].Value);
                if (slotsToRemove.Contains(n)) { removedLines++; continue; }
                var renum = Renumber(n);
                output.Add(renum == n ? line : $"Plugins\tPlugin{renum}\t{p[2]}");
                continue;
            }

            var dispMatch = Regex.Match(p[1], @"^Plugin(\d+)Display(\d+)$");
            if (dispMatch.Success)
            {
                var n = int.Parse(dispMatch.Groups[1].Value);
                if (slotsToRemove.Contains(n)) { removedLines++; continue; }
                var renum = Renumber(n);
                var k     = dispMatch.Groups[2].Value;
                output.Add(renum == n ? line : $"Plugins\tPlugin{renum}Display{k}\t{p[2]}");
                continue;
            }

            // Unknown Plugins\t key — leave alone.
            output.Add(line);
        }

        File.WriteAllLines(prfPath, output, PrfEncoding);
        var slotList = string.Join(", ", slotsToRemove.Select(s => $"Plugin{s}"));
        Logger.Log("APPLY", $"VatEFS removed from {prfPath} (slot(s) {slotList}, {removedLines} line(s)); subsequent slots renumbered");
    }
}
