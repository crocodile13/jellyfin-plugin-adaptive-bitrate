using AdaptiveBitrate.Core;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdaptiveBitrate.Configuration;

/// <summary>Langue de la page de configuration.</summary>
public enum UiLanguage
{
    /// <summary>Celle de l'interface Jellyfin de chacun.</summary>
    System,

    /// <summary>Français.</summary>
    French,

    /// <summary>Anglais.</summary>
    English,

    /// <summary>Allemand.</summary>
    German,

    /// <summary>Espagnol.</summary>
    Spanish,
}

/// <summary>Réglages du plugin (bornés à l'usage par <see cref="Ladder"/>).</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Plugin actif : coupé, Jellyfin sert ses playlists telles quelles.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lecteurs concernés : hors du réseau local seulement, ou tous.</summary>
    public Scope Scope { get; set; } = Scope.Remote;

    /// <summary>Nombre maximal de qualités proposées sous celle de départ.</summary>
    public int MaxRungs { get; set; } = 4;

    /// <summary>Rapport de débit entre deux qualités voisines (0,6 : chacune à 60 % de celle du dessus).</summary>
    public double Step { get; set; } = 0.6;

    /// <summary>Débit vidéo minimal d'une qualité (kb/s).</summary>
    public int MinBitrateKbps { get; set; } = 700;

    /// <summary>Qualités au-dessus de celle de départ (le réseau s'améliore après le test de début de lecture).</summary>
    public int RungsAbove { get; set; }

    /// <summary>Plafond des qualités du dessus (kb/s ; 0 = le débit du fichier).</summary>
    public int AboveCapKbps { get; set; }

    /// <summary>Applications jamais concernées (une par ligne ou séparées par des virgules ; nom contenu dans celui de l'application).</summary>
    public string ExcludedClients { get; set; } = string.Empty;

    /// <summary>
    /// Lecteurs hors du réseau local : le flux adaptatif (HLS) plutôt que la lecture directe du fichier, pour qu'une baisse
    /// du réseau ne coupe pas la lecture. Expérimental, coupé par défaut.
    /// </summary>
    public bool PreferAdaptive { get; set; }

    /// <summary>Langue de la page de configuration.</summary>
    public UiLanguage Language { get; set; } = UiLanguage.System;

    /// <summary>Réglages de l'échelle pour le moteur.</summary>
    public LadderOptions LadderOptions() => new LadderOptions
    {
        MaxRungs = MaxRungs, Step = Step, MinBitrate = MinBitrateKbps * 1000, RungsAbove = RungsAbove, AboveCap = AboveCapKbps * 1000,
    }.Sanitized();
}
