using System;
using System.Collections.Generic;
using System.Linq;

namespace AdaptiveBitrate.Core;

/// <summary>Où l'échelle de qualités s'applique.</summary>
public enum Scope
{
    /// <summary>Lecteurs hors du réseau local seulement (le réseau local suffit presque toujours).</summary>
    Remote,

    /// <summary>Tous les lecteurs, réseau local compris (Wi-Fi faible, CPL).</summary>
    All,
}

/// <summary>Ce que l'on sait d'une demande de lecture.</summary>
/// <param name="IsLocal">Le lecteur est dans le réseau local (selon Jellyfin).</param>
/// <param name="IsLiveStream">Chaîne en direct ou flux ouvert à la demande (pas d'échelle : ouvrir un flux est lent).</param>
/// <param name="Client">Nom de l'application (Jellyfin Web, Jellyfin Android TV…).</param>
/// <param name="CanTranscode">Le compte a le droit de transcoder la vidéo (les marches du dessous sont transcodées).</param>
public sealed record PlaybackRequest(bool IsLocal, bool IsLiveStream, string? Client, bool CanTranscode);

/// <summary>Décisions du plugin, sans effet de bord.</summary>
public static class Policy
{
    /// <summary>Raison pour laquelle l'échelle ne s'applique pas (texte français, clé de traduction), ou null : elle s'applique.</summary>
    public static string? Skip(bool enabled, Scope scope, IReadOnlyCollection<string> excludedClients, PlaybackRequest r)
    {
        if (!enabled)
        {
            return "plugin coupé";
        }

        if (r.IsLiveStream)
        {
            return "flux en direct";
        }

        if (scope == Scope.Remote && r.IsLocal)
        {
            return "lecteur du réseau local";
        }

        if (!r.CanTranscode)
        {
            return "transcodage vidéo interdit au compte";
        }

        if (r.Client is { Length: > 0 } c && excludedClients.Any(x => x.Trim().Length > 0 && c.Contains(x.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return "application exclue";
        }

        return null;
    }

    /// <summary>Liste d'applications saisie (une par ligne ou séparées par des virgules), nettoyée.</summary>
    public static List<string> ParseClients(string? text)
        => (text ?? string.Empty).Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
}
