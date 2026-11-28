using System;
using System.Linq;
using AdaptiveBitrate.Core;
using Xunit;

namespace AdaptiveBitrate.Tests;

public class GovernorTests
{
    // échelle d'un fichier à 8 Mb/s : 8,1 · 4,5 · 2,5 · 1,5 · 0,9 Mb/s (débits annoncés, audio compris)
    private static readonly int[] Ladder = { 8_100_000, 4_500_000, 2_500_000, 1_500_000, 900_000 };
    private static readonly GovernorOptions On = new() { Enabled = true, AnticipateDown = true };

    /// <summary>Taille d'un segment de 4 s à la qualité <paramref name="rung"/> (octets).</summary>
    private static long Bytes(int rung) => Ladder[rung] / 8 * 4;

    /// <summary>
    /// Un lecteur réel : il demande le segment <paramref name="seg"/> à <paramref name="t"/>, le serveur l'envoie aussitôt,
    /// et la demande suivante arrive quand il est livré à <paramref name="mbps"/> Mb/s. Renvoie l'instant de cette demande.
    /// </summary>
    private static double Step(Governor g, int requested, ref int seg, double t, double mbps, GovernorOptions o, out Decision d)
    {
        d = g.Decide(requested, seg, 4, t, o);
        g.Sending(seg, Bytes(d.Rung), t, 0);
        seg++;
        return t + (Bytes(d.Rung) * 8 / (mbps * 1e6));
    }

    [Fact]
    public void OffItOnlyMeasuresAndServesWhatThePlayerAsks()
    {
        var off = new GovernorOptions();
        var g = new Governor(Ladder);
        int seg = 0;
        double t = Step(g, 0, ref seg, 0, 20, off, out var d);
        Assert.Equal(new Decision(0, Verdict.AsRequested), d);
        Assert.Null(g.Rate);                                                        // livraison pas encore close
        Assert.Equal(new Decision(3, Verdict.AsRequested), g.Decide(3, seg, 4, t, off));
        Assert.InRange(g.Rate!.Value, 19.9e6, 20.1e6);                              // le temps de livraison, pas d'envoi
        Assert.Equal(new Decision(0, Verdict.AsRequested), g.Decide(0, seg + 1, 4, t + 1, off));
    }

    [Fact]
    public void TheRateIsTheDeliveryTimeAndAPlayerWaitingWithAFullBufferIsNotAMeasure()
    {
        var off = new GovernorOptions();
        var g = new Governor(Ladder);
        int seg = 0;
        double t = 0;
        // 1,6 Mb/s, qualité à 0,9 : le lecteur télécharge sans arrêt, son avance grandit
        for (int i = 0; i < 6; i++) { t = Step(g, 4, ref seg, t, 1.6, off, out _); }
        g.Decide(4, seg, 4, t, off);
        Assert.InRange(g.Rate!.Value, 1.59e6, 1.61e6);
        Assert.True(g.Lead > 8);
        // tampon plein : une demande toutes les 4 s ; ces attentes ne disent rien du réseau
        int samples = g.Samples;
        g.Sending(seg, Bytes(4), t, 1.2);                                           // 1,2 s d'attente de l'encodeur
        for (int i = 0; i < 3; i++) { t += 4; seg++; g.Decide(4, seg, 4, t, off); g.Sending(seg, Bytes(4), t, 1.2); }
        Assert.Equal(samples, g.Samples);
        Assert.Equal(3, g.Paced);
        Assert.InRange(g.Rate!.Value, 1.59e6, 1.61e6);
        Assert.InRange(g.EncoderLoad!.Value, 0.29, 0.31);                           // 1,2 s par segment de 4 s
        // longue pause de la lecture : pas une mesure non plus
        g.Decide(4, seg + 1, 4, t + 120, off);
        Assert.Equal(samples, g.Samples);
        // un segment sauté : un trou dans le flux
        Assert.Equal(0, g.Skipped);
        g.Decide(4, seg + 5, 4, t + 121, off);
        Assert.Equal(1, g.Skipped);
    }

    [Fact]
    public void APlayerSteppingDownIsAlwaysServedAtOnce()
    {
        var g = new Governor(Ladder);
        g.Decide(0, 0, 4, 0, On);
        Assert.Equal(new Decision(2, Verdict.AsRequested), g.Decide(2, 1, 4, 1, On));   // même juste après un changement
        Assert.Equal(new Decision(4, Verdict.AsRequested), g.Decide(4, 2, 4, 2, On));
    }

    [Fact]
    public void GoingBackUpWaitsAfterADropThenClimbsOneStepAtATimeWhenTheMeasureAllows()
    {
        var g = new Governor(Ladder);
        int seg = 0;
        double t = 0;
        for (int i = 0; i < 4; i++) { t = Step(g, 3, ref seg, t, 30, On, out _); }        // départ à 1,5 Mb/s, réseau à 30
        Assert.Equal(new Decision(3, Verdict.Held), g.Decide(0, seg, 4, t, On));       // trop tôt après la descente
        g.Sending(seg++, Bytes(3), t, 0);
        // tampon plein : une demande toutes les 4 s ; le lecteur voudrait la meilleure qualité
        var seen = new System.Collections.Generic.List<(double T, Decision D)>();
        for (t += 4; t < 80; t += 4)
        {
            var d = g.Decide(0, seg, 4, t, On);
            g.Sending(seg++, Bytes(d.Rung), t, 0);
            seen.Add((t, d));
        }

        var climbs = seen.Where((x, i) => x.D.Rung != (i == 0 ? 3 : seen[i - 1].D.Rung)).ToList();
        Assert.Equal(new[] { 2, 1, 0 }, climbs.Select(x => x.D.Rung));                  // une marche à la fois
        Assert.True(climbs[0].T >= 30);                                                 // 30 s après la descente
        Assert.True(climbs[1].T - climbs[0].T >= 10 && climbs[2].T - climbs[1].T >= 10); // 10 s entre deux (automatique)
        Assert.Equal(Verdict.AsRequested, climbs[2].D.Verdict);
    }

    [Fact]
    public void NoClimbWhenTheMeasuredRateDoesNotLeaveTheMargin()
    {
        var o = new GovernorOptions { Enabled = true, AnticipateDown = false, Margin = 1.25, HoldAfterDownSeconds = 0 };
        var g = new Governor(Ladder);
        int seg = 0;
        double t = 0;
        for (int i = 0; i < 3; i++) { t = Step(g, 3, ref seg, t, 2.8, o, out _); }      // 2,8 Mb/s : 2,5 × 1,25 ne passe pas
        Assert.Equal(new Decision(3, Verdict.Held), g.Decide(0, seg, 4, t, o));
    }

    [Fact]
    public void WhenTheRateNoLongerSufficesTheServerStepsDownAheadToAQualityThatFits()
    {
        var g = new Governor(Ladder);
        int seg = 0;
        double t = Step(g, 0, ref seg, 0, 2.2, On, out var d);                          // le réseau tombe à 2,2 Mb/s
        t = Step(g, 0, ref seg, t, 2.2, On, out d);
        Assert.Equal(new Decision(0, Verdict.AsRequested), d);                         // une seule mesure : pas de décision
        Step(g, 0, ref seg, t, 2.2, On, out d);                                         // le lecteur ne s'en est pas encore aperçu
        Assert.Equal(new Decision(3, Verdict.Anticipated), d);                         // 1,5 × 1,1 < 2,2 : la marche qui passe
        Assert.Equal(1, g.Anticipated);
        // descente anticipée coupée : le serveur laisse faire le lecteur
        var noAhead = new GovernorOptions { Enabled = true, AnticipateDown = false };
        var k = new Governor(Ladder);
        seg = 0;
        t = 0;
        for (int i = 0; i < 3; i++) { t = Step(k, 0, ref seg, t, 2.2, noAhead, out d); }
        Assert.Equal(new Decision(0, Verdict.AsRequested), d);
    }

    [Fact]
    public void LeadFollowsTheRequestRhythmAndRestartsAfterASeekOrAPause()
    {
        var g = new Governor(Ladder);
        g.Decide(0, 0, 4, 0, On);
        g.Decide(0, 1, 4, 0.5, On);
        g.Decide(0, 2, 4, 1, On);
        Assert.InRange(g.Lead!.Value, 10.9, 11.1);                                  // 12 s demandées en 1 s
        g.Decide(0, 3, 4, 9, On);
        Assert.InRange(g.Lead!.Value, 6.9, 7.1);                                    // il consomme : 16 s demandées en 9 s
        g.Decide(1, 3, 4, 9.2, On);                                                 // le segment en cours redemandé dans une autre qualité
        Assert.InRange(g.Lead!.Value, 6.7, 6.9);                                    // pas compté deux fois
        g.Decide(0, 40, 4, 10, On);                                                 // saut : repart de zéro
        Assert.InRange(g.Lead!.Value, 3.9, 4.1);
        Assert.Equal(0, g.Samples);
        g.Decide(0, 41, 4, 60, On);                                                 // longue pause : repart de zéro
        Assert.InRange(g.Lead!.Value, 3.9, 4.1);
    }

    [Fact]
    public void OutOfRangeSettingsAreBounded()
    {
        var s = new GovernorOptions { HoldAfterDownSeconds = -5, MinChangeSeconds = double.NaN, Margin = 0.2, MinSamples = 99 }.Sanitized();
        Assert.Equal((0.0, 0.0, 1.0, 10), (s.HoldAfterDownSeconds, s.MinChangeSeconds, s.Margin, s.MinSamples));
        Assert.Equal(0.0, new GovernorOptions { Margin = double.NaN }.Sanitized().Margin);    // 0 = automatique
    }

    /// <summary>Segments de 3 s de 1 Mo livrés en <paramref name="took"/> secondes chacun, demandés l'un après l'autre.</summary>
    private static void Deliver(Governor g, double[] took)
    {
        var off = new GovernorOptions();
        double t = 0;
        int seg = 0;
        g.Decide(0, seg, 3, t, off);
        foreach (var d in took)
        {
            g.Sending(seg, 1_000_000, t, 0);
            t += d;
            g.Decide(0, ++seg, 3, t, off);
        }
    }

    [Fact]
    public void AutomaticValuesFollowTheSegmentLengthAndTheConnectionsSteadiness()
    {
        var auto = new GovernorOptions { Enabled = true };
        var g = new Governor(Ladder);
        g.Decide(0, 0, 3, 0, auto);
        Assert.Equal(7.5, g.MinChangeFor(auto));          // 2,5 segments de 3 s
        Assert.Equal(2, g.MinSamplesFor(auto));           // 6 s de vidéo
        Assert.Equal(1.25, g.MarginFor(auto));            // moins de trois mesures
        var h = new Governor(Ladder);
        h.Decide(0, 0, 20, 0, auto);
        Assert.Equal(30, h.MinChangeFor(auto));           // plafond
        Assert.Equal(1, h.MinSamplesFor(auto));
        // connexion régulière : petite marge ; connexion irrégulière : grande marge
        var steady = new Governor(Ladder);
        Deliver(steady, new[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 });
        Assert.InRange(steady.MarginFor(auto), 1.09, 1.11);
        var k = new Governor(Ladder);
        Deliver(k, new[] { 0.2, 0.9, 0.3, 1.2, 0.25, 0.8 });
        Assert.InRange(k.MarginFor(auto), 1.5, 2.0);
        // une valeur réglée l'emporte
        Assert.Equal(1.6, g.MarginFor(new GovernorOptions { Margin = 1.6 }));
        Assert.Equal(12, g.MinChangeFor(new GovernorOptions { MinChangeSeconds = 12 }));
    }

    [Fact]
    public void SteppingDownAheadWaitsUntilThePlayersLeadNoLongerCoversAnEncoderStart()
    {
        Governor.ResetStartup();
        Governor.ReportStartup(2.0);
        var o = new GovernorOptions { Enabled = true, AnticipateDown = true };
        // le lecteur a plus de 40 s d'avance ; le réseau tombe à 6 Mb/s pour 8,1 : chaque segment de 4 s met 5,4 s à
        // arriver, l'avance fond de 1,4 s par segment. Le serveur descend d'avance seulement quand elle ne couvre plus le
        // démarrage (2 s) + le segment suivant à ce débit (5,4 s) + un segment (4 s)
        var g = new Governor(Ladder);
        for (int i = 0; i < 12; i++) { g.Decide(0, i, 4, i * 0.1, o); }
        int seg = 12;
        double t = 1.2;
        Decision d = new(0, Verdict.AsRequested);
        while (seg < 80 && d.Verdict == Verdict.AsRequested)
        {
            t = Step(g, 0, ref seg, t, 6, o, out d);
            if (d.Verdict == Verdict.AsRequested && g.Samples >= 2) { Assert.True(g.Lead >= 11.3, $"lead {g.Lead}"); }
        }

        Assert.Equal(Verdict.Anticipated, d.Verdict);
        Assert.True(g.Lead < 11.5, $"lead {g.Lead}");
        Assert.InRange(Governor.Startup!.Value, 1.99, 2.01);
        Governor.ReportStartup(-1);
        Governor.ReportStartup(500);                       // valeurs absurdes ignorées
        Governor.ReportStartup(0.002);                     // segment déjà prêt : aucun encodage démarré
        Assert.InRange(Governor.Startup!.Value, 1.99, 2.01);
        Governor.ResetStartup();
    }
}
