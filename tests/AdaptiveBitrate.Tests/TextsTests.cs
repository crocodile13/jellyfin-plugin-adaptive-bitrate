using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AdaptiveBitrate.Core;
using Xunit;

namespace AdaptiveBitrate.Tests;

public class TextsTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "Jellyfin.Plugin.AdaptiveBitrate")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("dépôt introuvable");
    }

    /// <summary>Textes affichés : page de configuration (data-t, tr, trf) et raisons du moteur.</summary>
    private static HashSet<string> Displayed()
    {
        var root = Root();
        var page = File.ReadAllText(Path.Combine(root, "src", "Jellyfin.Plugin.AdaptiveBitrate", "Web", "configPage.html"));
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(page, "data-t=\"([^\"]+)\""))
        {
            res.Add(m.Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal));
        }

        foreach (Match m in Regex.Matches(page, @"\btrf?\('((?:[^'\\]|\\.)*)'"))
        {
            res.Add(m.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal));
        }

        var policy = File.ReadAllText(Path.Combine(root, "src", "AdaptiveBitrate.Core", "Policy.cs"));
        foreach (Match m in Regex.Matches(policy, "return \"([^\"]+)\";"))
        {
            res.Add(m.Groups[1].Value);
        }

        var filter = File.ReadAllText(Path.Combine(root, "src", "Jellyfin.Plugin.AdaptiveBitrate", "HlsFilter.cs"));
        foreach (Match m in Regex.Matches(filter, "skip = \"([^\"]+)\";"))
        {
            res.Add(m.Groups[1].Value);
        }

        return res;
    }

    [Fact]
    public void EveryDisplayedTextIsTranslatedInEnglishGermanAndSpanishWithTheSamePlaceholders()
    {
        var texts = Displayed();
        Assert.True(texts.Count > 40, "textes trouvés : " + texts.Count);
        static string Ph(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var file in new[] { "english", "german", "spanish" })
        {
            var dict = Texts.Raw(file);
            var missing = texts.Where(t => !dict.TryGetValue(t, out var v) || Ph(v) != Ph(t)).ToList();
            Assert.True(missing.Count == 0, file + " : " + string.Join(" | ", missing));
            // aucune traduction orpheline
            var unused = dict.Keys.Where(k => !texts.Contains(k)).ToList();
            Assert.True(unused.Count == 0, file + " (inutilisés) : " + string.Join(" | ", unused));
        }
    }

    [Fact]
    public void LanguagesFallBackToEnglishAndFrenchIsTheSource()
    {
        Assert.Equal("Save", Texts.T("Enregistrer", "en-US"));
        Assert.Equal("Speichern", Texts.T("Enregistrer", "de"));
        Assert.Equal("Enregistrer", Texts.T("Enregistrer", "fr-CA"));
        Assert.Equal("Save", Texts.T("Enregistrer", "ja"));
        Assert.Empty(Texts.Dictionary("fr"));
        Assert.Equal("Guardar", Texts.Dictionary("es")["Enregistrer"]);
    }
}
