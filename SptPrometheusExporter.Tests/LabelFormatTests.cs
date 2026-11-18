using SptPrometheusExporter.Metrics;

namespace SptPrometheusExporter.Tests;

public class LabelFormatTests
{
    [Fact]
    public void Build_WithNoLabels_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, LabelFormat.Build());
        Assert.Empty(LabelFormat.None);
    }

    [Fact]
    public void Build_SingleLabel_QuotesValue()
    {
        Assert.Equal("level=\"error\"", LabelFormat.Build(("level", "error")));
    }

    [Fact]
    public void Build_MultipleLabels_KeepsArgumentOrderAndSeparatesWithCommas()
    {
        var labels = LabelFormat.Build(("method", "GET"), ("status_code", "200"), ("route", "/client/game/start"));

        Assert.Equal("method=\"GET\",status_code=\"200\",route=\"/client/game/start\"", labels);
    }

    [Theory]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("say \"hi\"", "say \\\"hi\\\"")]
    [InlineData("line1\nline2", "line1\\nline2")]
    [InlineData("\\\"\n", "\\\\\\\"\\n")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Build_EscapesBackslashQuoteAndNewline(string rawValue, string expectedEscaped)
    {
        Assert.Equal($"name=\"{expectedEscaped}\"", LabelFormat.Build(("name", rawValue)));
    }

    [Fact]
    public void Build_EscapesBackslashBeforeQuote_SoEscapesAreNotDoubled()
    {
        // A quote must become \" and not \\" (which would happen if backslashes were escaped after quotes).
        Assert.Equal("name=\"a\\\"b\"", LabelFormat.Build(("name", "a\"b")));
    }

    [Fact]
    public void Build_DoesNotEscapeOtherCharacters()
    {
        Assert.Equal("username=\"Żółw ünïcode\ttab\"", LabelFormat.Build(("username", "Żółw ünïcode\ttab")));
    }
}
