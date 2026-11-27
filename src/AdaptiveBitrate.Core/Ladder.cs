using System;
using System.Collections.Generic;
using System.Linq;

namespace AdaptiveBitrate.Core;

/// <summary>Une marche de l'échelle : débit vidéo demandé à Jellyfin et taille maximale de l'image.</summary>
/// <param name="VideoBitrate">Débit vidéo (bits par seconde).</param>
/// <param name="MaxHeight">Hauteur maximale (pixels, paire).</param>
/// <param name="MaxWidth">Largeur maximale (pixels, paire), aux proportions de la source ; 0 = inconnue.</param>
public sealed record Rung(int VideoBitrate, int MaxHeight, int MaxWidth = 0);

/// <summary>Réglages de l'échelle (bornés par <see cref="Sanitized"/>).</summary>
public sealed class LadderOptions
{
    /// <summary>Nombre maximal de marches sous la qualité d'origine.</summary>
    public int MaxRungs { get; set; } = 4;

    /// <summary>Rapport de débit entre deux marches voisines (0,6 : chaque marche a 60 % du débit de celle du dessus).</summary>
    public double Step { get; set; } = 0.6;

    /// <summary>Débit vidéo minimal d'une marche (bits par seconde) : en dessous, l'image ne vaut plus la peine.</summary>
    public int MinBitrate { get; set; } = 700_000;

    /// <summary>Marches au-dessus du débit demandé par le lecteur (le réseau s'améliore après le test de début de lecture).</summary>
    public int RungsAbove { get; set; }

    /// <summary>Plafond des marches du dessus (bits par seconde ; 0 = le débit du fichier).</summary>
    public int AboveCap { get; set; }

    /// <summary>Copie bornée : un réglage hors bornes (fichier édité à la main) ne casse rien.</summary>
    public LadderOptions Sanitized() => new()
    {
        MaxRungs = Math.Clamp(MaxRungs, 0, 8),
        Step = double.IsFinite(Step) ? Math.Clamp(Step, 0.3, 0.9) : 0.6,
        MinBitrate = Math.Clamp(MinBitrate, 100_000, 50_000_000),
        RungsAbove = Math.Clamp(RungsAbove, 0, 3),
        AboveCap = Math.Clamp(AboveCap, 0, 400_000_000),
    };
}

/// <summary>Ce que l'on sait de la vidéo d'origine.</summary>
/// <param name="Width">Largeur (pixels ; 0 = inconnue).</param>
/// <param name="Height">Hauteur (pixels ; 0 = inconnue).</param>
/// <param name="Bitrate">Débit vidéo du fichier (bits par seconde ; 0 = inconnu).</param>
/// <param name="Fps">Images par seconde (0 = inconnu).</param>
public sealed record SourceVideo(int Width, int Height, int Bitrate, double Fps);

/// <summary>
/// Échelle de qualités proposée au lecteur, sous (et en option au-dessus de) la qualité que Jellyfin a choisie. Fonction
/// pure : les mêmes entrées donnent toujours la même échelle.
/// </summary>
public static class Ladder
{
    /// <summary>Repère de la courbe hauteur / débit : 1080 lignes à 5,8 Mb/s (30 images/s), comme les échelles courantes.</summary>
    private const double RefHeight = 1080, RefBitrate = 5_800_000;

    /// <summary>
    /// Exposant de la courbe : la hauteur croît comme le débit à la puissance 0,52 (1080p à 5,8 Mb/s, 720p vers 2,7 Mb/s,
    /// 360p vers 0,7 Mb/s). Continue, sans paliers.
    /// </summary>
    private const double Exponent = 0.52;

    /// <summary>
    /// Hauteur d'image adaptée à un débit : la courbe des échelles courantes, ramenée à 30 images/s (une vidéo à 60
    /// images/s demande deux fois plus de débit pour la même image), jamais plus que la source, paire.
    /// </summary>
    public static int HeightFor(int bitrate, SourceVideo src)
    {
        double fps = src.Fps > 0 ? src.Fps : 30;
        double effective = bitrate * Math.Min(1.0, 30.0 / Math.Max(fps, 1));
        double h = RefHeight * Math.Pow(Math.Max(effective, 1) / RefBitrate, Exponent);
        if (src.Height > 0)
        {
            h = Math.Min(h, src.Height);
        }

        int even = (int)Math.Round(h / 2) * 2;
        return Math.Max(even, 144);
    }

    /// <summary>Largeur qui va avec une hauteur, aux proportions de la source (paire ; 0 si la source est inconnue).</summary>
    public static int WidthFor(int height, SourceVideo src)
        => src.Width > 0 && src.Height > 0 ? (int)Math.Round(height * (double)src.Width / src.Height / 2) * 2 : 0;

    /// <summary>
    /// Marches à proposer, de la plus haute à la plus basse, sans la qualité d'origine (celle que Jellyfin a déjà mise
    /// dans la playlist). Le débit de départ est le plus petit de celui que le lecteur a demandé et de celui du fichier :
    /// en copie de la vidéo, le lecteur web demande un plafond sans rapport avec le fichier (866 Mb/s mesurés), et une
    /// échelle partie de là restait tout entière au-dessus du fichier (aucune qualité plus basse en réalité).
    /// </summary>
    /// <param name="requested">Débit vidéo demandé par le lecteur (bits par seconde ; null ou 0 = inconnu).</param>
    /// <param name="src">Vidéo d'origine.</param>
    /// <param name="options">Réglages de l'échelle.</param>
    public static List<Rung> Rungs(int? requested, SourceVideo src, LadderOptions options)
    {
        var o = options.Sanitized();
        var res = new List<Rung>();
        int top = requested is > 0 ? (src.Bitrate > 0 ? Math.Min(requested.Value, src.Bitrate) : requested.Value) : src.Bitrate;
        if (top <= 0)
        {
            return res;
        }

        // au-dessus : le réseau s'est amélioré depuis le test de début de lecture (seulement si le fichier le permet)
        int ceiling = src.Bitrate > 0 ? src.Bitrate : int.MaxValue;
        if (o.AboveCap > 0)
        {
            ceiling = Math.Min(ceiling, o.AboveCap);
        }

        var above = new List<Rung>();
        double up = top;
        for (int i = 0; i < o.RungsAbove; i++)
        {
            up /= o.Step;
            if (up > ceiling * 1.05)
            {
                break;
            }

            above.Insert(0, new Rung((int)Math.Round(up), HeightFor((int)Math.Round(up), src)));
        }

        res.AddRange(above);

        // en dessous : chaque marche a Step fois le débit de celle du dessus ; s'il faut plus de MaxRungs marches pour
        // descendre jusqu'au débit minimal, l'écart s'élargit pour que la dernière l'atteigne quand même (mesuré : un
        // fichier à 8 Mb/s s'arrêtait à 1,04 Mb/s, trop près d'une connexion à 1,6 Mb/s)
        if (o.MaxRungs > 0 && top * o.Step >= o.MinBitrate)
        {
            double needed = Math.Log((double)o.MinBitrate / top) / Math.Log(o.Step);
            bool widen = needed > o.MaxRungs;
            int n = widen ? o.MaxRungs : (int)Math.Floor(needed + 1e-9);
            double step = widen ? Math.Pow((double)o.MinBitrate / top, 1.0 / n) : o.Step;
            for (int i = 1; i <= n; i++)
            {
                int b = (int)Math.Round(top * Math.Pow(step, i));
                res.Add(new Rung(b, HeightFor(b, src)));
            }
        }

        return res.Select(r => r with { MaxWidth = WidthFor(r.MaxHeight, src) }).ToList();
    }
}
