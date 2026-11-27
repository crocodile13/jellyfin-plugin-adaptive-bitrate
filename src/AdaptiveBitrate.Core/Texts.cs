using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AdaptiveBitrate.Core;

/// <summary>
/// Traductions de l'interface : le texte français est la clé, chaque fichier (english.json, german.json, spanish.json)
/// sa traduction ; un texte absent en allemand ou en espagnol se rabat sur l'anglais.
/// </summary>
public static class Texts
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> All = new(() => new(StringComparer.Ordinal)
    {
        ["en"] = Load("english"), ["de"] = Load("german"), ["es"] = Load("spanish"),
    });

    /// <summary>Langues de l'interface (code à deux lettres).</summary>
    public static readonly string[] Languages = { "fr", "en", "de", "es" };

    /// <summary>Langue prise en charge la plus proche (« fr-FR » → « fr ») ; anglais par défaut.</summary>
    public static string Normalize(string? lang)
    {
        var l = (lang ?? string.Empty).Trim().ToLowerInvariant();
        l = l.Length >= 2 ? l[..2] : l;
        return Array.IndexOf(Languages, l) >= 0 ? l : "en";
    }

    /// <summary>Texte dans la langue demandée.</summary>
    public static string T(string fr, string? lang)
    {
        var l = Normalize(lang);
        if (l == "fr")
        {
            return fr;
        }

        if (All.Value.TryGetValue(l, out var d) && d.TryGetValue(fr, out var v))
        {
            return v;
        }

        return All.Value["en"].TryGetValue(fr, out var en) ? en : fr;
    }

    /// <summary>Dictionnaire complet d'une langue (allemand et espagnol complétés par l'anglais), pour la page.</summary>
    public static Dictionary<string, string> Dictionary(string? lang)
    {
        var l = Normalize(lang);
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (l == "fr")
        {
            return res;
        }

        foreach (var (k, v) in All.Value["en"])
        {
            res[k] = v;
        }

        if (l != "en")
        {
            foreach (var (k, v) in All.Value[l])
            {
                res[k] = v;
            }
        }

        return res;
    }

    /// <summary>Traductions d'une langue (fichier incorporé).</summary>
    public static Dictionary<string, string> Raw(string file) => Load(file);

    private static Dictionary<string, string> Load(string file)
    {
        using var s = typeof(Texts).Assembly.GetManifestResourceStream("AdaptiveBitrate.Core." + file + ".json");
        if (s is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var r = new StreamReader(s);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(r.ReadToEnd()) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
