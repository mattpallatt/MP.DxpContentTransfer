using DxpContentTransfer.Services;
using EPiServer.Core;
using Xunit;

namespace DxpContentTransfer.Tests;

public class ContentReferenceParserTests
{
    [Theory]
    [InlineData("123", 123)]
    [InlineData("123_456", 123)]   // "_" splits id from workId; only the id is used
    [InlineData("123:eng", 123)]   // ":" splits id from language
    public void Parse_returns_content_id(string input, int expectedId)
        => Assert.Equal(expectedId, ContentReferenceParser.Parse(input).ID);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("abc")]
    public void Parse_returns_empty_reference_for_missing_or_non_numeric(string input)
        => Assert.Equal(ContentReference.EmptyReference, ContentReferenceParser.Parse(input));
}
