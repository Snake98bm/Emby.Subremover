using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class LanguageGuesserTests
{
    private const string Video = @"D:\Movies\My Movie (2020)\My Movie (2020).mkv";

    // Everyday dialogue written for these tests, wrapped like an .srt file.
    private static string Srt(string text) => $"1\n00:00:01,000 --> 00:00:04,000\n<i>{text}</i>\n\n2\n00:00:05,000 --> 00:00:06,000\n{{\an8}}...\n\n";

    [Theory]
    [InlineData("eng", "I don't know what you want from me. We have to go there right now, okay? Yeah, I think that is the right thing to do. You can get the car and we will meet at the house. They are not going to wait for us all night, so just be ready.")]
    [InlineData("fre", "Je ne sais pas ce que tu veux. Nous devons partir maintenant, d'accord ? Oui, je pense que c'est la bonne chose à faire. Tu peux prendre la voiture et nous allons nous retrouver à la maison. Ils ne vont pas attendre toute la nuit, alors sois prêt avec moi.")]
    [InlineData("ger", "Ich weiß nicht, was du von mir willst. Wir müssen jetzt sofort gehen, ja? Ich glaube, das ist das Richtige. Du kannst das Auto nehmen und wir treffen uns im Haus. Sie werden nicht die ganze Nacht auf uns warten, also sei bereit. Ich bin hier, und es ist auch noch Zeit.")]
    [InlineData("spa", "No sé qué quieres de mí. Tenemos que irnos ahora mismo, ¿está bien? Sí, creo que es lo correcto. Puedes tomar el coche y nos vemos en la casa. Ellos no van a esperar toda la noche por nosotros, así que estoy listo y vamos con ella para eso.")]
    [InlineData("ita", "Non so cosa vuoi da me. Dobbiamo andare adesso, va bene? Sì, penso che sia la cosa giusta da fare. Puoi prendere la macchina e ci vediamo a casa. Non ci aspetteranno per tutta la notte, quindi sono pronto e ho anche questo per te qui.")]
    [InlineData("por", "Eu não sei o que você quer de mim. Nós temos que ir agora, está bem? Sim, eu acho que isso é a coisa certa a fazer. Você pode pegar o carro e a gente se encontra em casa. Eles não vão esperar a noite toda, então estou pronto e muito bem.")]
    [InlineData("dut", "Ik weet niet wat je van me wilt. We moeten nu gaan, goed? Ja, ik denk dat het de juiste keuze is. Jij kunt de auto nemen en we zien elkaar bij het huis. Ze gaan niet de hele nacht op ons wachten, dus wees klaar, want ik ben er ook.")]
    [InlineData("swe", "Jag vet inte vad du vill ha av mig. Vi måste gå nu, okej? Ja, jag tror att det är det rätta att göra. Du kan ta bilen och vi ses vid huset. De kommer inte att vänta på oss hela natten, så var redo. Hon är här och han har det.")]
    [InlineData("nor", "Jeg vet ikke hva du vil med meg. Vi må gå nå, greit? Ja, jeg tror det er det riktige å gjøre. Du kan ta bilen og vi møtes ved huset. De kommer ikke til å vente på oss hele natten, så vær klar. Hun er her og han har det med deg, nei. Hva skal vi gjøre med meg nå?")]
    [InlineData("dan", "Jeg ved ikke hvad du vil med mig. Vi skal gå nu, okay? Ja, jeg tror det er det rigtige at gøre. Du kan tage bilen og vi ses ved huset. De vil ikke vente på os hele natten, så vær klar. Hun er her og han har det med dig, nej. Hvad skal vi gøre med mig nu?")]
    [InlineData("pol", "Nie wiem, czego ode mnie chcesz. Musimy już iść, dobrze? Tak, myślę, że to jest dobra rzecz. Możesz wziąć samochód i spotkamy się w domu. Oni nie będą na nas czekać całą noc, więc bądź gotowy. Ja jestem tu i on też, ale co z nią?")]
    [InlineData("rus", "Я не знаю, что ты от меня хочешь. Нам нужно идти прямо сейчас, хорошо? Да, я думаю, что это правильно. Ты можешь взять машину, и мы встретимся в доме. Они не будут ждать нас всю ночь, так что будь готов. Он здесь, и она тоже с ним, но где все?")]
    [InlineData("gre", "Δεν ξέρω τι θέλεις από μένα. Πρέπει να φύγουμε τώρα, εντάξει; Ναι, νομίζω ότι αυτό είναι το σωστό. Μπορείς να πάρεις το αυτοκίνητο και θα βρεθούμε στο σπίτι.")]
    [InlineData("jpn", "何が欲しいのか分からない。今すぐ行かなければならない、いいね？はい、それが正しいと思います。車を使っていいよ、家で会おう。")]
    [InlineData("kor", "나는 네가 나에게 원하는 게 뭔지 모르겠어. 우리는 지금 당장 가야 해, 알았지? 응, 그게 옳은 일인 것 같아. 차를 가져가도 돼, 집에서 만나자.")]
    [InlineData("chi", "我不知道你想要我做什么。我们现在必须走了，好吗？是的，我认为这是正确的事情。你可以开车，我们在家里见面。")]
    [InlineData("heb", "אני לא יודע מה אתה רוצה ממני. אנחנו חייבים ללכת עכשיו, בסדר? כן, אני חושב שזה הדבר הנכון לעשות.")]
    public void Guesses_the_language_of_dialogue(string expected, string dialogue)
    {
        var guess = LanguageGuesser.Guess(Srt(dialogue));

        Assert.True(expected == guess.Language, $"expected {expected}, got {guess.Language ?? "nothing"}: {guess.Reason}");
    }

    [Fact]
    public void Too_little_text_is_not_guessed()
    {
        var guess = LanguageGuesser.Guess(Srt("Hello there, how are you?"));

        Assert.Null(guess.Language);
        Assert.StartsWith("only 5 words", guess.Reason);
    }

    [Fact]
    public void Text_without_common_words_is_not_guessed()
    {
        var guess = LanguageGuesser.Guess(Srt(string.Join(" ", Enumerable.Repeat("zork blap quux", 15))));

        Assert.Null(guess.Language);
        Assert.StartsWith("no language's common words stand out", guess.Reason);
    }

    [Fact]
    public void A_guessed_track_is_kept_or_removed_like_any_other_and_kept_ones_get_the_language_written()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, null), Sub(4, "und"), Sub(5, "und"));
        SubtitleLanguageGuessing.SetGuessedLanguage(probe.streams[3], "fre");
        SubtitleLanguageGuessing.SetGuessedLanguage(probe.streams[4], "eng");
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), KeepUndefined = true };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        // #3 (no tag) is guessed French and goes; #4 is guessed English and stays; #5 can't be told and stays as 'und'.
        Assert.Equal(new[] { 2, 4, 5 }, plan.Keep.Select(s => s.index));
        Assert.Equal(new[] { 3 }, plan.Remove.Select(s => s.index));
        Assert.Equal(new Dictionary<int, string> { [4] = "eng" }, plan.LanguageChanges());
        Assert.Equal("#4 eng (guessed) subrip", probe.streams[4].Describe());
    }

    [Fact]
    public void Guessed_languages_alone_need_a_remux()
    {
        var probe = Movie(Sub(2, "und"));
        SubtitleLanguageGuessing.SetGuessedLanguage(probe.streams[2], "eng");

        var plan = SubtitlePlanner.Plan(probe, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng") }, Video);

        Assert.True(plan.NeedsRemux);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Guesses_only_for_files_extract_kept_languages_and_never_touch_the_track()
    {
        var probe = Movie(Sub(2, "und"), Sub(3, "und"), Sub(4, "und"));
        probe.streams[2].GuessedLanguage = "eng";
        probe.streams[3].GuessedLanguage = "fre";
        var settings = new PlannerSettings
        {
            KeepLanguages = PlannerSettings.ParseLanguages("eng"),
            KeepUndefined = true,
            GuessForFilesOnly = true,
            ExtractTextSubtitles = true,
            RemoveExtractedFromVideo = true,
            MarkDefaultInName = true
        };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Equal(new[] { 2, 3, 4 }, plan.Keep.Select(s => s.index));
        Assert.Empty(plan.Remove);
        Assert.False(plan.NeedsRemux);
        var job = Assert.Single(plan.Extract);
        Assert.Equal(2, job.Stream.index);
        Assert.EndsWith("My Movie (2020).eng.default.srt", job.OutputPath);
        Assert.Equal("#3 und (looks fre) subrip", probe.streams[3].Describe());
    }

    private static string Cues(int count, Func<int, string> line) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"{i}\n00:00:{i % 60:00},000 --> 00:00:{i % 60:00},900\n{line(i)}\n\n"));

    [Fact]
    public void Text_that_describes_sounds_or_names_speakers_looks_like_sdh()
    {
        var sdh = Cues(40, i => i % 5 == 0 ? "[door slams]" : i % 7 == 0 ? "JOHN: Get in the car." : "We have to go now.");
        var regular = Cues(40, i => i == 3 ? "(laughs) Okay." : "We have to go now.");

        Assert.True(SdhDetector.Looks(sdh, out var reason));
        Assert.Equal("12 of 40 lines describe sounds or name the speaker", reason);
        Assert.False(SdhDetector.Looks(regular, out _));
        Assert.False(SdhDetector.Looks(Cues(10, _ => "[music]"), out _));
    }

    [Fact]
    public void An_unlabelled_track_that_reads_like_sdh_gets_an_sdh_file_and_the_regular_one_the_default()
    {
        var probe = Movie(Sub(2, "und"), Sub(3, "und"));
        probe.streams[2].GuessedLanguage = "eng";
        probe.streams[3].GuessedLanguage = "eng";
        probe.streams[3].LooksHearingImpaired = true;
        var settings = new PlannerSettings
        {
            KeepLanguages = PlannerSettings.ParseLanguages("eng"),
            KeepUndefined = true,
            RemoveHearingImpaired = true,
            GuessForFilesOnly = true,
            ExtractTextSubtitles = true,
            MarkDefaultInName = true
        };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Equal(new[] { "My Movie (2020).eng.default.srt", "My Movie (2020).eng.sdh.srt" },
            plan.Extract.Select(e => Path.GetFileName(e.OutputPath.Replace('\\', Path.DirectorySeparatorChar))));
        Assert.Equal(new[] { 2, 3 }, plan.Keep.Select(s => s.index));
        Assert.Equal("#3 und (looks eng) subrip (looks SDH)", probe.streams[3].Describe());
    }

    [Theory]
    [InlineData("cat", "No sé què vols de mi. Hem d'anar-hi ara mateix, d'acord? Sí, crec que és el que cal fer. Pots agafar el cotxe i ens veiem a casa. No ens esperaran tota la nit, així que estigues preparat, però jo també hi seré amb ells i amb tu.")]
    [InlineData("mac", "Не знам што сакаш од мене. Мораме да одиме сега, во ред? Да, мислам дека тоа е вистинската работа. Можеш да го земеш автомобилот и ќе се видиме дома. Тие нема да не чекаат цела ноќ, така што биди подготвен. Јас сум тука и тој е со неа.")]
    [InlineData("srp", "Не знам шта хоћеш од мене. Морамо да идемо сада, у реду? Да, мислим да је то права ствар. Можеш да узмеш ауто и видећемо се код куће. Они нас неће чекати целу ноћ, па буди спреман. Ја сам овде и он је са њом, али шта ти мислиш?")]
    [InlineData("hin", "मुझे नहीं पता कि तुम मुझसे क्या चाहते हो। हमें अभी जाना है, ठीक है? हाँ, मुझे लगता है कि यह सही है। तुम गाड़ी ले सकते हो और हम घर पर मिलेंगे। वे पूरी रात हमारा इंतज़ार नहीं करेंगे, तो तैयार रहो। मैं यहाँ हूँ और वह भी है।")]
    [InlineData("mar", "मला माहित नाही तुला माझ्याकडून काय हवं आहे. आपल्याला आत्ता जायला हवं, ठीक आहे? हो, मला वाटतं हे बरोबर आहे. तू गाडी घेऊ शकतोस आणि आपण घरी भेटू. ते रात्रभर आपली वाट पाहणार नाहीत, म्हणून तयार राहा. मी इथे आहे आणि तो पण आहे.")]
    public void Close_neighbours_are_not_given_the_nearest_big_language(string expected, string dialogue)
    {
        var guess = LanguageGuesser.Guess(Srt(dialogue));

        Assert.True(expected == guess.Language, $"expected {expected}, got {guess.Language ?? "nothing"}: {guess.Reason}");
    }

    [Fact]
    public void A_stray_kana_letter_in_chinese_text_stays_chinese()
    {
        var guess = LanguageGuesser.Guess(Srt("我不知道你想要我做什么。我们现在必须走了，好吗？是的，我认为这是正确的事情。你可以开车，我们在家里见面。他叫卡ー尔。"));

        Assert.Equal("chi", guess.Language);
    }

    [Fact]
    public void Guessed_text_tracks_stop_ocr_of_a_picture_track_in_the_same_language()
    {
        var probe = Movie(Sub(2, "und"), Sub(3, "eng", codec: "hdmv_pgs_subtitle"));
        probe.streams[2].GuessedLanguage = "eng";
        var settings = new PlannerSettings
        {
            KeepLanguages = PlannerSettings.ParseLanguages("eng"),
            KeepUndefined = true,
            GuessForFilesOnly = true,
            ExtractTextSubtitles = true,
            OcrPictureSubtitles = true
        };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Single(plan.Extract);
        Assert.Empty(plan.Ocr);
    }

    [Theory]
    [InlineData(@"D:\TV\Show\S01E01.m2ts")]
    [InlineData(@"D:\TV\Show\S01E01.avi")]
    public void Containers_that_cant_store_flags_get_no_default_or_language_change(string video)
    {
        var probe = Movie(Sub(2, "eng", sdh: true, isDefault: true), Sub(3, "eng"), Sub(4, "und"));
        SubtitleLanguageGuessing.SetGuessedLanguage(probe.streams[4], "eng");
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), MakeRegularDefault = true, DefaultLanguage = "eng" };

        var plan = SubtitlePlanner.Plan(probe, settings, video);

        Assert.Null(plan.NewDefault);
        Assert.Empty(plan.LanguageChanges());
        Assert.False(plan.NeedsRemux);
    }

    private const string English = "I don't know what you want from me. We have to go there right now, okay? Yeah, I think that is the right thing to do. You can get the car and we will meet at the house. They are not going to wait for us all night, so just be ready.";
    private const string French = "Je ne sais pas ce que tu veux. Nous devons partir maintenant, d'accord ? Oui, je pense que c'est la bonne chose à faire. Tu peux prendre la voiture et nous allons nous retrouver à la maison. Ils ne vont pas attendre toute la nuit, alors sois prêt avec moi.";

    [Fact]
    public void Tracks_without_a_language_and_tracks_sharing_one_are_read()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"), Sub(4, "fre"), Sub(5, "und"), Sub(6, "eng", forced: true), Sub(7, "spa", codec: "hdmv_pgs_subtitle"), Sub(8, "spa", codec: "hdmv_pgs_subtitle"));

        Assert.Equal(new[] { 2, 3, 5 }, SubtitleLanguageGuessing.TracksToRead(probe).Select(s => s.index));
    }

    [Fact]
    public void A_track_tagged_english_that_reads_as_french_counts_as_french()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"));

        var right = SubtitleLanguageGuessing.Apply(probe.streams[2], Srt(English), null, filesOnly: false);
        var wrong = SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(French), null, filesOnly: false);

        Assert.False(right.WrongTag);
        Assert.Equal("eng", probe.streams[2].Language);
        Assert.False(probe.streams[2].LanguageGuessed);
        Assert.True(wrong.WrongTag);
        Assert.Equal("fre", probe.streams[3].Language);

        var plan = SubtitlePlanner.Plan(probe, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng") }, Video);
        Assert.Equal(new[] { 3 }, plan.Remove.Select(s => s.index));
    }

    [Fact]
    public void In_files_only_mode_a_wrongly_tagged_track_stays_but_gets_no_english_file()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"));
        SubtitleLanguageGuessing.Apply(probe.streams[2], Srt(English), null, filesOnly: true);
        SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(French), null, filesOnly: true);
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), ExtractTextSubtitles = true, RemoveExtractedFromVideo = true };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Equal(new[] { 3 }, plan.Keep.Select(s => s.index));
        Assert.Equal(new[] { 2 }, plan.Extract.Select(e => e.Stream.index));
        Assert.EndsWith("My Movie (2020).eng.srt", plan.Extract[0].OutputPath);
        Assert.Equal("#3 eng (looks fre) subrip", probe.streams[3].Describe());
    }

    [Theory]
    [InlineData("hrv", "srp", false)]   // close neighbours keep their tag
    [InlineData("bos", "hrv", false)]   // no word list to check the tag against
    [InlineData("eng", "fre", true)]
    public void A_tag_is_only_overridden_on_clear_evidence(string tag, string guessed, bool contradicts)
    {
        var hits = new Dictionary<string, int> { ["hrv"] = 200, ["srp"] = 190, ["eng"] = 5, ["fre"] = 300 };

        Assert.Equal(contradicts, LanguageGuesser.ContradictsTag(new LanguageGuess { Language = guessed, Hits = hits }, tag));
    }

    [Fact]
    public void Chinese_text_under_an_english_tag_is_wrong_but_under_a_cantonese_tag_it_stays()
    {
        var chinese = new LanguageGuess { Language = "chi" };

        Assert.True(LanguageGuesser.ContradictsTag(chinese, "eng"));
        Assert.False(LanguageGuesser.ContradictsTag(chinese, "yue"));
    }

    [Fact]
    public void An_untitled_sdh_copy_of_a_tagged_track_gets_an_sdh_file()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"));
        var sdhText = string.Concat(Enumerable.Range(1, 30).Select(i => $"{i}\n00:00:{i:00},000 --> 00:00:{i:00},900\n{(i % 3 == 0 ? "[door slams]" : "We have to go now, okay? Yeah.")}\n\n"));
        SubtitleLanguageGuessing.Apply(probe.streams[3], sdhText + Srt(English), null, filesOnly: true);
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), ExtractTextSubtitles = true };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.EndsWith("My Movie (2020).eng.srt", plan.Extract[0].OutputPath);
        Assert.EndsWith("My Movie (2020).eng.sdh.srt", plan.Extract[1].OutputPath);
    }

    // Lithuanian-like text: no word list knows it, but it clearly isn't English.
    private const string Unlisted = "Nežinau, ko tu iš manęs nori. Turime eiti dabar pat, gerai? Taip, manau, kad tai teisingas dalykas. Gali pasiimti mašiną ir susitiksime namuose. Jie nelauks mūsų visą naktį, todėl būk pasiruošęs. Aš esu čia ir jis taip pat yra su ja.";

    [Fact]
    public void A_track_tagged_english_whose_text_isnt_english_gets_no_english_file()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"));
        SubtitleLanguageGuessing.Apply(probe.streams[2], Srt(English), null, filesOnly: true);
        var reading = SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(Unlisted), null, filesOnly: true);
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), ExtractTextSubtitles = true };

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Null(reading.Guess.Language);
        Assert.True(reading.WrongTag);
        Assert.Equal(new[] { 2 }, plan.Extract.Select(e => e.Stream.index));
        Assert.Equal(new[] { 2, 3 }, plan.Keep.Select(s => s.index));
        Assert.Equal("#3 eng (text isn't eng) subrip", probe.streams[3].Describe());
    }

    [Fact]
    public void Without_files_only_a_track_whose_text_isnt_its_tag_counts_as_having_no_language()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng"));
        SubtitleLanguageGuessing.Apply(probe.streams[3], Srt(Unlisted), null, filesOnly: false);

        Assert.Equal("und", probe.streams[3].Language);
        var keepUnd = SubtitlePlanner.Plan(probe, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), KeepUndefined = true }, Video);
        Assert.Equal(new Dictionary<int, string> { [3] = "und" }, keepUnd.LanguageChanges());
        var dropUnd = SubtitlePlanner.Plan(probe, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), KeepUndefined = false }, Video);
        Assert.Equal(new[] { 3 }, dropUnd.Remove.Select(s => s.index));
    }

    [Fact]
    public void A_close_neighbour_of_the_tag_is_not_ruled_out()
    {
        var guess = new LanguageGuess { Hits = new Dictionary<string, int> { ["hrv"] = 109, ["srp"] = 103 }, Words = 355 };

        Assert.False(LanguageGuesser.RulesOutTag(guess, "hrv"));
        Assert.False(LanguageGuesser.RulesOutTag(guess, "bos"));
    }
}
