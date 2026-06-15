using System.Text.Json;
using DxpContentTransfer.Services;
using Xunit;

namespace DxpContentTransfer.Tests;

public class MultilingualTests
{
    // A trimmed-down CMA payload for a Spanish ("es") branch of a page that exists in en + es,
    // with one culture-specific property (teaserText) and two culture-invariant ones
    // (hideSiteFooter, pageImage).
    private const string SpanishBranchJson = """
    {
      "contentLink": { "id": 1712, "guidValue": "738c8be6-7737-47ef-9e12-61ede1df8367" },
      "name": "Traseros",
      "language": { "name": "es", "displayName": "Spanish" },
      "existingLanguages": [ { "name": "en" }, { "name": "es" } ],
      "masterLanguage": { "name": "en" },
      "contentType": [ "Page", "LandingPage" ],
      "parentLink": { "id": 1698, "guidValue": "50525f0c-504a-4c8c-8b86-f8c74dbe800a" },
      "routeSegment": "traseros",
      "url": "https://example.com/es/claude-testing/traseros/",
      "status": "Published",
      "teaserText": { "value": "NO AMAMOS LOS TRASEROS", "propertyDataType": "PropertyLongString" },
      "hideSiteFooter": { "value": true, "propertyDataType": "PropertyBoolean" },
      "pageImage": { "value": { "id": 1699, "guidValue": "a7b1d9eb-a0b6-4651-95b7-83f366f15d38" }, "propertyDataType": "PropertyContentReference" }
    }
    """;

    [Fact]
    public void ExtractExistingLanguages_lists_all_branches_in_order()
        => Assert.Equal(new[] { "en", "es" }, ContentTransferService.ExtractExistingLanguages(SpanishBranchJson));

    [Fact]
    public void ExtractMasterLanguage_and_ContentLanguage()
    {
        Assert.Equal("en", ContentTransferService.ExtractMasterLanguage(SpanishBranchJson));
        Assert.Equal("es", ContentTransferService.ExtractContentLanguage(SpanishBranchJson));
    }

    [Fact]
    public void ExtractExistingLanguages_empty_when_absent()
        => Assert.Empty(ContentTransferService.ExtractExistingLanguages("""{ "name": "x" }"""));

    [Fact]
    public void BuildLanguageBranchJson_keeps_culture_specific_props_and_drops_invariant_ones()
    {
        var parent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var cultureSpecific = new HashSet<string> { "teaserText" };

        var result = ContentTransferService.BuildLanguageBranchJson(SpanishBranchJson, cultureSpecific, parent, "CheckedOut");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Culture-specific property kept; culture-invariant ones dropped (these cause the 409).
        Assert.True(root.TryGetProperty("teaserText", out _));
        Assert.False(root.TryGetProperty("hideSiteFooter", out _));
        Assert.False(root.TryGetProperty("pageImage", out _));

        // System fields the branch write needs are retained.
        Assert.Equal("Traseros", root.GetProperty("name").GetString());
        Assert.Equal("es", root.GetProperty("language").GetProperty("name").GetString());
        Assert.Equal("LandingPage", root.GetProperty("contentType")[1].GetString());

        // Parent bound by guid; status overridden; read-only/system fields stripped.
        Assert.Equal(parent.ToString(), root.GetProperty("parentLink").GetProperty("guidValue").GetString());
        Assert.Equal("CheckedOut", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("contentLink", out _));
        Assert.False(root.TryGetProperty("existingLanguages", out _));
        Assert.False(root.TryGetProperty("masterLanguage", out _));
        Assert.False(root.TryGetProperty("routeSegment", out _));
        Assert.False(root.TryGetProperty("url", out _));
    }
}
