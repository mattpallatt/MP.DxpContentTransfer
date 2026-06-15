using System.Text.Json;
using DxpContentTransfer.Services;
using Xunit;

namespace DxpContentTransfer.Tests;

public class JsonSurgeryTests
{
    // ── StripReadOnlyProperties ────────────────────────────────────────────────
    [Fact]
    public void StripReadOnlyProperties_removes_readonly_keys_and_environment_ids()
    {
        var json = """
        {
          "contentLink": { "id": 5, "guidValue": "11111111-1111-1111-1111-111111111111" },
          "parentLink":  { "id": 3, "guidValue": "22222222-2222-2222-2222-222222222222" },
          "name": "Cookies",
          "routeSegment": "cookies",
          "myRef": { "guidValue": "33333333-3333-3333-3333-333333333333", "id": 9, "workId": 2, "providerName": "p" }
        }
        """;
        using var doc = JsonDocument.Parse(ContentTransferService.StripReadOnlyProperties(json));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("contentLink", out _));
        Assert.False(root.TryGetProperty("parentLink", out _));
        Assert.False(root.TryGetProperty("routeSegment", out _));
        Assert.Equal("Cookies", root.GetProperty("name").GetString());

        var myRef = root.GetProperty("myRef");
        Assert.True(myRef.TryGetProperty("guidValue", out _));
        Assert.False(myRef.TryGetProperty("id", out _));
        Assert.False(myRef.TryGetProperty("workId", out _));
        Assert.False(myRef.TryGetProperty("providerName", out _));
    }

    [Fact]
    public void StripReadOnlyProperties_can_preserve_parentLink_but_still_strips_its_env_id()
    {
        var json = """{ "parentLink": { "id": 3, "guidValue": "22222222-2222-2222-2222-222222222222" }, "name": "x" }""";
        using var doc = JsonDocument.Parse(ContentTransferService.StripReadOnlyProperties(json, preserveParentLink: true));
        var parentLink = doc.RootElement.GetProperty("parentLink");

        Assert.Equal("22222222-2222-2222-2222-222222222222", parentLink.GetProperty("guidValue").GetString());
        Assert.False(parentLink.TryGetProperty("id", out _));
    }

    // ── InjectParentLink / InjectStatus ────────────────────────────────────────
    [Fact]
    public void InjectParentLink_sets_parent_guid()
    {
        var guid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using var doc = JsonDocument.Parse(ContentTransferService.InjectParentLink("""{ "name": "x" }""", guid));
        Assert.Equal(guid.ToString(), doc.RootElement.GetProperty("parentLink").GetProperty("guidValue").GetString());
    }

    [Fact]
    public void InjectStatus_sets_status()
    {
        using var doc = JsonDocument.Parse(ContentTransferService.InjectStatus("""{ "name": "x" }""", "Published"));
        Assert.Equal("Published", doc.RootElement.GetProperty("status").GetString());
    }

    // ── InjectTargetContentIds ─────────────────────────────────────────────────
    [Fact]
    public void InjectTargetContentIds_injects_id_and_zero_workId_for_mapped_guid()
    {
        var guid = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var json = $$"""{ "ref": { "guidValue": "{{guid}}" } }""";
        using var doc = JsonDocument.Parse(ContentTransferService.InjectTargetContentIds(json, new() { [guid] = 1692 }));
        var refNode = doc.RootElement.GetProperty("ref");

        Assert.Equal(1692, refNode.GetProperty("id").GetInt32());
        Assert.Equal(0, refNode.GetProperty("workId").GetInt32());
    }

    [Fact]
    public void InjectTargetContentIds_leaves_unmapped_refs_untouched()
    {
        var guid = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var unmapped = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var json = $$"""{ "ref": { "guidValue": "{{guid}}" } }""";
        using var doc = JsonDocument.Parse(ContentTransferService.InjectTargetContentIds(json, new() { [unmapped] = 1 }));
        Assert.False(doc.RootElement.GetProperty("ref").TryGetProperty("id", out _));
    }

    // ── ExtractContentReferenceGuids ───────────────────────────────────────────
    [Fact]
    public void ExtractContentReferenceGuids_collects_distinct_guids_in_first_seen_order()
    {
        var json = """
        { "a": { "guidValue": "11111111-1111-1111-1111-111111111111" },
          "b": [ { "guidValue": "22222222-2222-2222-2222-222222222222" },
                 { "guidValue": "11111111-1111-1111-1111-111111111111" } ] }
        """;
        Assert.Equal(
            new[]
            {
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222")
            },
            ContentTransferService.ExtractContentReferenceGuids(json));
    }

    // ── ReplaceSourceDomain ────────────────────────────────────────────────────
    [Fact]
    public void ReplaceSourceDomain_swaps_origin_in_every_string()
    {
        var json = """{ "u": "https://src.example.com/page", "nested": { "v": "go to https://src.example.com/x now" } }""";
        using var doc = JsonDocument.Parse(
            ContentTransferService.ReplaceSourceDomain(json, "https://src.example.com", "https://tgt.example.com"));

        Assert.Equal("https://tgt.example.com/page", doc.RootElement.GetProperty("u").GetString());
        Assert.Equal("go to https://tgt.example.com/x now", doc.RootElement.GetProperty("nested").GetProperty("v").GetString());
    }

    [Fact]
    public void ReplaceSourceDomain_is_noop_when_origins_match()
    {
        var json = """{ "u": "https://same.example.com/page" }""";
        Assert.Equal(json, ContentTransferService.ReplaceSourceDomain(json, "https://same.example.com", "https://same.example.com"));
    }

    // ── GetAssetBinaryUrl ──────────────────────────────────────────────────────
    [Fact]
    public void GetAssetBinaryUrl_returns_link_for_media()
    {
        var json = """{ "contentType": ["Media", "ImageFile"], "language": { "link": "/cms/content/x.jpg" } }""";
        Assert.Equal("/cms/content/x.jpg", ContentTransferService.GetAssetBinaryUrl(json));
    }

    [Fact]
    public void GetAssetBinaryUrl_null_for_non_media()
    {
        var json = """{ "contentType": ["Page"], "language": { "link": "/x" } }""";
        Assert.Null(ContentTransferService.GetAssetBinaryUrl(json));
    }

    [Fact]
    public void GetAssetBinaryUrl_null_when_media_has_no_link()
    {
        var json = """{ "contentType": ["Media"], "language": { } }""";
        Assert.Null(ContentTransferService.GetAssetBinaryUrl(json));
    }

    // ── IsLocalContent ─────────────────────────────────────────────────────────
    [Fact]
    public void IsLocalContent_true_from_own_url()
        => Assert.True(ContentTransferService.IsLocalContent("""{ "url": "https://x/contentassets/abc/" }"""));

    [Fact]
    public void IsLocalContent_true_from_parentLink_url_when_own_url_is_null()
    {
        // The CMA returns "url": null for content inside an asset folder; the parentLink still carries
        // the /contentassets/ path. Missing this fallback orphaned local blocks.
        var json = """{ "url": null, "parentLink": { "guidValue": "a03a69be-f9dc-426e-bc46-2dcc24cc0ec0", "url": "https://x/contentassets/a03a69bef9dc426ebc462dcc24cc0ec0/" } }""";
        Assert.True(ContentTransferService.IsLocalContent(json));
    }

    [Fact]
    public void IsLocalContent_false_for_global_content()
    {
        Assert.False(ContentTransferService.IsLocalContent("""{ "url": "https://x/globalassets/news/a.jpg", "parentLink": { "url": "https://x/globalassets/news/" } }"""));
        Assert.False(ContentTransferService.IsLocalContent("""{ "url": null, "parentLink": { "url": null } }"""));
    }
}
