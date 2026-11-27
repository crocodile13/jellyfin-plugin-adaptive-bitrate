using System;
using System.Collections.Generic;
using System.Linq;

namespace AdaptiveBitrate.Core;

/// <summary>Un changement de qualité vu par le serveur (le lecteur a demandé la playlist d'une autre marche).</summary>
/// <param name="At">Instant (UTC).</param>
/// <param name="VideoBitrate">Débit vidéo de la marche demandée (bits par seconde ; 0 = qualité d'origine en copie).</param>
public sealed record Switch(DateTime At, int VideoBitrate);

/// <summary>Une qualité proposée dans la playlist maîtresse.</summary>
/// <param name="Kbps">Débit annoncé (BANDWIDTH, kb/s, audio compris).</param>
/// <param name="Resolution">Taille de l'image (« 1280x720 » ; vide si inconnue).</param>
public sealed record Offer(int Kbps, string Resolution);

/// <summary>Résumé d'une lecture : ses marches successives.</summary>
/// <param name="Session">Identifiant de la lecture (PlaySessionId).</param>
/// <param name="Title">Titre lu.</param>
/// <param name="Client">Application.</param>
/// <param name="Offers">Qualités proposées, dans l'ordre de la playlist.</param>
/// <param name="Steps">Marches dans l'ordre (sans répétition consécutive).</param>
public sealed record SessionSummary(string Session, string Title, string Client, IReadOnlyList<Offer> Offers, IReadOnlyList<Switch> Steps)
{
    /// <summary>Nombre de qualités proposées.</summary>
    public int Offered => Offers.Count;

    /// <summary>Descentes de qualité.</summary>
    public int Down => Steps.Zip(Steps.Skip(1)).Count(p => Rank(p.Second) < Rank(p.First));

    /// <summary>Montées de qualité.</summary>
    public int Up => Steps.Zip(Steps.Skip(1)).Count(p => Rank(p.Second) > Rank(p.First));

    // la qualité d'origine en copie (0) est la plus haute
    private static long Rank(Switch s) => s.VideoBitrate <= 0 ? long.MaxValue : s.VideoBitrate;
}

/// <summary>
/// Journal borné des lectures récentes et de leurs changements de qualité (page de configuration). Mémoire bornée : au
/// plus <see cref="MaxSessions"/> lectures, <see cref="MaxSteps"/> marches chacune ; rien n'est écrit sur disque.
/// </summary>
public sealed class SwitchLog
{
    /// <summary>Lectures gardées.</summary>
    public const int MaxSessions = 30;

    /// <summary>Marches gardées par lecture.</summary>
    public const int MaxSteps = 100;

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public string Session = string.Empty;
        public string Title = string.Empty;
        public string Client = string.Empty;
        public List<Offer> Offers = new();
        public List<Switch> Steps = new();
    }

    /// <summary>Une playlist maîtresse a été servie avec ces qualités.</summary>
    public void Started(string session, string title, string client, IReadOnlyList<Offer> offers)
    {
        if (string.IsNullOrEmpty(session))
        {
            return;
        }

        lock (_lock)
        {
            var e = Find(session);
            if (e is null)
            {
                e = new Entry { Session = session };
                _entries.Add(e);
                if (_entries.Count > MaxSessions)
                {
                    _entries.RemoveAt(0);
                }
            }

            e.Title = title;
            e.Client = client;
            e.Offers = offers.Take(20).ToList();
        }
    }

    /// <summary>Le lecteur a demandé la playlist de la marche <paramref name="videoBitrate"/>.</summary>
    public void Requested(string session, int videoBitrate, DateTime at)
    {
        lock (_lock)
        {
            var e = Find(session);
            if (e is null || (e.Steps.Count > 0 && e.Steps[^1].VideoBitrate == videoBitrate))
            {
                return;
            }

            e.Steps.Add(new Switch(at, videoBitrate));
            if (e.Steps.Count > MaxSteps)
            {
                e.Steps.RemoveAt(0);
            }
        }
    }

    /// <summary>Lectures récentes, la plus récente d'abord.</summary>
    public List<SessionSummary> Recent()
    {
        lock (_lock)
        {
            return _entries.AsEnumerable().Reverse().Select(Summary).ToList();
        }
    }

    /// <summary>Une lecture, ou null si elle n'est pas (ou plus) dans le journal.</summary>
    public SessionSummary? Get(string session)
    {
        lock (_lock)
        {
            return Find(session) is { } e ? Summary(e) : null;
        }
    }

    private static SessionSummary Summary(Entry e) => new(e.Session, e.Title, e.Client, e.Offers.ToList(), e.Steps.ToList());

    private Entry? Find(string session) => _entries.FirstOrDefault(x => x.Session == session);
}
