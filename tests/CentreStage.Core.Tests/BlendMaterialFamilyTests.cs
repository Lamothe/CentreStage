using CentreStage.Core;

namespace CentreStage.Core.Tests;

public class BlendMaterialFamilyTests
{
    [Theory]
    [InlineData("Hair")]
    [InlineData("Hair_Transparency")]
    [InlineData("Hair_Transparency.001")]
    [InlineData("BabyHair_Transparency")]
    [InlineData("Scalp_Transparency")]
    [InlineData("long_hair_cards")]
    [InlineData("Hair2")]
    [InlineData("hair1")]
    [InlineData("HairCard.003")]
    public void Matches_EveryPartOfAHairSet(string name)
    {
        Assert.True(BlendMaterialFamily.Matches(name, BlendMaterialFamily.Hair));
    }

    [Theory]
    [InlineData("HAIR_Transparency")]
    [InlineData("BabyHAIR_Transparency")]
    [InlineData("SCALP_Transparency")]
    [InlineData("hair")]
    [InlineData("HAIRTransparency")]
    [InlineData("FPHair_Cards")]
    [InlineData("2Hair")]
    [InlineData("hair02Cards")]
    public void Matches_IsCaseInsensitiveAndSplitsAcronyms(string name)
    {
        Assert.True(BlendMaterialFamily.Matches(name, BlendMaterialFamily.Hair));
    }

    // Substring matching would hit the first group; the rest are ordinary names that must
    // stay untouched. The boost is on by default, so a false positive silently thickens
    // a material that has nothing to do with hair.
    [Theory]
    [InlineData("Chair_Fabric")]
    [InlineData("ChairLeather")]
    [InlineData("Mohair_Trim")]
    [InlineData("Hairpin")]
    [InlineData("hairline")]
    public void DoesNotMatch_MaterialsThatMerelyContainTheLetters(string name)
    {
        Assert.False(BlendMaterialFamily.Matches(name, BlendMaterialFamily.Hair));
    }

    [Theory]
    [InlineData("Std_Skin_Head")]
    [InlineData("Std_Cornea_L")]
    [InlineData("pants_shirt")]
    [InlineData("Std_Eyelash")]
    public void DoesNotMatch_UnrelatedMaterials(string name)
    {
        Assert.False(BlendMaterialFamily.Matches(name, BlendMaterialFamily.Hair));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DoesNotMatch_MissingNames(string? name)
    {
        Assert.False(BlendMaterialFamily.Matches(name, BlendMaterialFamily.Hair));
    }

    [Fact]
    public void Matches_ReportsFalseForAnEmptyFamilyList()
    {
        Assert.False(BlendMaterialFamily.Matches("Hair_Transparency", ""));
        Assert.False(BlendMaterialFamily.Matches("Hair_Transparency", " , "));
        Assert.False(BlendMaterialFamily.Matches("Hair_Transparency", (string?)null));
        Assert.False(BlendMaterialFamily.Matches("Hair_Transparency", Array.Empty<string>()));
    }

    [Fact]
    public void Matches_HandlesSpacingAndEmptyEntries()
    {
        Assert.True(BlendMaterialFamily.Matches("Scalp_Transparency", " hair , , scalp "));
        Assert.False(BlendMaterialFamily.Matches("Scalp_Transparency", "hair,fringe"));
    }

    [Fact]
    public void Matches_KeepsSingleFragmentOverridesWorking()
    {
        Assert.True(BlendMaterialFamily.Matches("Hair_Transparency.001", "hair"));
        Assert.False(BlendMaterialFamily.Matches("Scalp_Transparency", "hair"));
    }

    [Fact]
    public void Parse_DropsEmptyEntriesAndWhitespace()
    {
        Assert.Equal(["hair", "scalp"], BlendMaterialFamily.Parse(" hair , , scalp "));
        Assert.Empty(BlendMaterialFamily.Parse("   "));
        Assert.Empty(BlendMaterialFamily.Parse(null));
    }

    [Fact]
    public void Matches_HandlesLeadingAndTrailingSeparators()
    {
        Assert.True(BlendMaterialFamily.Matches("_Hair_", "hair"));
        Assert.True(BlendMaterialFamily.Matches("Hair123", "hair"));
        Assert.False(BlendMaterialFamily.Matches("___", "hair"));
    }
}