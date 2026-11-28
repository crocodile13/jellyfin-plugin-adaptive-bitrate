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
/// Mesure et pilotage d'une lecture par le serveur, segment par segment. Mesure du débit : le lecteur ne demande un
/// segment qu'une fois le précédent reçu en entier ; le temps entre le début de l'envoi d'un segment et la demande du
/// suivant est donc son temps de livraison réel, tampons du système, proxy et VPN compris (chronométrer l'envoi seul ne
/// mesure que la copie dans le tampon d'envoi du système : environ 1 Go/s pour un segment de 1 Mo, quelle que soit la
/// connexion). Une demande retardée par le lecteur lui-même (tampon plein) n'est pas une mesure : écartée. Avance du
/// lecteur : durée de vidéo demandée moins temps écoulé (une pause ou un saut repart de zéro). Attente de l'encodeur :
/// temps entre la demande d'un segment et le début de son envoi, hors du débit. Pilotage (si activé) : une descente demandée par
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
    private long _sentBytes;
    private double _sentStart = double.NaN, _sentSeconds, _sentLead;
    private int _sentSegment = -10;
    private readonly Queue<double> _waits = new();
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

    /// <summary>
    /// Premier segment d'une nouvelle qualité servi après <paramref name="seconds"/> secondes d'attente. Moins de 50 ms :
    /// le segment était déjà sur le disque (l'encodage de cette qualité avait pris de l'avance avant d'être arrêté, mesuré
    /// au banc : 2 ms), aucun encodage n'a démarré ; pas une mesure.
    /// </summary>
    public static void ReportStartup(double seconds)
    {
        if (!(seconds >= 0.05) || !double.IsFinite(seconds) || seconds > 120)
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

    /// <summary>Demandes écartées de la mesure (le lecteur attendait, son tampon plein).</summary>
    public int Paced { get; private set; }

    /// <summary>Segments sautés en avant (le flux a un trou : jamais attendu sans recherche dans la vidéo).</summary>
    public int Skipped { get; private set; }

    /// <summary>
    /// Attente moyenne de l'encodeur par seconde de vidéo sur les derniers segments (0 = segments prêts ; 1 = l'encodage
    /// avance au temps réel, sans marge ; au-delà, il ne suit pas), ou null avant la première mesure.
    /// </summary>
    public double? EncoderLoad => _waits.Count > 0 ? _waits.Average() : null;

    /// <summary>Durée des segments (secondes, la dernière demandée).</summary>
    public double SegmentSeconds => _segment;

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

        Measure(segment, now);
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

    /// <summary>
    /// Le segment <paramref name="segment"/> part : <paramref name="bytes"/> octets, envoi commencé à <paramref name="start"/>
    /// (même horloge que <see cref="Decide"/>), après <paramref name="waited"/> secondes d'attente de l'encodeur. Sa durée
    /// de livraison se lit à la demande du segment suivant.
    /// </summary>
    public void Sending(int segment, long bytes, double start, double waited)
    {
        if (bytes <= 0 || !double.IsFinite(start))
        {
            return;
        }

        _sentBytes = bytes;
        _sentStart = start;
        _sentSegment = segment;
        _sentSeconds = _segment;
        _sentLead = segment == _lastSegment ? Lead ?? 0 : 0;
        if (waited >= 0 && double.IsFinite(waited) && _segment > 0)
        {
            _waits.Enqueue(waited / _segment);
            while (_waits.Count > Window)
            {
                _waits.Dequeue();
            }
        }
    }

    // la demande du segment suivant clôt la livraison du précédent ; écartée quand le lecteur a pu attendre de lui-même :
    // livraison aussi longue que la vidéo du segment alors qu'il avait déjà beaucoup d'avance en demandant ce segment
    // (tampon plein, ou lecture en pause ; l'avance compte le segment demandé : plus d'un demi-segment de réserve en plus,
    // au moins 4 s, car jellyfin-web limite son tampon à 6 s aux débits élevés). Avec peu d'avance, il n'avait aucune
    // raison d'attendre : livraison lente.
    private void Measure(int segment, double now)
    {
        if (double.IsNaN(_sentStart) || segment != _sentSegment + 1)
        {
            if (segment > _lastSegment + 1 && _lastSegment >= 0 && !double.IsNaN(_lastRequest))
            {
                Skipped++;
            }

            _sentStart = double.NaN;
            return;
        }

        double took = now - _sentStart;
        _sentStart = double.NaN;
        if (took <= 0)
        {
            return;
        }

        if (took >= 0.8 * _sentSeconds && _sentLead > Math.Max(1.5 * _sentSeconds, 4))
        {
            Paced++;
            return;
        }

        _rates.Enqueue(_sentBytes * 8 / Math.Max(took, 0.005));
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

    // avance du lecteur : vidéo demandée depuis l'ancrage, moins le temps écoulé. Une demande en arrière de un ou deux
    // segments (le lecteur recharge le segment en cours dans une autre qualité) retire ce qui est redemandé ; un saut
    // (recherche dans la vidéo) repart de zéro ; une avance devenue négative (lecture en pause, ou coupure) repart de zéro
    // sans effacer la mesure du débit. Une livraison lente n'est pas une pause : elle fait seulement fondre l'avance.
    private void Follow(int segment, double seconds, double now)
    {
        int back = _lastSegment + 1 - segment;
        bool jump = !double.IsNaN(_anchorWall) && segment != _lastSegment + 1 && !(back is > 0 and <= 2);
        if (jump || double.IsNaN(_anchorWall))
        {
            _anchorWall = now;
            _anchorMedia = 0;
            _media = 0;
            if (jump)
            {
                _rates.Clear();
            }
        }
        else if (back is > 0 and <= 2)
        {
            _media = Math.Max(_anchorMedia, _media - (back * Math.Max(0, seconds)));
        }

        if (_media - _anchorMedia - (now - _anchorWall) < 0)
        {
            _anchorWall = now - (_media - _anchorMedia);
        }

        _media += Math.Max(0, seconds);
        Lead = _media - _anchorMedia - (now - _anchorWall);
        _lastSegment = segment;
        _lastRequest = now;
    }
}
