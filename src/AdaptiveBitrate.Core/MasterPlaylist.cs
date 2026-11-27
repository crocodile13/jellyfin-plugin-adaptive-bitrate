using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AdaptiveBitrate.Core;

/// <summary>Une qualité d'une playlist HLS maîtresse : sa ligne #EXT-X-STREAM-INF et l'adresse de sa playlist.</summary>
/// <param name="Info">Ligne #EXT-X-STREAM-INF complète.</param>
/// <param name="Uri">Adresse de la playlist de cette qualité.</param>
public sealed record Variant(string Info, string Uri)
{
    /// <summary>Débit annoncé (BANDWIDTH, bits par seconde ; 0 si absent).</summary>
    public long Bandwidth => MasterPlaylist.Attribute(Info, "BANDWIDTH") is { } b && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// Lecture et fusion de playlists HLS maîtresses (celles que produit Jellyfin) : la playlist d'origine reste telle quelle
/// (sous-titres, vignettes de la barre de lecture, qualité de départ en premier), les qualités des autres marches y sont
/// ajoutées. Fonction pure.
/// </summary>
public static class MasterPlaylist
{
    private const string StreamInf = "#EXT-X-STREAM-INF:";

    /// <summary>Qualités d'une playlist maîtresse, dans leur ordre.</summary>
    public static List<Variant> Variants(string playlist)
    {
        var res = new List<Variant>();
        var lines = Lines(playlist);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(StreamInf, StringComparison.Ordinal))
            {
                continue;
            }

            // l'adresse est la ligne suivante qui n'est ni vide ni un commentaire ou une balise
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].Length == 0)
                {
                    continue;
                }

                if (!lines[j].StartsWith('#'))
                {
                    res.Add(new Variant(lines[i], lines[j]));
                    i = j;
                }

                break;
            }
        }

        return res;
    }

    /// <summary>
    /// Playlist d'origine augmentée des qualités <paramref name="added"/> (après ses propres qualités, pour qu'un lecteur
    /// qui part de la première commence à la qualité choisie par Jellyfin). Une qualité dont l'adresse est déjà présente
    /// n'est pas répétée. Tout le reste de la playlist d'origine est gardé à sa place.
    /// </summary>
    public static string Merge(string original, IEnumerable<Variant> added)
    {
        var lines = Lines(original);
        var known = new HashSet<string>(Variants(original).Select(v => v.Uri), StringComparer.Ordinal);
        var extra = new List<Variant>();
        foreach (var v in added)
        {
            if (known.Add(v.Uri))
            {
                extra.Add(v);
            }
        }

        if (extra.Count == 0)
        {
            return original;
        }

        // insérées juste après l'adresse de la dernière qualité d'origine (avant les vignettes #EXT-X-IMAGE-STREAM-INF)
        int last = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(StreamInf, StringComparison.Ordinal))
            {
                for (int j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].Length > 0 && !lines[j].StartsWith('#'))
                    {
                        last = j;
                        i = j;
                        break;
                    }
                }
            }
        }

        if (last < 0)
        {
            return original;   // pas une playlist maîtresse reconnue : rien à faire
        }

        var insert = extra.SelectMany(v => new[] { v.Info, v.Uri }).ToList();
        lines.InsertRange(last + 1, insert);
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            sb.Append(l).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Valeur d'un attribut d'une ligne de balise HLS (guillemets retirés), ou null.</summary>
    public static string? Attribute(string tagLine, string name)
    {
        var m = Regex.Match(tagLine, "(?:[:,])" + Regex.Escape(name) + "=(\"[^\"]*\"|[^,]*)", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value.Trim('"') : null;
    }

    /// <summary>Lignes de la playlist, sans fins de ligne ni lignes vides finales.</summary>
    private static List<string> Lines(string s)
    {
        var lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
