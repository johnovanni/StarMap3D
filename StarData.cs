using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;

namespace StarMap3D;

public sealed class LoadResult
{
    public List<Star> Stars { get; set; } = new();
    public string Status { get; set; } = "";
}

/// <summary>
/// Loads nearby stars.
///   * Stars:   HYG database (Hipparcos + Yale Bright Star + Gliese nearby-star catalogues).
///   * Planets: NASA Exoplanet Archive (pscomppars). One row per planet.
/// Both are downloaded once, out to <see cref="MaxDownloadLy"/>, and cached in %LocalAppData%\StarMap3D.
/// The "distance from Earth" shown on screen is then just an in-memory filter over that cache
/// (see <see cref="ApplyDistance"/>), so moving the distance slider never needs a re-download.
/// If nothing can be downloaded and there is no cache, a small built-in list of well-known nearby
/// stars is used instead (it only covers distances out to about 51 ly).
/// </summary>
public static class StarData
{
    /// <summary>Outer bound of what gets downloaded/cached. The on-screen distance slider cannot go past this.</summary>
    public const double MaxDownloadLy = 300.0;
    const double MaxPc = MaxDownloadLy / Star.LyPerPc;

    /// <summary>Initial/default value for the on-screen distance slider.</summary>
    public const double DefaultLy = 100.0;
    public const double MinLy = 5.0;

    // Raw, download-bound rows kept in memory so the distance slider can re-filter instantly.
    static List<HygRow>? _cachedHyg;
    static List<ExoRow>? _cachedExo;

    static readonly string[] HygUrls =
    {
        "https://raw.githubusercontent.com/astronexus/HYG-Database/main/hyg/CURRENT/hyg_v42.csv",
        "https://raw.githubusercontent.com/astronexus/HYG-Database/main/hyg/CURRENT/hyg_v41.csv",
        "https://raw.githubusercontent.com/astronexus/HYG-Database/main/hyg/CURRENT/hyg_v40.csv",
        "https://raw.githubusercontent.com/astronexus/HYG-Database/main/hyg/CURRENT/hygdata_v3.csv",
        "https://raw.githubusercontent.com/astronexus/HYG-Database/master/hygdata_v3.csv",
    };

    static readonly string ExoUrl =
        "https://exoplanetarchive.ipac.caltech.edu/TAP/sync?query=" +
        Uri.EscapeDataString("select pl_name,hostname,ra,dec,sy_dist,st_spectype,st_teff,sy_vmag " +
                             "from pscomppars where sy_dist<" + (MaxPc + 0.02).ToString("0.00", CultureInfo.InvariantCulture)) +
        "&format=csv";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("StarMap3D/1.0");
        return c;
    }

    static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarMap3D");

    // ------------------------------------------------------------------ rows

    sealed class HygRow
    {
        public string Proper = "", Bayer = "", Gl = "", Hip = "", Hd = "", Spect = "";
        public double RaDeg, DecDeg, DistPc, Mag = double.NaN, AbsMag = double.NaN, Ci = double.NaN;
    }

    sealed class ExoRow
    {
        public string Planet = "", Host = "", Spect = "";
        public double RaDeg, DecDeg, DistPc, Teff = double.NaN, Vmag = double.NaN;
    }

    // ------------------------------------------------------------------ entry point

    public static async Task<LoadResult> LoadAsync(bool refresh, IProgress<string>? progress = null,
        string? localHygPath = null, double maxLy = DefaultLy)
    {
        Directory.CreateDirectory(CacheDir);
        string hygCache = Path.Combine(CacheDir, "hyg_near.csv");
        string exoCache = Path.Combine(CacheDir, "exoplanets.csv");

        List<HygRow>? hyg = null;
        List<ExoRow>? exo = null;
        var notes = new List<string>();
        var errors = new List<string>();

        // ---- star catalogue
        if (localHygPath != null)
        {
            try
            {
                hyg = await Task.Run(() => ReadHygFile(localHygPath));
                if (hyg.Count >= 50) WriteHygCache(hygCache, hyg);
                else { errors.Add($"'{Path.GetFileName(localHygPath)}': only {hyg.Count} stars within {MaxDownloadLy:0} ly found (needs HYG columns ra, dec, dist)."); hyg = null; }
            }
            catch (Exception ex) { errors.Add("Could not read the file: " + ex.Message); hyg = null; }
        }
        if (hyg == null && !refresh && File.Exists(hygCache))
            hyg = await Task.Run(() => TryRead(() => ReadHygFile(hygCache)));
        if (hyg == null || hyg.Count < 50)
        {
            progress?.Report("Downloading star catalogue (HYG)...");
            hyg = await DownloadHygAsync(hygCache, errors);
            if (hyg == null && File.Exists(hygCache))
            {
                hyg = await Task.Run(() => TryRead(() => ReadHygFile(hygCache)));
                notes.Add("Star catalogue download failed - using cached copy.");
            }
        }

        // ---- exoplanets
        if (!refresh && File.Exists(exoCache))
            exo = await Task.Run(() => TryRead(() => ReadExoFile(exoCache)));
        if (exo == null || exo.Count == 0)
        {
            progress?.Report("Downloading exoplanet list (NASA Exoplanet Archive)...");
            exo = await DownloadExoAsync(exoCache, errors);
            if (exo == null && File.Exists(exoCache))
            {
                exo = await Task.Run(() => TryRead(() => ReadExoFile(exoCache)));
                notes.Add("Exoplanet download failed - using cached copy.");
            }
        }

        bool haveHyg = hyg != null && hyg.Count >= 50;
        bool haveExo = exo != null && exo.Count > 0;
        _cachedHyg = haveHyg ? hyg : null;
        _cachedExo = haveExo ? exo : null;

        return Assemble(maxLy, errors, notes);
    }

    /// <summary>
    /// Re-filters the already-downloaded catalogues to a new maximum distance, with no network
    /// access at all. Call this whenever the user moves the distance slider.
    /// </summary>
    public static LoadResult ApplyDistance(double maxLy) => Assemble(maxLy, new List<string>(), new List<string>());

    static LoadResult Assemble(double maxLy, List<string> errors, List<string> notes)
    {
        maxLy = Math.Clamp(maxLy, MinLy, MaxDownloadLy);
        var result = new LoadResult();
        string err = errors.Count == 0 ? "" : "\nProblems: " + string.Join(" | ", errors.Take(6));
        bool haveHyg = _cachedHyg != null;
        bool haveExo = _cachedExo != null;

        if (haveHyg)
        {
            result.Stars = Build(_cachedHyg!, _cachedExo ?? new List<ExoRow>(), maxLy);
            result.Status = $"Showing {result.Stars.Count:N0} stars within {maxLy:0.#} ly, from the HYG catalogue" +
                            (haveExo ? " and NASA Exoplanet Archive." : "; exoplanet list unavailable (no rings).");
            if (notes.Count > 0) result.Status += " " + string.Join(" ", notes);
            if (!haveExo) result.Status += err;
        }
        else if (haveExo)
        {
            result.Stars = Build(new List<HygRow>(), _cachedExo!, maxLy);
            result.Status = "PARTIAL: star catalogue unavailable, showing only the Sun and the planet-hosting stars from " +
                            "NASA within " + maxLy.ToString("0.#") + " ly. Use 'Load catalogue CSV...' or 'Refresh data'." + err;
        }
        else
        {
            result.Stars = BuildFallback(maxLy);
            result.Status = "OFFLINE FALLBACK: could not download catalogues, showing well-known nearby stars " +
                            "within " + maxLy.ToString("0.#") + " ly (data only goes out to about 51 ly) with approximate positions." + err;
        }
        return result;
    }

    static T? TryRead<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    // ------------------------------------------------------------------ downloads

    static async Task<List<string>> DiscoverHygUrlsAsync(List<string> errors)
    {
        var found = new List<string>();
        foreach (var dir in new[] { "hyg/CURRENT", "hyg", "" })
        {
            try
            {
                string json = await Http.GetStringAsync("https://api.github.com/repos/astronexus/HYG-Database/contents/" + dir);
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string? url = el.TryGetProperty("download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (url == null || !name.Contains("hyg", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("subset", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
                        found.Add(url);
                }
            }
            catch (Exception ex) { errors.Add($"GitHub listing '{dir}': {Short(ex)}"); }
        }
        return found.OrderByDescending(u => u, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Short(Exception ex) => (ex.InnerException?.Message ?? ex.Message).Split('\n')[0];

    static async Task<List<HygRow>?> DownloadHygAsync(string cachePath, List<string> errors)
    {
        var urls = (await DiscoverHygUrlsAsync(errors)).Concat(HygUrls).Distinct().ToList();
        foreach (var url in urls)
        {
            string label = url.Substring(url.LastIndexOf('/') + 1);
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) { errors.Add($"{label}: HTTP {(int)resp.StatusCode}"); continue; }
                using var raw = await resp.Content.ReadAsStreamAsync();
                Stream stream = url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(raw, CompressionMode.Decompress) : raw;
                using var reader = new StreamReader(stream);
                var rows = await Task.Run(() => ParseHyg(reader));
                if (rows.Count < 50) { errors.Add($"{label}: only {rows.Count} usable rows"); continue; }
                WriteHygCache(cachePath, rows);
                return rows;
            }
            catch (Exception ex) { errors.Add($"{label}: {Short(ex)}"); }
        }
        return null;
    }

    static async Task<List<ExoRow>?> DownloadExoAsync(string cachePath, List<string> errors)
    {
        try
        {
            string text = await Http.GetStringAsync(ExoUrl);
            var rows = ParseExo(new StringReader(text));
            if (rows.Count == 0) { errors.Add("NASA Exoplanet Archive returned no usable rows."); return null; }
            File.WriteAllText(cachePath, text);
            return rows;
        }
        catch (Exception ex) { errors.Add("NASA Exoplanet Archive: " + Short(ex)); return null; }
    }

    // ------------------------------------------------------------------ parsing

    static List<HygRow> ReadHygFile(string path)
    {
        using var r = new StreamReader(path);
        return ParseHyg(r);
    }

    static List<ExoRow> ReadExoFile(string path)
    {
        using var r = new StreamReader(path);
        return ParseExo(r);
    }

    static Dictionary<string, int> HeaderMap(string header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cols = SplitCsv(header);
        for (int i = 0; i < cols.Count; i++) map[cols[i].Trim()] = i;
        return map;
    }

    static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    static List<HygRow> ParseHyg(TextReader reader)
    {
        var rows = new List<HygRow>();
        string? header = reader.ReadLine();
        if (header == null) return rows;
        var map = HeaderMap(header);
        int Idx(string n) => map.TryGetValue(n, out var i) ? i : -1;
        int iRa = Idx("ra"), iDec = Idx("dec"), iDist = Idx("dist");
        if (iRa < 0 || iDec < 0 || iDist < 0) return rows;
        int iProper = Idx("proper"), iBf = Idx("bf"), iGl = Idx("gl"), iHip = Idx("hip"), iHd = Idx("hd");
        int iMag = Idx("mag"), iAbs = Idx("absmag"), iSp = Idx("spect"), iCi = Idx("ci");

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";

            double dist = Num(Get(iDist));
            if (!(dist > 0.0001) || dist > MaxPc) continue;   // skips the Sun (dist 0) and unknown (dist 100000)
            double ra = Num(Get(iRa)), dec = Num(Get(iDec));
            if (double.IsNaN(ra) || double.IsNaN(dec)) continue;

            rows.Add(new HygRow
            {
                Proper = Get(iProper), Bayer = Get(iBf), Gl = Get(iGl), Hip = Get(iHip), Hd = Get(iHd),
                RaDeg = ra * 15.0,   // HYG right ascension is in decimal hours
                DecDeg = dec, DistPc = dist,
                Mag = Num(Get(iMag)), AbsMag = Num(Get(iAbs)), Spect = Get(iSp), Ci = Num(Get(iCi)),
            });
        }
        return rows;
    }

    static void WriteHygCache(string path, List<HygRow> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("proper,bf,gl,hip,hd,ra,dec,dist,mag,absmag,spect,ci");
        foreach (var r in rows)
        {
            sb.Append(Q(r.Proper)).Append(',').Append(Q(r.Bayer)).Append(',').Append(Q(r.Gl)).Append(',')
              .Append(Q(r.Hip)).Append(',').Append(Q(r.Hd)).Append(',')
              .Append((r.RaDeg / 15.0).ToString("R", inv)).Append(',').Append(r.DecDeg.ToString("R", inv)).Append(',')
              .Append(r.DistPc.ToString("R", inv)).Append(',')
              .Append(double.IsNaN(r.Mag) ? "" : r.Mag.ToString("R", inv)).Append(',')
              .Append(double.IsNaN(r.AbsMag) ? "" : r.AbsMag.ToString("R", inv)).Append(',')
              .Append(Q(r.Spect)).Append(',')
              .Append(double.IsNaN(r.Ci) ? "" : r.Ci.ToString("R", inv)).AppendLine();
        }
        File.WriteAllText(path, sb.ToString());

        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    static List<ExoRow> ParseExo(TextReader reader)
    {
        var rows = new List<ExoRow>();
        string? header;
        do { header = reader.ReadLine(); } while (header != null && header.StartsWith('#'));
        if (header == null) return rows;
        var map = HeaderMap(header);
        int Idx(string n) => map.TryGetValue(n, out var i) ? i : -1;
        int iPl = Idx("pl_name"), iHost = Idx("hostname"), iRa = Idx("ra"), iDec = Idx("dec"),
            iDist = Idx("sy_dist"), iSp = Idx("st_spectype"), iT = Idx("st_teff"), iV = Idx("sy_vmag");
        if (iHost < 0 || iRa < 0 || iDec < 0 || iDist < 0) return rows;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = SplitCsv(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            double ra = Num(Get(iRa)), dec = Num(Get(iDec)), d = Num(Get(iDist));
            if (double.IsNaN(ra) || double.IsNaN(dec) || double.IsNaN(d) || d <= 0 || d > MaxPc) continue;
            rows.Add(new ExoRow
            {
                Planet = Get(iPl), Host = Get(iHost), RaDeg = ra, DecDeg = dec, DistPc = d,
                Spect = Get(iSp), Teff = Num(Get(iT)), Vmag = Num(Get(iV)),
            });
        }
        return rows;
    }

    static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }

    // ------------------------------------------------------------------ merging

    static List<Star> Build(List<HygRow> hyg, List<ExoRow> exo, double maxLy)
    {
        var stars = new List<Star>(hyg.Count + 64);

        var sun = new Star
        {
            Name = "Sun", Designations = "Sol", Spectral = "G2V", Mag = -26.74, AbsMag = 4.83,
            RaDeg = 0, DecDeg = 0, DistPc = 0, IsNamed = true,
        };
        sun.PlanetNames.AddRange(SolarSystemPlanets);
        sun.UpdateCartesian();
        sun.UpdateAppearance();
        stars.Add(sun);

        foreach (var r in hyg)
        {
            var s = new Star
            {
                Name = PickName(r),
                Designations = Designations(r),
                RaDeg = r.RaDeg, DecDeg = r.DecDeg, DistPc = r.DistPc,
                Spectral = r.Spect, Mag = r.Mag, AbsMag = r.AbsMag,
                IsNamed = r.Proper.Length > 0,
            };
            s.UpdateAppearance(double.NaN, r.Ci);
            stars.Add(s);
        }

        // Attach each planetary system to the matching catalogue star (by sky position + distance),
        // or add it as a new star if the catalogue does not contain it (many faint red dwarfs).
        int hygCount = stars.Count;
        foreach (var g in exo.GroupBy(e => e.Host))
        {
            var first = g.First();
            double ra = g.Average(e => e.RaDeg), dec = g.Average(e => e.DecDeg), d = first.DistPc;

            Star? best = null;
            double bestSep = double.MaxValue;
            for (int i = 1; i < hygCount; i++)
            {
                var s = stars[i];
                if (Math.Abs(s.DistPc - d) > 0.06 * d + 0.15) continue;
                double sep = AngularSepDeg(ra, dec, s.RaDeg, s.DecDeg);
                if (sep < 0.035 && sep < bestSep) { bestSep = sep; best = s; }   // < 2 arc-minutes
            }

            var names = g.Select(e => e.Planet).Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList();
            if (names.Count == 0) names.Add(first.Host + " b");

            if (best == null)
            {
                double abs = double.IsNaN(first.Vmag) ? double.NaN : first.Vmag - 5.0 * (Math.Log10(d) - 1.0);
                best = new Star
                {
                    Name = first.Host, Spectral = first.Spect, Mag = first.Vmag, AbsMag = abs,
                    RaDeg = ra, DecDeg = dec, DistPc = d,
                };
                best.UpdateAppearance(first.Teff);
                stars.Add(best);
            }
            else
            {
                // NASA's Gaia-based coordinates/distances are more accurate than the old catalogues.
                best.RaDeg = ra; best.DecDeg = dec; best.DistPc = d;
                if (best.Name.Length == 0) best.Name = first.Host;
                else if (!best.Name.Equals(first.Host, StringComparison.OrdinalIgnoreCase))
                    best.Designations = string.IsNullOrEmpty(best.Designations)
                        ? first.Host : best.Designations + ", " + first.Host;
                if (best.ColorClass == 8) best.UpdateAppearance(first.Teff);
            }
            foreach (var n in names) if (!best.PlanetNames.Contains(n)) best.PlanetNames.Add(n);
            best.IsNamed = true;
        }

        stars.RemoveAll(s => s.DistPc * Star.LyPerPc > maxLy + 0.5);
        foreach (var s in stars) if (s.DistPc > 0) s.UpdateCartesian();
        return stars;
    }

    static double AngularSepDeg(double ra1, double dec1, double ra2, double dec2)
    {
        const double D = Math.PI / 180.0;
        double c = Math.Sin(dec1 * D) * Math.Sin(dec2 * D) +
                   Math.Cos(dec1 * D) * Math.Cos(dec2 * D) * Math.Cos((ra1 - ra2) * D);
        return Math.Acos(Math.Clamp(c, -1, 1)) / D;
    }

    static string PickName(HygRow r)
    {
        if (r.Proper.Length > 0) return r.Proper;
        if (r.Gl.Length > 0) return char.IsDigit(r.Gl[0]) ? "GJ " + r.Gl : r.Gl;
        if (r.Bayer.Length > 0) return r.Bayer;
        if (r.Hip.Length > 0) return "HIP " + r.Hip;
        if (r.Hd.Length > 0) return "HD " + r.Hd;
        return "";
    }

    static string Designations(HygRow r)
    {
        var l = new List<string>();
        if (r.Hip.Length > 0) l.Add("HIP " + r.Hip);
        if (r.Hd.Length > 0) l.Add("HD " + r.Hd);
        if (r.Gl.Length > 0) l.Add(char.IsDigit(r.Gl[0]) ? "GJ " + r.Gl : r.Gl);
        if (r.Bayer.Length > 0) l.Add(r.Bayer);
        return string.Join(", ", l);
    }

    // ------------------------------------------------------------------ offline fallback

    // name, RA (deg), Dec (deg), distance (ly), spectral type, number of known planets. Approximate.
    static readonly (string n, double ra, double dec, double ly, string sp, int pl)[] Fallback =
    {
        ("Proxima Centauri", 217.429, -62.680, 4.24, "M5.5V", 2),
        ("Alpha Centauri A", 219.902, -60.834, 4.37, "G2V", 0),
        ("Alpha Centauri B", 219.896, -60.838, 4.37, "K1V", 0),
        ("Barnard's Star", 269.452, 4.693, 5.96, "M4V", 4),
        ("Wolf 359", 164.120, 7.015, 7.86, "M6V", 2),
        ("Lalande 21185", 165.831, 35.970, 8.31, "M2V", 2),
        ("Sirius", 101.287, -16.716, 8.60, "A1V", 0),
        ("Luyten 726-8", 24.750, -17.950, 8.73, "M5.5V", 0),
        ("Ross 154", 282.460, -23.840, 9.71, "M3.5V", 0),
        ("Ross 248", 355.480, 44.180, 10.32, "M6V", 0),
        ("Epsilon Eridani", 53.233, -9.458, 10.47, "K2V", 1),
        ("Lacaille 9352", 346.470, -35.850, 10.72, "M0.5V", 2),
        ("Ross 128", 176.937, 0.801, 11.01, "M4V", 1),
        ("61 Cygni A", 316.730, 38.750, 11.40, "K5V", 0),
        ("Procyon", 114.830, 5.225, 11.46, "F5IV", 0),
        ("Groombridge 34", 5.600, 44.020, 11.62, "M1.5V", 2),
        ("Epsilon Indi A", 330.840, -56.786, 11.87, "K5V", 1),
        ("Tau Ceti", 26.017, -15.937, 11.91, "G8V", 4),
        ("GJ 1061", 55.110, -44.510, 11.99, "M5.5V", 3),
        ("YZ Ceti", 22.030, -16.980, 12.11, "M4.5V", 3),
        ("Luyten's Star", 116.170, 5.230, 12.35, "M3.5V", 2),
        ("Teegarden's Star", 43.260, 16.880, 12.50, "M7V", 2),
        ("Kapteyn's Star", 77.900, -45.020, 12.83, "M1V", 0),
        ("Kruger 60", 337.000, 57.700, 13.15, "M3V", 0),
        ("Wolf 1061", 247.550, -12.660, 14.05, "M3V", 3),
        ("Gliese 674", 264.110, -46.900, 14.8, "M2.5V", 1),
        ("Gliese 876", 343.320, -14.260, 15.25, "M4V", 4),
        ("GJ 1002", 1.340, -7.540, 15.8, "M5.5V", 2),
        ("Gliese 832", 322.110, -49.010, 16.2, "M1.5V", 1),
        ("Altair", 297.700, 8.870, 16.7, "A7V", 0),
        ("Gliese 581", 229.860, -7.720, 20.5, "M3V", 3),
        ("HD 219134", 348.340, 57.170, 21.3, "K3V", 6),
        ("Gliese 667 C", 259.750, -34.990, 23.6, "M1.5V", 2),
        ("Vega", 279.230, 38.780, 25.0, "A0V", 0),
        ("Fomalhaut", 344.410, -29.620, 25.1, "A3V", 0),
        ("Gliese 436", 175.550, 26.710, 31.9, "M2.5V", 1),
        ("Pollux", 116.330, 28.030, 33.8, "K0III", 1),
        ("Arcturus", 213.920, 19.180, 36.7, "K1.5III", 0),
        ("TRAPPIST-1", 346.620, -5.040, 40.7, "M8V", 7),
        ("Upsilon Andromedae", 24.200, 41.410, 44.0, "F8V", 3),
        ("51 Pegasi", 344.370, 20.770, 50.9, "G2IV", 1),
        ("55 Cancri", 133.150, 28.330, 41.0, "G8V", 5),
    };

    static readonly string[] SolarSystemPlanets =
        { "Mercury", "Venus", "Earth", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune" };

    static List<Star> BuildFallback(double maxLy)
    {
        var stars = new List<Star>();
        var sun = new Star { Name = "Sun", Spectral = "G2V", AbsMag = 4.83, IsNamed = true };
        sun.PlanetNames.AddRange(SolarSystemPlanets);
        sun.UpdateAppearance();
        stars.Add(sun);

        foreach (var (n, ra, dec, ly, sp, pl) in Fallback)
        {
            if (ly > maxLy + 0.5) continue;
            var s = new Star { Name = n, RaDeg = ra, DecDeg = dec, DistPc = ly / Star.LyPerPc, Spectral = sp, IsNamed = true };
            for (int i = 0; i < pl; i++) s.PlanetNames.Add(n + " " + (char)('b' + i));
            s.UpdateAppearance();
            s.UpdateCartesian();
            stars.Add(s);
        }
        return stars;
    }
}
