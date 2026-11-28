using System;
using System.Collections.Generic;
using System.Linq;

namespace AdaptiveBitrate.Core;

/// <summary>Réglages du pilotage par le serveur (bornés par <see cref="Sanitized"/>).</summary>
public sealed class GovernorOptions
{
    /// <summary>Le serveur pilote la qualité envoyée (sinon il ne fait que mesurer).</summary>
    public bool Enabled { get; set; }

    /// <summary>Après une descente, secondes avant de laisser remonter.</summary>
    public double HoldAfterDownSeconds { get; set; } = 30;

    /// <summary>
    /// Secondes au moins entre deux changements (sauf une descente demandée par le lecteur : toujours immédiate). 0 =
    /// automatique : 2,5 segments (le temps de voir si le réseau a vraiment changé), entre 6 et 30 s.
    /// </summary>
    public double MinChangeSeconds { get; set; }

    /// <summary>
    /// Descendre d'avance quand le débit mesuré ne suffit plus, avant que le tampon du lecteur ne se vide. Coupé par défaut :
    /// sur le banc (lien bridé à 1,6 Mb/s), la mesure du serveur réagit après le lecteur et surestime le débit quand les
    /// segments sont petits (tampons du système) ; elle ne s'est jamais déclenchée.
    /// </summary>
    public bool AnticipateDown { get; set; }

    /// <summary>
    /// Marge de débit : une qualité n'est gardée ou atteinte que si le débit mesuré dépasse son débit × cette marge. 0 =
    /// automatique : 1 + deux fois la variabilité mesurée de la connexion (écart type / moyenne des derniers segments),
    /// entre 1,1 et 2 ; 1,25 tant qu'il n'y a pas trois mesures.
    /// </summary>
    public double Margin { get; set; }

    /// <summary>Segments mesurés au moins avant toute décision du serveur. 0 = automatique : de quoi couvrir 6 s de vidéo (1 à 5).</summary>
    public int MinSamples { get; set; }

    /// <summary>Copie bornée.</summary>
    public GovernorOptions Sanitized() => new()
    {
        Enabled = Enabled,
        HoldAfterDownSeconds = Finite(HoldAfterDownSeconds, 30, 0, 600),
        MinChangeSeconds = Finite(MinChangeSeconds, 0, 0, 300),
        AnticipateDown = AnticipateDown,
        Margin = Margin <= 0 || !double.IsFinite(Margin) ? 0 : Math.Clamp(Margin, 1.0, 3.0),
        MinSamples = Math.Clamp(MinSamples, 0, 10),
    };

    private static double Finite(double v, double d, double lo, double hi) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : d;
}

/// <summary>Ce que le serveur décide pour un segment demandé.</summary>
public enum Verdict
{
    /// <summary>La qualité demandée par le lecteur.</summary>
    AsRequested,

    /// <summary>Le lecteur voulait remonter trop tôt (ou sans débit suffisant) : la qualité en cours est gardée.</summary>
    Held,

    /// <summary>Le débit mesuré ne suffit plus : une qualité plus basse, avant que le tampon ne se vide.</summary>
    Anticipated,
}

/// <summary>Décision pour un segment : la marche à servir (indice dans l'échelle, 0 = la plus haute) et pourquoi.</summary>
/// <param name="Rung">Marche servie.</param>
/// <param name="Verdict">Raison.</param>
public sealed record Decision(int Rung, Verdict Verdict);

/// <summary>
/// Mesure et pilotage d'une lecture par le serveur, segment par segment. Mesure : le temps d'envoi de chaque segment au
/// lecteur (débit réel, sans trafic en plus) et l'avance du lecteur déduite du rythme de ses demandes (durée de vidéo
/// demandée moins temps écoulé ; une pause ou un saut repart de zéro). Pilotage (si activé) : une descente demandée par
/// le lecteur est toujours servie tout de suite ; une remontée attend <see cref="GovernorOptions.HoldAfterDownSeconds"/>
/// après une descente, se fait d'une marche à la fois et seulement si le débit mesuré la permet ; et, si le débit ne
/// suffit plus pour la qualité en cours, le serveur descend d'avance, pendant que le lecteur a encore de l'avance (le
/// nouvel encodage démarre à l'abri de son tampon ; Jellyfin arrête l'ancien : jamais deux encodages pour une lecture).
/// Pur : l'horloge est passée par l'appelant.
/// </summary>
public sealed class Governor
{
    private const int Window = 3;   // segments du débit lissé
    private const int Spread = 6;   // segments de la variabilité (marge automatique)

    private static readonly object StartupLock = new();
    private static double _startup = double.NaN;
    private static readonly Queue<double> StartupSamples = new();

    private readonly IReadOnlyList<int> _bandwidth;   // débit annoncé de chaque marche (bits/s), du plus haut au plus bas
    private readonly Queue<double> _rates = new();
    private double _segment = 3;
    private double _lastChange = double.NegativeInfinity, _lastDown = double.NegativeInfinity;
    private double _anchorWall = double.NaN, _anchorMedia, _media, _lastRequest = double.NaN;
    private int _lastSegment = -2;
    private bool _holding;

    /// <param name="bandwidth">Débit annoncé de chaque marche (bits/s), de la plus haute à la plus basse.</param>
    public Governor(IReadOnlyList<int> bandwidth)
    {
        _bandwidth = bandwidth;
        Current = 0;
    }

    /// <summary>Marche servie en dernier.</summary>
    public int Current { get; private set; }

    /// <summary>Débit d'envoi lissé (bits/s) sur les derniers segments, ou null avant la première mesure.</summary>
    public double? Rate => _rates.Count > 0 ? Recent.Count / Recent.Sum(r => 1 / r) : null;   // moyenne harmonique : prudente

    private List<double> Recent => _rates.Skip(Math.Max(0, _rates.Count - Window)).ToList();

    /// <summary>
    /// Temps de démarrage d'un encodage sur ce serveur (secondes, lissé sur les changements de qualité de toutes les
    /// lectures), ou null avant le premier : rapide avec une carte graphique, plus lent sur le processeur.
    /// </summary>
    public static double? Startup
    {
        get
        {
            lock (StartupLock)
            {
                return double.IsNaN(_startup) ? null : _startup;
            }
        }
    }

    /// <summary>Un encodage a démarré en <paramref name="seconds"/> secondes (premier segment d'une nouvelle qualité).</summary>
    public static void ReportStartup(double seconds)
    {
        if (!(seconds > 0) || !double.IsFinite(seconds) || seconds > 120)
        {
            return;
        }

        lock (StartupLock)
        {
            _startup = double.IsNaN(_startup) ? seconds : (0.7 * _startup) + (0.3 * seconds);
            StartupSamples.Enqueue(seconds);
            while (StartupSamples.Count > 10)
            {
                StartupSamples.Dequeue();
            }
        }
    }

    /// <summary>Dernières mesures brutes du démarrage d'un encodage (secondes, la plus ancienne d'abord ; diagnostic).</summary>
    public static double[] RecentStartups()
    {
        lock (StartupLock)
        {
            return StartupSamples.ToArray();
        }
    }

    /// <summary>Remet à zéro le temps de démarrage mesuré (tests).</summary>
    public static void ResetStartup()
    {
        lock (StartupLock)
        {
            _startup = double.NaN;
            StartupSamples.Clear();
        }
    }

    /// <summary>Écart minimal entre deux changements réellement appliqué (réglé, ou automatique).</summary>
    public double MinChangeFor(GovernorOptions o) => o.MinChangeSeconds > 0 ? o.MinChangeSeconds : Math.Clamp(2.5 * _segment, 6, 30);

    /// <summary>Segments mesurés avant une décision, réellement appliqués.</summary>
    public int MinSamplesFor(GovernorOptions o) => o.MinSamples > 0 ? o.MinSamples : Math.Clamp((int)Math.Ceiling(6 / Math.Max(_segment, 0.5)), 1, 5);

    /// <summary>Marge de débit réellement appliquée (réglée, ou d'après la variabilité mesurée de la connexion).</summary>
    public double MarginFor(GovernorOptions o)
    {
        if (o.Margin > 0)
        {
            return o.Margin;
        }

        if (_rates.Count < 3)
        {
            return 1.25;
        }

        double mean = _rates.Average();
        double sd = Math.Sqrt(_rates.Sum(r => (r - mean) * (r - mean)) / _rates.Count);
        return 1 + Math.Clamp(2 * sd / mean, 0.1, 1.0);
    }

    /// <summary>Segments mesurés.</summary>
    public int Samples => _rates.Count;

    /// <summary>Avance estimée du lecteur (secondes de vidéo demandées d'avance sur le temps écoulé), ou null au départ.</summary>
    public double? Lead { get; private set; }

    /// <summary>Remontées retenues : fois où le serveur a commencé à garder la qualité en cours (pas chaque segment gardé).</summary>
    public int Held { get; private set; }

    /// <summary>Descentes anticipées par le serveur.</summary>
    public int Anticipated { get; private set; }

    /// <summary>
    /// Le lecteur demande le segment <paramref name="segment"/> (durée <paramref name="seconds"/>) de la marche
    /// <paramref name="requested"/> à l'instant <paramref name="now"/> (secondes) : marche à servir.
    /// </summary>
    public Decision Decide(int requested, int segment, double seconds, double now, GovernorOptions options)
    {
        var o = options.Sanitized();
        requested = Math.Clamp(requested, 0, _bandwidth.Count - 1);
        if (seconds > 0)
        {
            _segment = seconds;
        }

        Follow(segment, seconds, now);
        if (!o.Enabled)
        {
            return Serve(requested, Verdict.AsRequested, now);
        }

        int cur = Current;
        bool measured = Recent.Count >= Math.Min(MinSamplesFor(o), Window) && _rates.Count >= MinSamplesFor(o) && Rate is double;
        double rate = Rate ?? 0;
        double margin = MarginFor(o), minChange = MinChangeFor(o);

        // le lecteur descend : toujours tout de suite (le retarder ferait des coupures)
        if (requested > cur)
        {
            return Serve(requested, Verdict.AsRequested, now);
        }

        // le débit ne suffit plus pour la qualité en cours : descendre d'avance, jusqu'à une marche qui passe, quand
        // l'avance du lecteur ne couvre plus le démarrage d'un encodage plus le téléchargement du segment suivant à ce
        // débit (avance inconnue : tout de suite)
        bool tight = Lead is not double lead || lead < (Startup ?? 2) + (_segment * _bandwidth[cur] / Math.Max(rate, 1)) + _segment;
        if (o.AnticipateDown && measured && rate < _bandwidth[cur] * margin && tight && now - _lastChange >= minChange && cur < _bandwidth.Count - 1)
        {
            int fit = cur + 1;
            while (fit < _bandwidth.Count - 1 && rate < _bandwidth[fit] * margin)
            {
                fit++;
            }

            Anticipated++;
            return Serve(Math.Max(fit, requested), Verdict.Anticipated, now);
        }

        // le lecteur (ou le serveur) voudrait remonter : d'une marche à la fois, et seulement quand c'est sûr
        if (requested < cur)
        {
            bool early = now - _lastDown < o.HoldAfterDownSeconds || now - _lastChange < minChange;
            int next = cur - 1;
            bool fits = !measured || rate >= _bandwidth[next] * margin;
            if (early || !fits)
            {
                if (!_holding)
                {
                    Held++;
                }

                _holding = true;
                return Serve(cur, Verdict.Held, now);
            }

            return Serve(next, next == requested ? Verdict.AsRequested : Verdict.Held, now);
        }

        return Serve(cur, Verdict.AsRequested, now);
    }

    /// <summary>Un segment a été envoyé : <paramref name="bytes"/> octets en <paramref name="seconds"/> secondes.</summary>
    public void Sent(long bytes, double seconds)
    {
        // envois trop courts pour être mesurés (tampons du système) : un plancher de 5 ms
        if (bytes <= 0)
        {
            return;
        }

        _rates.Enqueue(bytes * 8 / Math.Max(seconds, 0.005));
        while (_rates.Count > Spread)
        {
            _rates.Dequeue();
        }
    }

    private Decision Serve(int rung, Verdict v, double now)
    {
        if (v != Verdict.Held || rung != Current)
        {
            _holding = v == Verdict.Held && rung == Current;
        }

        if (rung != Current)
        {
            if (rung > Current)
            {
                _lastDown = now;
            }

            _lastChange = now;
            Current = rung;
        }

        return new Decision(rung, v);
    }

    // avance du lecteur : vidéo demandée depuis l'ancrage, moins le temps écoulé ; un saut (segment non consécutif) ou une
    // longue pause (plus de deux segments sans demande) repart de zéro
    private void Follow(int segment, double seconds, double now)
    {
        bool jump = segment != _lastSegment + 1;
        bool pause = !double.IsNaN(_lastRequest) && now - _lastRequest > 2 * Math.Max(seconds, 1) + 2;
        if (jump || pause || double.IsNaN(_anchorWall))
        {
            _anchorWall = now;
            _anchorMedia = 0;
            _media = 0;
            if (jump)
            {
                _rates.Clear();
            }
        }

        _media += Math.Max(0, seconds);
        Lead = _media - _anchorMedia - (now - _anchorWall);
        _lastSegment = segment;
        _lastRequest = now;
    }
}
