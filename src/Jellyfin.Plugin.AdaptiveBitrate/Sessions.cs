using System;
using System.Collections.Generic;
using System.Linq;
using AdaptiveBitrate.Core;

namespace Jellyfin.Plugin.AdaptiveBitrate;

/// <summary>Paramètres de l'action de Jellyfin propres à une qualité (débit, taille, copie de la vidéo).</summary>
public sealed class RungParams
{
    /// <summary>Noms des paramètres qui font une qualité (tous les autres sont communs à l'échelle).</summary>
    public static readonly string[] Keys = { "videoBitRate", "maxHeight", "maxWidth", "width", "height", "allowVideoStreamCopy", "enableAutoStreamCopy" };

    /// <summary>Valeur de chaque paramètre (null = absent).</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Débit annoncé de la qualité (bits/s, audio compris).</summary>
    public long Bandwidth { get; init; }

    /// <summary>Cette qualité correspond à ces arguments d'action (débit vidéo et copie autorisée).</summary>
    public bool Matches(IDictionary<string, object?> args)
        => Equals(Norm(Values.GetValueOrDefault("videoBitRate")), Norm(args.TryGetValue("videoBitRate", out var b) ? b : null))
           && (Values.GetValueOrDefault("allowVideoStreamCopy") as bool? ?? true) == (args.TryGetValue("allowVideoStreamCopy", out var c) ? c as bool? ?? true : true);

    private static long? Norm(object? v) => v switch { int i => i, long l => l, _ => null };
}

/// <summary>Une lecture suivie : ses qualités (de la plus haute à la plus basse) et sa mesure / son pilotage.</summary>
public sealed class Session
{
    public Session(IReadOnlyList<RungParams> rungs)
    {
        Rungs = rungs;
        Governor = new Governor(rungs.Select(r => (int)Math.Min(int.MaxValue, r.Bandwidth)).ToList());
    }

    /// <summary>Qualités, de la plus haute à la plus basse.</summary>
    public IReadOnlyList<RungParams> Rungs { get; }

    /// <summary>Mesure et décisions.</summary>
    public Governor Governor { get; }

    /// <summary>Dernière activité (éviction).</summary>
    public DateTime Touched { get; set; } = DateTime.UtcNow;

    /// <summary>Indice de la qualité demandée par ces arguments, ou -1.</summary>
    public int IndexOf(IDictionary<string, object?> args)
    {
        for (int i = 0; i < Rungs.Count; i++)
        {
            if (Rungs[i].Matches(args))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Lectures suivies, en mémoire bornée (les plus anciennes partent), sans rien écrire sur disque.</summary>
public sealed class Sessions
{
    /// <summary>Lectures gardées.</summary>
    public const int Max = 64;

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _all = new(StringComparer.Ordinal);

    /// <summary>Nouvelle playlist maîtresse servie pour cette lecture : son échelle remplace la précédente.</summary>
    public void Start(string id, IReadOnlyList<RungParams> rungs)
    {
        if (string.IsNullOrEmpty(id) || rungs.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            _all[id] = new Session(rungs);
            while (_all.Count > Max)
            {
                _all.Remove(_all.OrderBy(kv => kv.Value.Touched).First().Key);
            }
        }
    }

    /// <summary>La lecture, ou null.</summary>
    public Session? Get(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_lock)
        {
            if (_all.TryGetValue(id, out var s))
            {
                s.Touched = DateTime.UtcNow;
                return s;
            }

            return null;
        }
    }
}
