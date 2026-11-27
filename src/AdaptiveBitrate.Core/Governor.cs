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

    /// <summary>Secondes au moins entre deux changements (sauf une descente demandée par le lecteur : toujours immédiate).</summary>
    public double MinChangeSeconds { get; set; } = 10;

    /// <summary>Descendre d'avance quand le débit mesuré ne suffit plus, avant que le tampon du lecteur ne se vide.</summary>
    public bool AnticipateDown { get; set; } = true;

    /// <summary>Marge de débit : une qualité n'est gardée ou atteinte que si le débit mesuré dépasse son débit × cette marge.</summary>
    public double Margin { get; set; } = 1.25;

    /// <summary>Segments mesurés au moins avant toute décision du serveur.</summary>
    public int MinSamples { get; set; } = 2;

    /// <summary>Copie bornée.</summary>
    public GovernorOptions Sanitized() => new()
    {
        Enabled = Enabled,
        HoldAfterDownSeconds = Finite(HoldAfterDownSeconds, 30, 0, 600),
        MinChangeSeconds = Finite(MinChangeSeconds, 10, 0, 300),
        AnticipateDown = AnticipateDown,
        Margin = Finite(Margin, 1.25, 1.0, 3.0),
        MinSamples = Math.Clamp(MinSamples, 1, 10),
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

    private readonly IReadOnlyList<int> _bandwidth;   // débit annoncé de chaque marche (bits/s), du plus haut au plus bas
    private readonly Queue<double> _rates = new();
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
    public double? Rate => _rates.Count > 0 ? _rates.Count / _rates.Sum(r => 1 / r) : null;   // moyenne harmonique : prudente

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
        Follow(segment, seconds, now);
        if (!o.Enabled)
        {
            return Serve(requested, Verdict.AsRequested, now);
        }

        int cur = Current;
        bool measured = _rates.Count >= o.MinSamples && Rate is double;
        double rate = Rate ?? 0;

        // le lecteur descend : toujours tout de suite (le retarder ferait des coupures)
        if (requested > cur)
        {
            return Serve(requested, Verdict.AsRequested, now);
        }

        // le débit ne suffit plus pour la qualité en cours : descendre d'avance, jusqu'à une marche qui passe
        if (o.AnticipateDown && measured && rate < _bandwidth[cur] * o.Margin && now - _lastChange >= o.MinChangeSeconds && cur < _bandwidth.Count - 1)
        {
            int fit = cur + 1;
            while (fit < _bandwidth.Count - 1 && rate < _bandwidth[fit] * o.Margin)
            {
                fit++;
            }

            Anticipated++;
            return Serve(Math.Max(fit, requested), Verdict.Anticipated, now);
        }

        // le lecteur (ou le serveur) voudrait remonter : d'une marche à la fois, et seulement quand c'est sûr
        if (requested < cur)
        {
            bool early = now - _lastDown < o.HoldAfterDownSeconds || now - _lastChange < o.MinChangeSeconds;
            int next = cur - 1;
            bool fits = !measured || rate >= _bandwidth[next] * o.Margin;
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
        while (_rates.Count > Window)
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
