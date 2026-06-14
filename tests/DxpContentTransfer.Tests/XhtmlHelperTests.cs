using System.Text.Json;
using System.Text.Json.Nodes;
using DxpContentTransfer.Services;
using Xunit;

namespace DxpContentTransfer.Tests;

public class XhtmlHelperTests
{
    // ── NormalizeInlineImagePath ───────────────────────────────────────────────
    [Theory]
    [InlineData("/EPiServer/CMS/Content/globalassets/en/foo/bar.jpg,,105?epieditmode=false", "/globalassets/en/foo/bar.jpg")]
    [InlineData("/EPiServer/CMS/Content/globalassets/en/x.jpg,,108%3Fepieditmode=false", "/globalassets/en/x.jpg")] // %3F decodes to ?
    [InlineData("/contentassets/abc/y.png,,55", "/contentassets/abc/y.png")]
    [InlineData("/globalassets/en/x.jpg", "/globalassets/en/x.jpg")]
    [InlineData("/globalassets/x.jpg#frag", "/globalassets/x.jpg")]
    public void NormalizeInlineImagePath_decodes_strips_query_version_and_editmode_prefix(string input, string expected)
        => Assert.Equal(expected, XhtmlProcessor.NormalizeInlineImagePath(input));

    [Fact]
    public void NormalizeInlineImagePath_passes_through_null_and_empty()
    {
        Assert.Null(XhtmlProcessor.NormalizeInlineImagePath(null));
        Assert.Equal("", XhtmlProcessor.NormalizeInlineImagePath(""));
    }

    // ── ToRelativePath ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData("https://x.com/a/b?c=1", "/a/b?c=1")]
    [InlineData("/a/b", "/a/b")]
    public void ToRelativePath_returns_path_for_absolute_or_rooted(string input, string expected)
        => Assert.Equal(expected, XhtmlProcessor.ToRelativePath(input));

    [Theory]
    [InlineData("relative/no/slash")]
    [InlineData("")]
    [InlineData(null)]
    public void ToRelativePath_returns_null_for_non_paths(string input)
        => Assert.Null(XhtmlProcessor.ToRelativePath(input));

    // ── ParseContentFragments ──────────────────────────────────────────────────
    [Fact]
    public void ParseContentFragments_extracts_guid_link_and_name_in_order()
    {
        var html = """
            <div class="epi-contentfragment" contenteditable="false" data-classid="36f4349b-8093-492b-b616-05d8964e4c89" data-contentguid="5e3ad3f2-7423-4fd8-9ba1-3303e7a1318a" data-contentlink="118" data-contentname="MattyP">MattyP</div>
            <div class="epi-contentfragment" data-contentguid="019e9700-572f-49ad-afa0-766ff42cab50" data-contentlink="37" data-contentname="Alloy Plan teaser">Alloy Plan teaser</div>
            """;
        var frags = XhtmlProcessor.ParseContentFragments(html).ToList();

        Assert.Equal(2, frags.Count);
        Assert.Equal(Guid.Parse("5e3ad3f2-7423-4fd8-9ba1-3303e7a1318a"), frags[0].Guid);
        Assert.Equal(118, frags[0].ContentLink);
        Assert.Equal("MattyP", frags[0].Name);
        Assert.Equal(37, frags[1].ContentLink);
        Assert.Equal("Alloy Plan teaser", frags[1].Name);
    }

    [Fact]
    public void ParseContentFragments_tolerates_shuffled_attribute_order_and_missing_link()
    {
        var html = """<div data-contentname="X" class="epi-contentfragment" data-contentguid="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee">X</div>""";
        var frag = Assert.Single(XhtmlProcessor.ParseContentFragments(html));
        Assert.Equal(0, frag.ContentLink); // no data-contentlink ⇒ 0
        Assert.Equal("X", frag.Name);
    }

    [Fact]
    public void ParseContentFragments_skips_fragment_divs_without_a_guid()
        => Assert.Empty(XhtmlProcessor.ParseContentFragments("<div class=\"epi-contentfragment\">no guid here</div>"));

    [Theory]
    [InlineData("<p>hello <b>world</b></p>")]
    [InlineData("")]
    public void ParseContentFragments_returns_empty_for_plain_html(string html)
        => Assert.Empty(XhtmlProcessor.ParseContentFragments(html));

    // ── ExtractXhtmlImageUrls (img src + a href) ───────────────────────────────
    [Fact]
    public void ExtractXhtmlImageUrls_collects_both_img_src_and_a_href()
    {
        var json = Xhtml("<p><img src=\"/a.jpg\" /><a href=\"/b.mp4\">link</a></p>");
        Assert.Equal(new[] { "/a.jpg", "/b.mp4" }, XhtmlProcessor.ExtractXhtmlImageUrls(json));
    }

    [Fact]
    public void ExtractXhtmlImageUrls_ignores_non_xhtml_properties()
    {
        var json = new JsonObject
        {
            ["title"] = new JsonObject
            {
                ["propertyDataType"] = "PropertyString",
                ["value"] = "<img src=\"/nope.jpg\">"
            }
        }.ToJsonString();
        Assert.Empty(XhtmlProcessor.ExtractXhtmlImageUrls(json));
    }

    // ── ExtractXhtmlContentFragments ───────────────────────────────────────────
    [Fact]
    public void ExtractXhtmlContentFragments_reads_fragments_from_the_xhtml_value()
    {
        var json = Xhtml("<div class=\"epi-contentfragment\" data-contentguid=\"5e3ad3f2-7423-4fd8-9ba1-3303e7a1318a\" data-contentlink=\"118\" data-contentname=\"MattyP\">MattyP</div>");
        var frag = Assert.Single(XhtmlProcessor.ExtractXhtmlContentFragments(json));
        Assert.Equal(118, frag.ContentLink);
        Assert.Equal("MattyP", frag.Name);
    }

    // ── RewriteXhtmlUrls ───────────────────────────────────────────────────────
    [Fact]
    public void RewriteXhtmlUrls_remaps_embedded_comma_id()
    {
        var value = Rewrite("<img src=\"/x.jpg,,105?epieditmode=false\">", idMap: new() { [105] = 1692 });
        Assert.Contains(",,1692?", value);
        Assert.DoesNotContain(",,105?", value);
    }

    [Fact]
    public void RewriteXhtmlUrls_comma_id_remap_respects_digit_boundary()
    {
        // 105 must not match inside 1050.
        var value = Rewrite("<img src=\"/x.jpg,,1050\">", idMap: new() { [105] = 1692 });
        Assert.Contains(",,1050", value);
    }

    [Fact]
    public void RewriteXhtmlUrls_remaps_inline_block_contentlink()
    {
        var value = Rewrite("<div class=\"epi-contentfragment\" data-contentlink=\"118\">x</div>", blockMap: new() { [118] = 200 });
        Assert.Contains("data-contentlink=\"200\"", value);
        Assert.DoesNotContain("data-contentlink=\"118\"", value);
    }

    [Fact]
    public void RewriteXhtmlUrls_strips_source_origin_then_applies_url_map()
    {
        var value = Rewrite("<a href=\"https://src.example.com/old.jpg\">x</a>", urlMap: new() { ["/old.jpg"] = "/new.jpg" });
        Assert.Contains("href=\"/new.jpg\"", value);
        Assert.DoesNotContain("src.example.com", value);
    }

    // ── parser robustness (cases the old double-quote-only regex missed) ───────
    [Fact]
    public void ParseContentFragments_handles_single_quoted_attributes()
    {
        var html = "<div class='epi-contentfragment' data-contentguid='5e3ad3f2-7423-4fd8-9ba1-3303e7a1318a' data-contentlink='118' data-contentname='MattyP'>MattyP</div>";
        var frag = Assert.Single(XhtmlProcessor.ParseContentFragments(html));
        Assert.Equal(118, frag.ContentLink);
        Assert.Equal("MattyP", frag.Name);
    }

    [Fact]
    public void ExtractXhtmlImageUrls_handles_single_quoted_src()
    {
        var json = Xhtml("<img src='/single.jpg'>");
        Assert.Equal(new[] { "/single.jpg" }, XhtmlProcessor.ExtractXhtmlImageUrls(json));
    }

    // ── helpers ────────────────────────────────────────────────────────────────
    private const string SourceBaseUrl = "https://src.example.com";

    private static string Xhtml(string innerHtml) => new JsonObject
    {
        ["body"] = new JsonObject
        {
            ["propertyDataType"] = "PropertyXhtmlString",
            ["value"] = innerHtml
        }
    }.ToJsonString();

    // Runs RewriteXhtmlUrls over a single PropertyXhtmlString and returns the rewritten value.
    private static string Rewrite(
        string innerHtml,
        Dictionary<string, string> urlMap = null,
        Dictionary<int, int> idMap = null,
        Dictionary<int, int> blockMap = null)
    {
        var rewritten = XhtmlProcessor.RewriteXhtmlUrls(Xhtml(innerHtml), SourceBaseUrl, urlMap, idMap, blockMap);
        using var doc = JsonDocument.Parse(rewritten);
        return doc.RootElement.GetProperty("body").GetProperty("value").GetString();
    }
}
