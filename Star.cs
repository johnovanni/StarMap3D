using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace StarMap3D;

/// <summary>One star (or star system) in the map.</summary>
public sealed class Star
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Extra catalogue designations (HIP / HD / Gliese ...).</summary>
    public string Designations { get; set; } = "";

    // Equatorial position of the star as read from the catalogue.
    public double RaDeg { get; set; }
    public double DecDeg { get; set; }
    public double DistPc { get; set; }

    /// <summary>
    /// Cartesian position in light years, Sun at the origin.
    /// +X points to RA 0h, +Y to RA 6h, +Z to the north celestial pole.
    /// </summary>
    public double X, Y, Z;

    public double DistLy { get; set; }
    public string Spectral { get; set; } = "";
    public double Mag { get; set; } = double.NaN;
    public double AbsMag { get; set; } = double.NaN;

    /// <summary>One entry per known planet; each planet gets one ring on the map.</summary>
    public List<string> PlanetNames { get; } = new();
    public int Planets => PlanetNames.Count;

    /// <summary>True when the star has a proper name, or planets (so it is worth labelling).</summary>
    public bool IsNamed { get; set; }

    // Rendering hints
    public int ColorClass { get; set; } = 8;
    public double BaseRadius { get; set; } = 2.0;

    public const double LyPerPc = 3.26156;

    public void UpdateCartesian()
    {
        DistLy = DistPc * LyPerPc;
        double ra = RaDeg * Math.PI / 180.0;
        double dec = DecDeg * Math.PI / 180.0;
        double cd = Math.Cos(dec);
        X = DistLy * cd * Math.Cos(ra);
        Y = DistLy * cd * Math.Sin(ra);
        Z = DistLy * Math.Sin(dec);
    }

    public void UpdateAppearance(double teff = double.NaN, double bv = double.NaN)
    {
        int c = ClassifySpectral(Spectral);
        if (c < 0 && !double.IsNaN(teff)) c = ClassifyTeff(teff);
        if (c < 0 && !double.IsNaN(bv)) c = ClassifyBV(bv);
        ColorClass = c < 0 ? 8 : c;
        BaseRadius = double.IsNaN(AbsMag)
            ? 2.0
            : Math.Clamp(2.7 - 0.16 * (AbsMag - 4.8), 1.3, 4.2);
    }

    // --- colour classes -------------------------------------------------

    public static readonly string[] ClassNames =
        { "O/B", "A", "F", "G", "K", "M", "L/T", "White dwarf", "Unknown" };

    public static readonly Color[] ClassColors =
    {
        Color.FromRgb(155, 176, 255), // O/B  blue
        Color.FromRgb(202, 215, 255), // A    blue-white
        Color.FromRgb(248, 247, 255), // F    white
        Color.FromRgb(255, 240, 170), // G    yellow
        Color.FromRgb(255, 190, 120), // K    orange
        Color.FromRgb(255, 140, 95),  // M    red-orange
        Color.FromRgb(210, 95, 75),   // L/T  deep red
        Color.FromRgb(195, 225, 255), // D    white dwarf
        Color.FromRgb(215, 215, 215), // unknown
    };

    public static int ClassifySpectral(string spect)
    {
        if (string.IsNullOrWhiteSpace(spect)) return -1;
        foreach (char ch in spect)
        {
            switch (ch)
            {
                case 'O':
                case 'B': return 0;
                case 'A': return 1;
                case 'F': return 2;
                case 'G': return 3;
                case 'K': return 4;
                case 'M': return 5;
                case 'L':
                case 'T':
                case 'Y': return 6;
                case 'D': return 7;
            }
        }
        return -1;
    }

    public static int ClassifyTeff(double t) =>
        t >= 10000 ? 0 : t >= 7500 ? 1 : t >= 6000 ? 2 : t >= 5200 ? 3 : t >= 3900 ? 4 : t >= 2300 ? 5 : 6;

    public static int ClassifyBV(double bv) =>
        bv < 0.0 ? 0 : bv < 0.3 ? 1 : bv < 0.58 ? 2 : bv < 0.81 ? 3 : bv < 1.4 ? 4 : 5;
}
