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

/// <summary>Qui voit la surcouche de diagnostic pendant une lecture (client web).</summary>
public enum DebugOverlay
{
    /// <summary>Personne.</summary>
    Off,

    /// <summary>Les administrateurs seulement.</summary>
    Admins,

    /// <summary>Tout le monde.</summary>
    Everyone,
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

    /// <summary>Écart maximal entre deux qualités voisines, en rapport de débit (0,5 : chacune a au moins la moitié du débit de celle du dessus).</summary>
    public double Step { get; set; } = 0.5;

    /// <summary>Réglages passés aux valeurs de la version en cours (migration, une seule fois).</summary>
    public int SettingsVersion { get; set; }

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

    /// <summary>Surcouche de diagnostic pendant une lecture dans le navigateur (qualité, débit reçu, tampon…).</summary>
    public DebugOverlay DebugOverlay { get; set; } = DebugOverlay.Off;

    /// <summary>
    /// Le serveur pilote la qualité envoyée (sinon il mesure seulement, pour la surcouche et la page). Coupé par défaut :
    /// vérifié dans le navigateur (changements de qualité réduits d'un quart, sans coupure de plus), pas encore sur les
    /// applications TV.
    /// </summary>
    public bool ServerControl { get; set; }

    /// <summary>Après une descente, secondes avant de laisser remonter.</summary>
    public int HoldAfterDownSeconds { get; set; } = 30;

    /// <summary>Secondes au moins entre deux changements décidés par le serveur (0 = automatique, selon la durée des segments).</summary>
    public int MinChangeSeconds { get; set; }

    /// <summary>Descendre d'avance quand le débit mesuré ne suffit plus (coupé : voir <see cref="GovernorOptions.AnticipateDown"/>).</summary>
    public bool AnticipateDown { get; set; }

    /// <summary>Marge de débit (une qualité n'est gardée ou atteinte que si le débit mesuré dépasse son débit × cette marge ; 0 = automatique, selon la régularité de la connexion).</summary>
    public double Margin { get; set; }

    /// <summary>Segments mesurés au moins avant une décision du serveur (0 = automatique, selon la durée des segments).</summary>
    public int MinSamples { get; set; }

    /// <summary>Réglages du pilotage pour le moteur.</summary>
    public GovernorOptions GovernorOptions() => new GovernorOptions
    {
        Enabled = ServerControl, HoldAfterDownSeconds = HoldAfterDownSeconds, MinChangeSeconds = MinChangeSeconds,
        AnticipateDown = AnticipateDown, Margin = Margin, MinSamples = MinSamples,
    }.Sanitized();

    /// <summary>Réglages de l'échelle pour le moteur.</summary>
    public LadderOptions LadderOptions() => new LadderOptions
    {
        MaxRungs = MaxRungs, Step = Step, MinBitrate = MinBitrateKbps * 1000, RungsAbove = RungsAbove, AboveCap = AboveCapKbps * 1000,
    }.Sanitized();
}
