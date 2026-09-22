using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;

namespace NexusMods.Networking.HttpDownloader.Tests;

public class HttpDownloadJobWorkerTests
{
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IServiceProvider _serviceProvider;

    public HttpDownloadJobWorkerTests(IServiceProvider serviceProvider)
    {
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _serviceProvider = serviceProvider;
    }

    [Fact]
    public async Task Test_StalledDownloadFailsInsteadOfHangingForever()
    {
        var server = _serviceProvider.GetRequiredService<LocalHttpServer>();
        var url = new Uri(server.Uri, "/stall");

        var previousTimeout = HttpDownloadJob.StallTimeout;
        HttpDownloadJob.StallTimeout = TimeSpan.FromSeconds(1);
        try
        {
            await using var outputPath = _temporaryFileManager.CreateFile();
            var act = async () => await HttpDownloadJob.Create(_serviceProvider, url, url, outputPath.Path);

            // Polly retries the stall 3 times (resuming via range requests), then the job must fail visibly.
            var exception = (await act.Should().ThrowAsync<Exception>()).Which;
            exception.ToString().Should().Contain("stalled");
        }
        finally
        {
            HttpDownloadJob.StallTimeout = previousTimeout;
        }
    }

    [Fact]
    [Trait("RequiresNetworking", "True")]
    public async Task Test_NexusModsCDN100MFile()
    {
        const string url = "https://paris.nexus-cdn.com/100M";

        await using var outputPath = _temporaryFileManager.CreateFile();
        _ = await HttpDownloadJob.Create(_serviceProvider, new Uri(url), new Uri(url), outputPath.Path);;

        outputPath.Path.FileExists.Should().BeTrue();
        outputPath.Path.FileInfo.Size.Should().Be(Size.MB * 100);

        var hash = await outputPath.Path.XxHash3Async();
        hash.Should().Be(Hash.From(0x2330001AD4114867));
    }
}
