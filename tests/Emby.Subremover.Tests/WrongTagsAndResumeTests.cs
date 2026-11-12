using Emby.Subremover;
using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class WrongTagsAndResumeTests
{
    private const string Video = @"D:\Movies\My Movie (2020)\My Movie (2020).mkv";
    private const string English = "I don't know what you want from me. We have to go there right now, okay? Yeah, I think that is the right thing to do. You can get the car and we will meet at the house. They are not going to wait for us all night, so just be ready.";
    private const string French = "Je ne sais pas ce que tu veux. Nous devons partir maintenant, d'accord ? Oui, je pense que c'est la bonne chose à faire. Tu peux prendre la voiture et nous allons nous retrouver à la maison. Ils ne vont pas attendre toute la nuit, alors sois prêt avec moi.";
    private const string Unlisted = "Nežinau, ko tu iš manęs nori. Turime eiti dabar pat, gerai? Taip, manau, kad tai teisingas dalykas. Gali pasiimti mašiną ir susitiksime namuose. Jie nelauks mūsų visą naktį, todėl būk pasiruošęs. Aš esu čia ir jis taip pat yra su ja.";

    private static string Srt(string text) => "1\n00:00:01,000 --> 00:00:05,000\n" + text + "\n\n";

    private static PlannerSettings KeepEnglish(bool keepUndefined = true) =>
        new() { KeepLanguages = PlannerSettings.ParseLanguages("eng"), KeepUndefined = keepUndefined };

    [Fact]
    public void Leave_them_keeps_every_wrong_tag_as_it_is()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"), Sub(4, "eng"));
        var rules = new GuessRules { GuessUntagged = true, UntaggedFilesOnly = true, FixWrongTags = false };
        SubtitleLanguageGuessing.Apply(probe.streams[2], Srt(English), null, rules);
        var french = SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(French), null, rules);
        var unknown = SubtitleLanguageGuessing.Apply(probe.streams[4], Srt(Unlisted), null, rules);

        Assert.True(french.WrongTag);
        Assert.True(unknown.WrongTag);
        Assert.All(new[] { 2, 3, 4 }, i => Assert.Equal("eng", probe.streams[i].Language));
        Assert.Equal(new[] { 2, 3, 4 }, SubtitlePlanner.Plan(probe, KeepEnglish(), Video).Keep.Select(s => s.index));
    }

    [Fact]
    public void Fix_the_tag_goes_by_the_real_language_and_unknown_ones_get_no_language()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"), Sub(4, "eng"));
        var rules = new GuessRules { FixWrongTags = true };
        SubtitleLanguageGuessing.Apply(probe.streams[2], Srt(English), null, rules);
        SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(French), null, rules);
        SubtitleLanguageGuessing.Apply(probe.streams[4], Srt(Unlisted), null, rules);

        Assert.Equal("fre", probe.streams[3].Language);
        Assert.Equal("#3 fre (was eng) subrip", probe.streams[3].Describe());
        Assert.Equal("und", probe.streams[4].Language);

        var plan = SubtitlePlanner.Plan(probe, KeepEnglish(keepUndefined: true), Video);
        Assert.Equal(new[] { 3 }, plan.Remove.Select(s => s.index));
        Assert.Equal(new[] { 2, 4 }, plan.Keep.Select(s => s.index));
    }

    [Fact]
    public void Fix_the_tag_can_remove_the_ones_whose_language_cant_be_told()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"), Sub(5, "und"));
        var rules = new GuessRules { FixWrongTags = true, RemoveUnknownWrongTags = true };
        SubtitleLanguageGuessing.Apply(probe.streams[2], Srt(English), null, rules);
        SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(Unlisted), null, rules);

        var plan = SubtitlePlanner.Plan(probe, KeepEnglish(keepUndefined: true), Video);

        // A track that never had a language is still kept by "Keep subtitles with no language set".
        Assert.Equal(new[] { 3 }, plan.Remove.Select(s => s.index));
        Assert.Equal(new[] { 2, 5 }, plan.Keep.Select(s => s.index));
    }

    [Fact]
    public void A_track_tagged_another_language_that_reads_as_english_is_kept_after_fixing()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "hin"), Sub(4, "hin"));
        var rules = new GuessRules { FixWrongTags = true, CheckEveryTextSubtitle = true };
        SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(English), null, rules);

        var plan = SubtitlePlanner.Plan(probe, KeepEnglish(), Video);
        Assert.Contains(3, plan.Keep.Select(s => s.index));
        Assert.Equal("eng", plan.LanguageChanges()[3]);
    }

    [Fact]
    public void Check_every_text_subtitle_reads_lone_tagged_tracks_too()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "fre"), Sub(4, "und"), Sub(5, "eng", forced: true), Sub(6, "spa", codec: "hdmv_pgs_subtitle"));
        Assert.Equal(new[] { 4 }, SubtitleLanguageGuessing.TracksToRead(probe).Select(s => s.index));
        Assert.Equal(new[] { 2, 3, 4 }, SubtitleLanguageGuessing.TracksToRead(probe, everyTrack: true).Select(s => s.index));
    }

    [Fact]
    public void Older_settings_keep_fixing_tags_only_when_guessing_already_changed_tracks()
    {
        var changedTracks = new PluginOptions { GuessLanguages = true, GuessForFilesOnly = false };
        var filesOnly = new PluginOptions { GuessLanguages = true, GuessForFilesOnly = true };
        var newInstall = new PluginOptions();
        OptionsStore.Upgrade(changedTracks);
        OptionsStore.Upgrade(filesOnly);
        OptionsStore.Upgrade(newInstall);

        Assert.Equal(WrongLanguageTags.FixTheTag, changedTracks.WrongLanguageTags);
        Assert.Equal(WrongLanguageTags.LeaveThem, filesOnly.WrongLanguageTags);
        Assert.Equal(WrongLanguageTags.LeaveThem, newInstall.WrongLanguageTags);

        // Once upgraded, a later choice sticks.
        changedTracks.WrongLanguageTags = WrongLanguageTags.LeaveThem;
        OptionsStore.Upgrade(changedTracks);
        Assert.Equal(WrongLanguageTags.LeaveThem, changedTracks.WrongLanguageTags);
    }

    [Fact]
    public void The_wrong_tag_choices_change_the_settings_signature()
    {
        var leave = KeepEnglish();
        var fix = KeepEnglish();
        fix.FixWrongTags = true;
        var fixRemove = KeepEnglish();
        fixRemove.FixWrongTags = true;
        fixRemove.RemoveUnknownWrongTags = true;
        Assert.Equal(3, new[] { leave.Signature(), fix.Signature(), fixRemove.Signature() }.Distinct().Count());
    }

    [Fact]
    public void A_checkpoint_is_kept_between_runs_and_carries_the_totals()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-checkpoint-").FullName;
        try
        {
            var store = new CheckpointStore(dir, new TestJsonSerializer());
            Assert.False(store.Exists);
            var checkpoint = new RunCheckpoint
            {
                Key = RunCheckpoint.KeyFor("sig", true, "12082", ""),
                DryRun = true,
                LogFile = "subremover-1.txt",
                Checked = { Video },
                Counts = { ["WouldChange"] = 1 },
                Totals = new RunTotals { TracksRemoved = 3, WrongTags = 2 }
            };
            store.Save(checkpoint);

            var loaded = store.Load()!;
            Assert.Equal(checkpoint.Key, loaded.Key);
            Assert.Equal(new[] { Video }, loaded.Checked);
            Assert.Equal(1, loaded.Counts["WouldChange"]);
            var totals = new RunTotals();
            totals.CopyFrom(loaded.Totals);
            Assert.Equal(3, totals.TracksRemoved);
            Assert.Equal(2, totals.WrongTags);

            // Another library choice or Test mode is a different run.
            Assert.NotEqual(checkpoint.Key, RunCheckpoint.KeyFor("sig", false, "12082", ""));
            Assert.NotEqual(checkpoint.Key, RunCheckpoint.KeyFor("sig", true, "12082,12083", ""));

            store.Delete();
            Assert.False(store.Exists);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
