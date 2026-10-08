using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VatscaUpdateChecker.Services;

public sealed record GngPackageSelectionResult(string Status, string? Version = null, string? Identity = null);
public enum GngPackageKind { Full, UpdateOnly }

/// <summary>Selects the newest Swedish ZIP of the requested kind from the observed AeroNav package table.</summary>
public static class GngPackageSelection
{
    public static bool IsPackagePage(Uri? uri) => uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.IdnHost.Equals("files.aero-nav.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath is "/ESAA" or "/ESAA/";

    // Returns only bounded public package metadata. URLs remain in the page, and no
    // form, browser credential store, cookie, token or web storage is inspected.
    // The request owner must also prevent repeated attempts across navigations.
    public static string CreateInspectionScript(GngPackageKind kind, string? requiredVersion = null) =>
        CreateScript(kind, requiredVersion, null);

    public static string CreateDownloadScript(GngPackageKind kind, string identity, string? requiredVersion = null)
    {
        var version = GetIdentityVersion(identity);
        if (version is null || (requiredVersion is not null && requiredVersion != version))
            throw new ArgumentException("Choose the inspected GNG package identity before starting its download.", nameof(identity));
        return CreateScript(kind, requiredVersion, identity);
    }

    private static string CreateScript(GngPackageKind kind, string? requiredVersion, string? identity)
    {
        var packageKind = kind switch
        {
            GngPackageKind.Full => "FULL_PACKAGE",
            GngPackageKind.UpdateOnly => "UPDATE_ONLY",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var required = "";
        if (requiredVersion is not null)
        {
            var version = Regex.Match(requiredVersion, @"\A([0-9]{4})/([0-9]{2}) rev\.((?:0|[1-9][0-9]{0,3}))\z", RegexOptions.CultureInvariant);
            if (!version.Success || int.Parse(version.Groups[1].Value[2..], CultureInfo.InvariantCulture) is < 1 or > 14 ||
                int.Parse(version.Groups[2].Value, CultureInfo.InvariantCulture) < 1) throw new ArgumentException("Choose a validated GNG AIRAC/revision before selecting a matching package.", nameof(requiredVersion));
            required = version.Groups[1].Value + version.Groups[2].Value + "-" + version.Groups[3].Value.PadLeft(4, '0');
        }
        return SelectionScript.Replace("__PACKAGE_KIND__", packageKind, StringComparison.Ordinal)
            .Replace("__REQUIRED_VERSION__", required, StringComparison.Ordinal)
            .Replace("__EXPECTED_IDENTITY__", identity ?? "", StringComparison.Ordinal);
    }

    private const string SelectionScript = """
        (() => {
            const result = (status, candidate = null) => ({
                status,
                version: candidate ? candidate.version : null,
                identity: candidate ? candidate.identity : null
            });
            let page;
            try { page = new URL(window.location.href); } catch { return result('wrong-page'); }
            if (page.protocol !== 'https:' || page.hostname !== 'files.aero-nav.com' ||
                (page.port && page.port !== '443') || page.username || page.password ||
                !['/ESAA', '/ESAA/'].includes(page.pathname)) return result('wrong-page');
            const requestedName = 'ESAA __PACKAGE_KIND__';
            const requiredVersion = '__REQUIRED_VERSION__';
            const expectedIdentity = '__EXPECTED_IDENTITY__';
            const text = element => (element.textContent || '').replace(/\s+/g, ' ').trim();
            const expected = ['client', 'packagename', 'airac', 'version', 'released', 'download'];
            const allTables = Array.from(document.querySelectorAll('table'));
            if (allTables.length > 64) return result('ambiguous');
            const tables = [];
            for (const table of allTables) {
                const rows = Array.from(table.rows);
                if (!rows.length) continue;
                const headers = Array.from(rows[0].cells);
                if (headers.length !== 6 || !headers.every((cell, i) => text(cell).toLowerCase() === expected[i])) continue;
                if (!headers.every((cell, i) => cell.colSpan === (i === 5 ? 2 : 1) && cell.rowSpan === 1)) return result('ambiguous');
                tables.push(rows);
            }
            if (tables.length === 0) return result('no-package');
            if (tables.length !== 1 || tables[0].length > 10001) return result('ambiguous');
            const candidates = [];
            for (const row of tables[0].slice(1)) {
                const cells = Array.from(row.cells);
                if (cells.length !== 7 || cells.some(cell => cell.colSpan !== 1 || cell.rowSpan !== 1)) return result('ambiguous');
                const values = cells.slice(0, 5).map(text);
                if (values.some(value => value.length > 128)) return result('ambiguous');
                if (values[0].toUpperCase() !== 'ES') continue;
                const name = values[1].toUpperCase();
                if (name !== requestedName) {
                    if (name.startsWith('ESAA') && name.includes(requestedName.split(' ')[1].split('_')[0])) return result('ambiguous');
                    continue;
                }
                const airac = /^([0-9]{4})\s*\/\s*([0-9]{2})$/.exec(values[2]);
                const revision = /^[0-9]{1,4}$/.test(values[3]) ? Number(values[3]) : -1;
                const released = /^([0-9]{4})-([0-9]{2})-([0-9]{2}) ([0-9]{2}):([0-9]{2}):([0-9]{2})$/.exec(values[4]);
                if (!airac || revision < 0 || !released) return result('ambiguous');
                if (Number(airac[1].slice(2)) < 1 || Number(airac[1].slice(2)) > 14 || Number(airac[2]) < 1) return result('ambiguous');
                const parts = released.slice(1).map(Number);
                const date = new Date(Date.UTC(parts[0], parts[1] - 1, parts[2], parts[3], parts[4], parts[5]));
                if (parts[0] < 1000 || date.getUTCFullYear() !== parts[0] || date.getUTCMonth() + 1 !== parts[1] ||
                    date.getUTCDate() !== parts[2] || date.getUTCHours() !== parts[3] ||
                    date.getUTCMinutes() !== parts[4] || date.getUTCSeconds() !== parts[5]) return result('ambiguous');
                const timestamp = released.slice(1).join('');
                if (requiredVersion && requiredVersion !== airac[1] + airac[2] + '-' + String(revision).padStart(4, '0')) continue;
                candidates.push({
                    rank: [Number(airac[1] + airac[2]), revision, Number(timestamp)],
                    version: airac[1] + '/' + airac[2] + ' rev.' + revision,
                    identity: timestamp + '-' + airac[1] + airac[2] + '-' + String(revision).padStart(4, '0'),
                    zipCell: cells[5], sevenZipCell: cells[6]
                });
            }
            if (!candidates.length) return result('no-package');
            const compare = (a, b) => {
                for (let i = 0; i < a.rank.length; i++) if (a.rank[i] !== b.rank[i]) return b.rank[i] - a.rank[i];
                return 0;
            };
            candidates.sort(compare);
            const latest = candidates[0];
            if (candidates.length > 1 && compare(latest, candidates[1]) === 0) return result('ambiguous');
            if (expectedIdentity && latest.identity !== expectedIdentity) return result('selection-changed');
            const links = Array.from(latest.zipCell.querySelectorAll('a'));
            const otherLinks = Array.from(latest.sevenZipCell.querySelectorAll('a'));
            if (links.length !== 1 || text(links[0]).toLowerCase() !== 'zip' ||
                otherLinks.length !== 1 || text(otherLinks[0]).toLowerCase() !== '7z') return result('ambiguous');
            const link = links[0];
            if (link.closest('.disabled, [disabled], [aria-disabled="true"]')) return result('waiting-for-login', latest);
            const href = link.getAttribute('href');
            if (!href || !href.trim() || href.trim().startsWith('#')) return result('unsupported-link', latest);
            let download;
            try { download = new URL(href, page.href); } catch { return result('unsupported-link', latest); }
            if (download.protocol !== 'https:' || download.origin !== page.origin || download.username || download.password ||
                download.hash || (download.pathname === page.pathname && download.search === page.search) ||
                typeof link.click !== 'function') return result('unsupported-link', latest);
            if (!expectedIdentity) return result('ready', latest);
            const claim = Symbol.for('VatscaLaunchpad.GngDownloadClaim.v1.__PACKAGE_KIND__');
            if (window[claim]) return result('already-attempted');
            window[claim] = true;
            try { link.click(); } catch { return result('unsupported-link', latest); }
            return result('clicked', latest);
        })()
        """;

    public static GngPackageSelectionResult ParseResult(string? json)
    {
        if (json is null || json.Length > 512) return new("ambiguous");
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("status" or "version" or "identity"))) return new("ambiguous");
            if (!value.TryGetProperty("status", out var statusValue) || statusValue.ValueKind != JsonValueKind.String) return new("ambiguous");
            var status = statusValue.GetString();
            if (status is "wrong-page" or "already-attempted" or "no-package" or "ambiguous" or "selection-changed") return new(status);
            if (status is not ("ready" or "clicked" or "waiting-for-login" or "unsupported-link")) return new("ambiguous");
            if (!value.TryGetProperty("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("identity", out var identityValue) || identityValue.ValueKind != JsonValueKind.String) return new("ambiguous");
            var version = versionValue.GetString()!;
            var identity = identityValue.GetString()!;
            if (version != GetIdentityVersion(identity)) return new("ambiguous");
            return new(status, version, identity);
        }
        catch (JsonException) { return new("ambiguous"); }
    }

    private static string? GetIdentityVersion(string? identity)
    {
        if (identity is null || identity.Length != 26) return null;
        var match = Regex.Match(identity, @"\A([0-9]{14})-([0-9]{4})([0-9]{2})-([0-9]{4})\z", RegexOptions.CultureInvariant);
        if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            int.Parse(match.Groups[1].Value[..4], CultureInfo.InvariantCulture) < 1000 ||
            int.Parse(match.Groups[2].Value[2..], CultureInfo.InvariantCulture) is < 1 or > 14 || int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) < 1)
            return null;
        return match.Groups[2].Value + "/" + match.Groups[3].Value + " rev." + int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
    }
}
