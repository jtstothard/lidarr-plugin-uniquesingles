using UniqueSingles.Matching;
using Xunit;

namespace UniqueSingles.Test.Matching;

public class TitleNormalizerTests
{
    [Fact]
    public void Normalize_NullInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Normalize_WhitespaceOnly_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TitleNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_AllCapsWithExclamation_StripsPunctuationAndLowercases()
    {
        // "HOT TO GO!" → "hot to go"
        Assert.Equal("hot to go", TitleNormalizer.Normalize("HOT TO GO!"));
    }

    [Fact]
    public void Normalize_FeatAnnotation_StripsEntireParenthetical()
    {
        // "Parting Gift (feat. Brendan Kelly)" → "parting gift"
        Assert.Equal("parting gift", TitleNormalizer.Normalize("Parting Gift (feat. Brendan Kelly)"));
    }

    [Fact]
    public void Normalize_FeatDotlessAnnotation_StripsEntireParenthetical()
    {
        // "Song (feat Artist)" → "song"
        Assert.Equal("song", TitleNormalizer.Normalize("Song (feat Artist)"));
    }

    [Fact]
    public void Normalize_FeaturingAnnotation_StripsEntireParenthetical()
    {
        // "Song (featuring Some Artist)" → "song"
        Assert.Equal("song", TitleNormalizer.Normalize("Song (featuring Some Artist)"));
    }

    [Fact]
    public void Normalize_AcousticVersionSuffix_PreservesVersionSuffix()
    {
        // "Die Young (acoustic)" → "die young (acoustic)" — NOT stripped
        Assert.Equal("die young (acoustic)", TitleNormalizer.Normalize("Die Young (acoustic)"));
    }

    [Fact]
    public void Normalize_RemixVersionSuffix_PreservesVersionSuffix()
    {
        // "Good Hurt (Aevion remix)" → "good hurt (aevion remix)" — NOT stripped
        Assert.Equal("good hurt (aevion remix)", TitleNormalizer.Normalize("Good Hurt (Aevion remix)"));
    }

    [Fact]
    public void Normalize_RadioEditSuffix_PreservesVersionSuffix()
    {
        Assert.Equal("song title (radio edit)", TitleNormalizer.Normalize("Song Title (radio edit)"));
    }

    [Fact]
    public void Normalize_AlreadyNormalized_ReturnsUnchanged()
    {
        Assert.Equal("simple title", TitleNormalizer.Normalize("simple title"));
    }

    [Fact]
    public void Normalize_MultipleWhitespace_CollapsesToSingle()
    {
        Assert.Equal("multi space title", TitleNormalizer.Normalize("Multi   Space   Title"));
    }

    [Fact]
    public void Normalize_TrailingQuestionMark_Strips()
    {
        Assert.Equal("are you sure", TitleNormalizer.Normalize("Are You Sure?"));
    }

    [Fact]
    public void Normalize_TrailingPeriod_Strips()
    {
        Assert.Equal("the end", TitleNormalizer.Normalize("The End."));
    }

    [Fact]
    public void Normalize_MultipleTrailingPunctuation_StripsAll()
    {
        Assert.Equal("what", TitleNormalizer.Normalize("What?!"));
    }

    [Fact]
    public void Normalize_TrailingPunctuationAfterFeat_StripsBoth()
    {
        // "Song (feat. Artist)!" → strip feat → "Song !" → strip trailing → "song"
        Assert.Equal("song", TitleNormalizer.Normalize("Song (feat. Artist)!"));
    }

    [Fact]
    public void Normalize_MixedCaseAndPunctuation_NormalizesCorrectly()
    {
        Assert.Equal("hot to go", TitleNormalizer.Normalize("Hot to Go!"));
    }

    [Fact]
    public void Normalize_CaseInsensitiveTitleMatch_BothNormalizeSame()
    {
        // Verify that differently-cased versions of the same title normalize identically
        var album = TitleNormalizer.Normalize("HOT TO GO!");
        var single = TitleNormalizer.Normalize("Hot to Go!");
        Assert.Equal(album, single);
    }

    [Fact]
    public void Normalize_DurationToleranceExample_FromEdgeCases()
    {
        // "Pink Pony Club" with no special formatting
        Assert.Equal("pink pony club", TitleNormalizer.Normalize("Pink Pony Club"));
    }

    [Fact]
    public void Normalize_ParentheticalNotFeat_PreservesParenthetical()
    {
        // Parenthetical that isn't "feat." or "featuring" should be preserved
        Assert.Equal("school nights (live)", TitleNormalizer.Normalize("School Nights (live)"));
    }

    [Fact]
    public void Normalize_TrailingSemicolonAndColon_Strips()
    {
        Assert.Equal("song", TitleNormalizer.Normalize("Song;"));
        Assert.Equal("song", TitleNormalizer.Normalize("Song:"));
    }

    [Fact]
    public void Normalize_PunctuationInMiddle_Preserves()
    {
        // Punctuation in the middle of the title should be preserved
        Assert.Equal("it's a test", TitleNormalizer.Normalize("It's a Test"));
    }

    [Fact]
    public void Normalize_TabAndNewline_CollapsesToSpace()
    {
        Assert.Equal("tab separated", TitleNormalizer.Normalize("Tab\tSeparated"));
        Assert.Equal("newline separated", TitleNormalizer.Normalize("Newline\nSeparated"));
    }

    [Fact]
    public void Normalize_FeatWithMultipleArtists_StripsEntireParenthetical()
    {
        Assert.Equal("collab", TitleNormalizer.Normalize("Collab (feat. Artist1 & Artist2)"));
    }
}
