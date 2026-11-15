using Selene.Features.Missions;

namespace Selene.Tests.Features.Missions;

public class PayoutTests : IDisposable
{
    private const uint Fisher = 18;
    private const uint Mission = 1666;
    private const ulong Mene = 0x0040000000000001UL;
    private const ulong Pandia = 0x0040000000000002UL;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "selene-payouts-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 14, 21, 0, 0);
    private int _masteryAsked;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static MissionState State(uint mission, int classScore, uint score = 0, MissionRank rank = MissionRank.None, uint job = Fisher, bool inZone = true, int streak = 0)
        => new(inZone, 1319, job, mission, score, rank, Array.Empty<uint>(), 0, classScore, GoldStreak: streak);

    private PayoutSample? See(PayoutWatch watch, MissionState state, double seconds = 1, int? mastery = null)
    {
        _now = _now.AddSeconds(seconds);
        return watch.Observe(state, _now, () =>
        {
            _masteryAsked++;
            return mastery;
        });
    }

    /// <summary>The user's chat log, as the watch would see it: 32,830 before, 120 more after an A-rank gold.</summary>
    [Fact]
    public void AMissionSeenFromStartToEnd_IsMeasuredByTheClassScoresRise()
    {
        var watch = new PayoutWatch();

        Assert.Null(See(watch, State(0, 32830)));
        Assert.Null(See(watch, State(Mission, 32830)));
        Assert.Null(See(watch, State(Mission, 32830, 1800, MissionRank.Silver), 300));
        Assert.Null(See(watch, State(Mission, 32830, 2600, MissionRank.Gold), 200));

        var sample = See(watch, State(0, 32950, streak: 2), 2);

        Assert.NotNull(sample);
        Assert.Equal(Mission, sample!.MissionId);
        Assert.Equal(120, sample.ClassPoints);
        Assert.Equal((int)MissionRank.Gold, sample.Rank);
        Assert.Equal(2600u, sample.Score);
        Assert.Equal(502, sample.Seconds);
        Assert.Equal(2, sample.GoldStreak);
        Assert.Equal(Fisher, sample.ClassJobId);
        Assert.Contains("paid 120 class points", watch.Status);
    }

    /// <summary>The user's wish across the suite: the bags are looked at when a mission starts and ends, never on a timer.</summary>
    [Fact]
    public void MasteryPoints_AreReadTwiceAMission_AndTheirRiseIsKept()
    {
        var watch = new PayoutWatch();
        See(watch, State(0, 1000));
        See(watch, State(1677, 1000), mastery: 311789);
        for (var i = 0; i < 20; i++)
            See(watch, State(1677, 1000, 30000, MissionRank.Gold), mastery: 311789);

        var sample = See(watch, State(0, 5824), mastery: 331789);

        Assert.Equal(2, _masteryAsked);
        Assert.Equal(20000, sample!.MasteryPoints);
        Assert.Equal(4824, sample.ClassPoints);
    }

    /// <summary>An abandoned mission pays nothing and is no measurement of what the mission pays.</summary>
    [Fact]
    public void AMissionThatEndsWithNoClassPoints_IsNotRecorded()
    {
        var watch = new PayoutWatch();
        See(watch, State(0, 1000));
        See(watch, State(Mission, 1000));

        Assert.Null(See(watch, State(0, 1000)));
        Assert.Null(See(watch, State(0, 1000), PayoutWatch.Settle.TotalSeconds + 1));

        Assert.Contains("not recorded", watch.Status);
        Assert.Null(See(watch, State(0, 1120))); // a later rise is not this mission's
    }

    /// <summary>The class score may show the rise a moment after the mission is gone.</summary>
    [Fact]
    public void ARiseThatShowsAMomentLate_IsStillThisMissions()
    {
        var watch = new PayoutWatch();
        See(watch, State(0, 1000));
        See(watch, State(Mission, 1000, 2600, MissionRank.Gold));

        Assert.Null(See(watch, State(0, 1000)));
        var sample = See(watch, State(0, 1120), 2);

        Assert.Equal(120, sample!.ClassPoints);
    }

    /// <summary>A mission already going when the plugin loaded: its points count, its time is not known.</summary>
    [Fact]
    public void AMissionAlreadyUnderWay_IsMeasured_ButNotTimed()
    {
        var watch = new PayoutWatch();
        See(watch, State(Mission, 1000, 900, MissionRank.Bronze));

        var sample = See(watch, State(0, 1024), 100);

        Assert.Equal(24, sample!.ClassPoints);
        Assert.Equal(0, sample.Seconds);
    }

    /// <summary>The class score is the job's: a change of job, or leaving the zone, ends the watch without a sample.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AChangeOfJobOrZone_DropsTheWatch(bool changeJob)
    {
        var watch = new PayoutWatch();
        See(watch, State(0, 1000));
        See(watch, State(Mission, 1000));

        Assert.Null(See(watch, changeJob ? State(0, 617390, job: 8) : State(0, 0, inZone: false)));
        Assert.Null(See(watch, State(0, 1120)));
    }

    [Fact]
    public void ASummary_KeepsEachRanksLatestAndRange_AndTheBestRank()
    {
        var at = new DateTime(2026, 11, 14);
        var summary = PayoutSummary.Summarise(new[]
        {
            new PayoutSample(at, Mission, Fisher, 3, 2600, 120, null, 500, 0),
            new PayoutSample(at.AddHours(1), Mission, Fisher, 3, 2700, 126, null, 700, 2),
            new PayoutSample(at.AddHours(2), Mission, Fisher, 2, 2100, 96, null, 0, 0),
            new PayoutSample(at, 1658, Fisher, 3, 3100, 75, null, 0, 0),
        });

        var mission = summary[Mission];
        Assert.Equal(3, mission.Samples);
        Assert.Equal((126, 120, 126, 2), mission.ByRank[3]);
        Assert.Equal((96, 96, 96, 1), mission.ByRank[2]);
        Assert.Equal((3, 126), mission.Best);
        Assert.Equal(600, mission.AverageSeconds);
        Assert.Equal(12.6, mission.PerMinute!.Value, 1);
        Assert.Null(summary[1658].PerMinute);
    }

    /// <summary>A file a character: each client writes its own, and a second client reading the folder sees both.</summary>
    [Fact]
    public void TheLog_KeepsAFileACharacter_AndAnotherClientReadsThemAll()
    {
        var mine = new PayoutLog(_directory);
        mine.Add(Mene, new PayoutSample(_now, Mission, Fisher, 3, 2600, 120, null, 500, 0));
        var theirs = new PayoutLog(_directory);
        theirs.Add(Pandia, new PayoutSample(_now, 1658, Fisher, 3, 3100, 75, null, 400, 0));

        var reader = new PayoutLog(_directory);
        reader.Reload();

        Assert.True(File.Exists(mine.PathFor(Mene)));
        Assert.True(File.Exists(theirs.PathFor(Pandia)));
        Assert.Equal(120, reader.Summaries[Mission].Best.Points);
        Assert.Equal(75, reader.Summaries[1658].Best.Points);
        Assert.Contains("2 sample(s) of 2 mission(s) from 2 character(s)", reader.Status);
    }

    [Fact]
    public void TheExport_IsASheetWithARowASample()
    {
        var log = new PayoutLog(_directory);
        log.Add(Mene, new PayoutSample(_now, Mission, Fisher, 3, 2600, 120, 0, 500, 2));

        var path = log.ExportCsv(id => $"Mission \"{id}\"");

        Assert.NotNull(path);
        var lines = File.ReadAllLines(path!);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("When,Character,MissionId,Mission,Job,Rank,Score,ClassPoints", lines[0]);
        Assert.Contains(",1666,\"Mission \"\"1666\"\"\",18,3,2600,120,0,500,2", lines[1]);
    }
}
