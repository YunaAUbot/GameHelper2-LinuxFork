using System.Net;
using System.Net.Http;
using System.Text;
using NinjaPricer;

namespace NinjaPricer.Tests;

public sealed class FetchSafetyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "NinjaPricerTests-" + Guid.NewGuid());

    public FetchSafetyTests() => Directory.CreateDirectory(this.directory);

    public void Dispose()
    {
        PriceFetcher.Shutdown();
        LeagueProvider.Shutdown();
        Directory.Delete(this.directory, true);
    }

    [Fact]
    public async Task BoundedReaderRejectsDeclaredAndStreamedOversizeResponses()
    {
        var declared = new StubHandler((_, _) => Response(new string('x', PriceFetcher.MaxResponseBytes + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PriceFetcher.ReadBoundedJsonForTests(declared, CancellationToken.None));

        var streamed = new StubHandler((_, _) => Response(new string('x', PriceFetcher.MaxResponseBytes + 1), omitLength: true));
        await Assert.ThrowsAsync<InvalidDataException>(() => PriceFetcher.ReadBoundedJsonForTests(streamed, CancellationToken.None));
    }

    [Fact]
    public async Task ServerPagesAreRejectedAboveCap()
    {
        var handler = new StubHandler((request, _) =>
            Response(request.RequestUri!.Query.Contains("Page=1")
                ? "{\"Pages\":999999,\"Items\":[]}"
                : "{\"Pages\":1,\"Items\":[]}"));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test League", 5);

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.DoesNotContain(handler.Requests, u => u.Contains("&Page=2", StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedCacheIsRejectedWithoutDeletion()
    {
        var path = Path.Combine(this.directory, "price_cache.json");
        File.WriteAllBytes(path, new byte[PriceFetcher.MaxCacheBytes + 1]);
        PriceFetcher.ResetForTests(new StubHandler((_, _) => Response("{}")));
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test League", 5);

        Assert.False(PriceFetcher.TryLoadCacheForTests(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CancelledGenerationCannotPublishOrWrite()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Response(NinjaExchange("stale", 9));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Old", 5);
        var fetch = PriceFetcher.RunFetchForTests(this.directory);
        PriceFetcher.Shutdown();
        release.TrySetResult();

        Assert.False(await fetch);
        Assert.False(PriceFetcher.HasPriceDataForName("stale"));
        Assert.False(File.Exists(Path.Combine(this.directory, "price_cache.json")));
        Assert.Equal(DateTime.MinValue, PriceFetcher.LastFetchUtc);
        Assert.Equal(0, PriceFetcher.ConsecutiveFailureCount);
        Assert.Equal(DateTime.MinValue, PriceFetcher.NextRetryUtc);
    }

    [Fact]
    public async Task MidFetchConfigurationQueuesExactlyOneCleanFollowUp()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, token) =>
        {
            if (request.RequestUri!.Query.Contains("league=Old"))
            {
                firstStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            else if (request.RequestUri.Query.Contains("league=New"))
            {
                followUpStarted.TrySetResult();
                await releaseFollowUp.Task.WaitAsync(token);
            }
            return Response(NinjaExchange(request.RequestUri.Query.Contains("league=New") ? "new" : "old", 2));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Old", 5);
        var fetch = PriceFetcher.RunFetchForTests(this.directory);
        await firstStarted.Task;
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "New", 6);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "New", 7);
        release.TrySetResult();
        await fetch;
        await followUpStarted.Task;
        Assert.False(PriceFetcher.HasPriceDataForName("old"));
        Assert.False(File.Exists(Path.Combine(this.directory, "price_cache.json")));
        releaseFollowUp.TrySetResult();
        await PriceFetcher.WaitForIdleForTests();

        Assert.Contains(handler.Requests, u => u.Contains("league=Old"));
        Assert.Contains(handler.Requests, u => u.Contains("league=New"));
        var phases = handler.Requests
            .Select(u => u.Contains("league=New") ? "New" : "Old")
            .Aggregate(new List<string>(), (seen, league) =>
            {
                if (seen.Count == 0 || seen[^1] != league) seen.Add(league);
                return seen;
            });
        Assert.Equal(new[] { "Old", "New" }, phases);
        Assert.True(PriceFetcher.HasPriceDataForName("new"));
    }

    [Fact]
    public async Task IdleLeagueChangeStartsImmediateRefreshAndHidesOldLeaguePrices()
    {
        var followUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (request, token) =>
        {
            var isNew = request.RequestUri!.Query.Contains("league=New");
            if (isNew)
            {
                followUpStarted.TrySetResult();
                await releaseFollowUp.Task.WaitAsync(token);
            }
            return Response(NinjaExchange(isNew ? "new" : "old", 2));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Old", 5);
        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.True(PriceFetcher.HasPriceDataForName("old"));

        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "New", 5);
        await followUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(PriceFetcher.HasPriceDataForName("old"));
        releaseFollowUp.TrySetResult();
        await PriceFetcher.WaitForIdleForTests();

        Assert.True(PriceFetcher.HasPriceDataForName("new"));
    }

    [Fact]
    public async Task MalformedSuccessfulResponsePreservesKnownGoodStateAndCache()
    {
        var malformed = false;
        var handler = new StubHandler((request, _) =>
        {
            if (malformed && request.RequestUri!.Query.Contains("UniqueAccessories"))
                return Response("{}");
            return Response(NinjaExchange("known", 3));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);
        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));
        var timestamp = PriceFetcher.LastFetchUtc;
        var cache = File.ReadAllBytes(Path.Combine(this.directory, "price_cache.json"));
        malformed = true;

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.True(PriceFetcher.HasPriceDataForName("known"));
        Assert.Equal(timestamp, PriceFetcher.LastFetchUtc);
        Assert.Equal(cache, File.ReadAllBytes(Path.Combine(this.directory, "price_cache.json")));
    }

    [Fact]
    public async Task FailedPartialFetchPreservesKnownGoodStateAndCache()
    {
        var fail = false;
        var handler = new StubHandler((request, _) =>
        {
            if (fail && request.RequestUri!.Query.Contains("UniqueAccessories"))
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            return Response(NinjaExchange("known", 3));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);
        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));
        var timestamp = PriceFetcher.LastFetchUtc;
        var cache = File.ReadAllBytes(Path.Combine(this.directory, "price_cache.json"));
        fail = true;

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.True(PriceFetcher.HasPriceDataForName("known"));
        Assert.Equal(timestamp, PriceFetcher.LastFetchUtc);
        Assert.Equal(cache, File.ReadAllBytes(Path.Combine(this.directory, "price_cache.json")));
    }

    [Fact]
    public async Task FailedFetchDoesNotRetryOnTheNextRenderFrame()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test", 5);

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        var requestCount = handler.Requests.Count;

        PriceFetcher.RefreshIfNeeded();

        Assert.False(PriceFetcher.IsFetching);
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task ForcedIdentityRefreshDoesNotQueueADuplicateActiveFetch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response(NinjaExchange("known", 3));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);
        var fetch = PriceFetcher.RunFetchForTests(this.directory);
        await started.Task;

        PriceFetcher.ForceRefresh(this.directory, ignoreCooldown: true);
        release.TrySetResult();
        Assert.True(await fetch);
        await PriceFetcher.WaitForIdleForTests();

        Assert.Equal(handler.Requests.Count, handler.Requests.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RetryDelayIsExponentialBoundedAndStaggeredByProviderIdentity()
    {
        var first = PriceFetcher.CalculateRetryDelayForTests(1, PriceFetcher.SourcePoe2Scout, "Runes of Aldur", 5);
        var second = PriceFetcher.CalculateRetryDelayForTests(2, PriceFetcher.SourcePoe2Scout, "Runes of Aldur", 5);
        var third = PriceFetcher.CalculateRetryDelayForTests(3, PriceFetcher.SourcePoe2Scout, "Runes of Aldur", 5);
        var otherProvider = PriceFetcher.CalculateRetryDelayForTests(1, PriceFetcher.SourcePoeNinja, "Runes of Aldur", 5);
        var capped = PriceFetcher.CalculateRetryDelayForTests(20, PriceFetcher.SourcePoe2Scout, "Runes of Aldur", 5);

        Assert.InRange(first, TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(36));
        Assert.True(second > first);
        Assert.True(third > second);
        Assert.NotEqual(first, otherProvider);
        Assert.InRange(capped, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task FailureHealthEscalatesAtThreeAndSuccessfulFetchResetsIt()
    {
        var fail = true;
        var handler = new StubHandler((_, _) => fail
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Response(NinjaExchange("known", 3)));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
            Assert.Equal(attempt, PriceFetcher.ConsecutiveFailureCount);
            Assert.True(PriceFetcher.NextRetryUtc > DateTime.UtcNow);
            Assert.Equal(attempt >= 3, PriceFetcher.ShouldShowFailureWarning);
        }

        var warning = PriceFetcher.GetFailureWarningText(DateTime.UtcNow);
        Assert.Contains("3 consecutive", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next retry", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HttpRequestException", warning, StringComparison.Ordinal);

        fail = false;
        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));

        Assert.Equal(0, PriceFetcher.ConsecutiveFailureCount);
        Assert.Equal(DateTime.MinValue, PriceFetcher.NextRetryUtc);
        Assert.False(PriceFetcher.ShouldShowFailureWarning);
        Assert.Equal(string.Empty, PriceFetcher.LastFetchError);
    }

    [Fact]
    public async Task CacheWriteFailureUsesTheSameRetryBackoff()
    {
        Directory.CreateDirectory(Path.Combine(this.directory, "price_cache.json"));
        var handler = new StubHandler((_, _) => Response(NinjaExchange("known", 3)));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));

        Assert.Equal(1, PriceFetcher.ConsecutiveFailureCount);
        Assert.True(PriceFetcher.NextRetryUtc > DateTime.UtcNow);
        var requestCount = handler.Requests.Count;
        PriceFetcher.RefreshIfNeeded();
        Assert.False(PriceFetcher.IsFetching);
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task MalformedScoutScalarConversionUsesRetryBackoff()
    {
        var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/Leagues", StringComparison.Ordinal))
                return Response("[{\"Value\":\"Test\",\"ChaosDivinePrice\":1,\"DivinePrice\":2}]");
            return Response("{\"Pages\":1,\"Items\":[{\"CurrentPrice\":{}}]}");
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test", 5);

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.Equal(1, PriceFetcher.ConsecutiveFailureCount);
        Assert.True(PriceFetcher.NextRetryUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task ScoutRejectsZeroPagesAfterPaginationHasStarted()
    {
        var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/Leagues", StringComparison.Ordinal))
                return Response("[{\"Value\":\"Test\",\"ChaosDivinePrice\":25,\"DivinePrice\":50}]");
            if (uri.AbsolutePath.Contains("/Currencies/", StringComparison.Ordinal) &&
                QueryValue(uri.ToString(), "Page") == "2")
                return Response("{\"Pages\":0,\"Items\":[]}");
            if (uri.AbsolutePath.Contains("/Currencies/", StringComparison.Ordinal))
                return Response("{\"Pages\":2,\"Items\":[{\"CurrentPrice\":1,\"Text\":\"known\"}]}");
            return Response("{\"Pages\":0,\"Items\":[]}");
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test", 5);

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.False(PriceFetcher.HasPriceDataForName("known"));
        Assert.False(File.Exists(Path.Combine(this.directory, "price_cache.json")));
    }

    [Fact]
    public async Task ScoutAcceptsZeroPagesOnlyForAnEmptyCategory()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Leagues", StringComparison.Ordinal))
                return Response("[{\"Value\":\"Test\",\"ChaosDivinePrice\":25,\"DivinePrice\":50}]");
            if (path.Contains("/Uniques/", StringComparison.Ordinal))
                return Response("{\"Pages\":0,\"Items\":[]}");
            if (path.Contains("/Currencies/", StringComparison.Ordinal))
                return Response("{\"Pages\":1,\"Items\":[]}");
            return Response(NinjaStash("known", 1));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test", 5);

        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.True(PriceFetcher.HasPriceDataForName("known"));
    }

    [Fact]
    public async Task ScoutFetchCoversAllKnownProviderFamilies()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Leagues", StringComparison.Ordinal))
                return Response("[{\"Value\":\"Test\",\"ChaosDivinePrice\":25,\"DivinePrice\":50}]");
            if (path.Contains("/Currencies/", StringComparison.Ordinal) || path.Contains("/Uniques/", StringComparison.Ordinal))
                return Response("{\"Pages\":1,\"Items\":[]}");
            return Response(NinjaStash("known", 1));
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoe2Scout, "Test", 5);

        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));

        var currencyCategories = handler.Requests
            .Where(url => url.Contains("/Currencies/ByCategory", StringComparison.Ordinal))
            .Select(url => QueryValue(url, "Category"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(new[]
        {
            "currency", "ritual", "runes", "idol", "essences", "fragments", "abyss", "breach",
            "delirium", "expedition", "incursion", "ultimatum", "vaal", "vaultkeys", "verisium",
            "uncutgems", "lineagesupportgems",
        }, currencyCategories);

        var uniqueCategories = handler.Requests
            .Where(url => url.Contains("/Uniques/ByCategory", StringComparison.Ordinal))
            .Select(url => QueryValue(url, "Category"))
            .ToArray();
        Assert.Equal(new[] { "weapon", "armour", "accessory", "flask", "jewel", "map", "sanctum" }, uniqueCategories);
    }

    [Fact]
    public async Task NinjaFetchCoversAllProviderFamiliesAndSeedsCurrencyRatesFirst()
    {
        var handler = new StubHandler((request, _) => Response(
            request.RequestUri!.AbsolutePath.Contains("/stash/", StringComparison.Ordinal)
                ? NinjaStash("known", 1)
                : NinjaCurrencyExchange()));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);

        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));

        var exchangeTypes = handler.Requests
            .Where(url => url.Contains("/exchange/", StringComparison.Ordinal))
            .Select(url => QueryValue(url, "type"))
            .ToArray();
        Assert.Equal("Currency", exchangeTypes[0]);
        Assert.Equal(new[]
        {
            "Currency", "Ritual", "Runes", "Idols", "Verisium", "Essences", "Fragments", "Abyss",
            "Breach", "Delirium", "Expedition", "Incursion", "Ultimatum", "Vaal", "VaultKeys",
            "UncutGems", "LineageSupportGems", "SoulCores",
        }, exchangeTypes);

        var stashTypes = handler.Requests
            .Where(url => url.Contains("/stash/", StringComparison.Ordinal))
            .Select(url => QueryValue(url, "type"))
            .ToArray();
        Assert.Equal(new[]
        {
            "UniqueArmours", "UniqueAccessories", "UniqueCharms", "UniqueWeapons", "UniqueFlasks", "UniqueJewels",
        }, stashTypes);
    }

    [Fact]
    public async Task NinjaRatesUseExactCurrencyRowsAndDoNotCompoundAcrossCategories()
    {
        var payload = NinjaCurrencyExchange();
        var handler = new StubHandler((_, _) => Response(payload));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);

        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));

        Assert.Equal(0.5, PriceFetcher.GetChaosPerExalted(), 6);
        Assert.Equal(25, PriceFetcher.GetChaosPerDivine(), 6);
        Assert.DoesNotContain(PriceFetcher.GetChaosPerExalted(), new[] { 8.0, 40.0 });
    }

    [Theory]
    [InlineData(PriceFetcher.SourcePoeNinja)]
    [InlineData(PriceFetcher.SourcePoe2Scout)]
    public async Task BothSourcesExposeNormalAndRunemasteredUniquePrices(int source)
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Leagues", StringComparison.Ordinal))
                return Response("[{\"Value\":\"Test\",\"ChaosDivinePrice\":25,\"DivinePrice\":50}]");
            if (path.Contains("/Currencies/", StringComparison.Ordinal))
                return Response("{\"Pages\":1,\"Items\":[]}");
            if (path.Contains("/Uniques/", StringComparison.Ordinal))
                return Response("{\"Pages\":0,\"Items\":[]}");
            if (path.Contains("/stash/", StringComparison.Ordinal))
                return Response(NinjaVariantStash());
            return Response(NinjaCurrencyExchange());
        });
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(source, "Test", 5);

        Assert.True(await PriceFetcher.RunFetchForTests(this.directory));
        var normal = PriceFetcher.GetPrice("Aurseize", scoutText: "Aurseize");
        var runemastered = PriceFetcher.GetPrice("Aurseize", scoutText: "Aurseize Runemastered");

        Assert.NotNull(normal);
        Assert.NotNull(runemastered);
        Assert.Equal(0.7, normal.PriceChaos, 6);
        Assert.Equal(5.2, runemastered.PriceChaos, 6);
    }

    [Fact]
    public async Task UnexpectedWorkerExceptionUsesRetryBackoffInsteadOfFaultingTask()
    {
        var handler = new StubHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new NotSupportedException("unexpected payload failure")));
        PriceFetcher.ResetForTests(handler);
        PriceFetcher.Configure(PriceFetcher.SourcePoeNinja, "Test", 5);

        Assert.False(await PriceFetcher.RunFetchForTests(this.directory));
        Assert.Equal(1, PriceFetcher.ConsecutiveFailureCount);
        Assert.Contains("NotSupportedException", PriceFetcher.LastFetchError, StringComparison.Ordinal);
        Assert.True(PriceFetcher.NextRetryUtc > DateTime.UtcNow);
    }

    private static string QueryValue(string url, string key)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return string.Empty;
    }

    private static string NinjaVariantStash() =>
        "{\"core\":{\"primary\":\"exalted\"},\"lines\":[" +
        "{\"name\":\"Aurseize\",\"baseType\":\"Layered Gauntlets\",\"primaryValue\":1.4}," +
        "{\"name\":\"Aurseize\",\"baseType\":\"Runemastered Layered Gauntlets\",\"primaryValue\":10.4}]}";

    private static string NinjaStash(string name, double price) =>
        $"{{\"core\":{{\"primary\":\"exalted\"}},\"lines\":[{{\"name\":\"{name}\",\"baseType\":\"\",\"primaryValue\":{price}}}]}}";

    private static string NinjaCurrencyExchange() =>
        "{\"core\":{\"primary\":\"exalted\",\"rates\":{\"divine\":0.02,\"chaos\":0.5}}," +
        "\"items\":[" +
        "{\"id\":\"ex\",\"name\":\"Exalted Orb\"}," +
        "{\"id\":\"chaos\",\"name\":\"Chaos Orb\"}," +
        "{\"id\":\"div\",\"name\":\"Divine Orb\"}," +
        "{\"id\":\"perfect\",\"name\":\"Perfect Exalted Orb\"}," +
        "{\"id\":\"greater\",\"name\":\"Greater Exalted Orb\"}]," +
        "\"lines\":[" +
        "{\"id\":\"ex\",\"name\":\"Exalted Orb\",\"primaryValue\":1}," +
        "{\"id\":\"chaos\",\"name\":\"Chaos Orb\",\"primaryValue\":2}," +
        "{\"id\":\"div\",\"name\":\"Divine Orb\",\"primaryValue\":50}," +
        "{\"id\":\"perfect\",\"name\":\"Perfect Exalted Orb\",\"primaryValue\":8}," +
        "{\"id\":\"greater\",\"name\":\"Greater Exalted Orb\",\"primaryValue\":5}]}";

    private static string NinjaExchange(string name, double price) =>
        $"{{\"core\":{{\"primary\":\"exalted\"}},\"items\":[{{\"id\":\"1\",\"name\":\"{name}\"}}],\"lines\":[{{\"id\":\"1\",\"name\":\"{name}\",\"primaryValue\":{price}}}]}}";

    private static HttpResponseMessage Response(string text, bool omitLength = false)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        if (omitLength) content.Headers.ContentLength = null;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
            => this.response = (r, c) => Task.FromResult(response(r, c));
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => this.response = response;
        public List<string> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!.ToString());
            return await this.response(request, cancellationToken);
        }
    }
}
