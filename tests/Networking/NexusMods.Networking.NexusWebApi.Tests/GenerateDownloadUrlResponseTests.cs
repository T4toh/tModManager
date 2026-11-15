using FluentAssertions;
using NexusMods.Abstractions.NexusWebApi;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class GenerateDownloadUrlResponseTests
{
    [Fact]
    public void ObjectWithUrl_ReturnsTheUrl()
    {
        NexusApiClient.ParseGenerateDownloadUrlResponse("""{"url":"https://files.nexus-cdn.com/x.zip"}""")
            .Should().Be(new Uri("https://files.nexus-cdn.com/x.zip"));
    }

    [Fact]
    public void MirrorList_ReturnsTheFirstUri()
    {
        NexusApiClient.ParseGenerateDownloadUrlResponse("""[{"name":"CDN","URI":"https://cdn/a.zip"},{"name":"B","URI":"https://b/a.zip"}]""")
            .Should().Be(new Uri("https://cdn/a.zip"));
    }

    [Fact]
    public void EmptyList_MeansNoSession()
    {
        // Nexus answers [] to an anonymous session: retrying the next file can't work either
        var act = () => NexusApiClient.ParseGenerateDownloadUrlResponse("[]");
        act.Should().Throw<NexusWebsiteSessionRequiredException>()
            .Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public void ChallengePage_IsTransient()
    {
        NexusApiClient.ParseGenerateDownloadUrlResponse("<!DOCTYPE html><html><head><title>Just a moment...</title>")
            .Should().BeNull();
    }
}
