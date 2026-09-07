using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace NinjaPricer
{
    internal static class LeagueProvider
    {
        private const int MaxResponseBytes = 512 * 1024;
        private const int MaxLeagues = 100;
        private const int MaxLeagueNameLength = 128;
        private static HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "NinjaPricer-GameHelper-Plugin");
            return client;
        }
        private static readonly object Gate = new();
        private static readonly List<string> leagues = new();
        private static bool isLoading;
        private static bool hasLoaded;
        private static long generation;
        private static CancellationTokenSource? cancellation;

        public static IReadOnlyList<string> Leagues
        {
            get { lock (Gate) return leagues.ToArray(); }
        }

        public static bool IsLoading
        {
            get { lock (Gate) return isLoading; }
        }

        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (hasLoaded || isLoading) return;
                isLoading = true;
            }

            StartLoad();
        }

        public static void ForceReload()
        {
            lock (Gate)
            {
                hasLoaded = false;
                leagues.Clear();
                if (isLoading) return;
                isLoading = true;
            }

            StartLoad();
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                generation++;
                cancellation?.Cancel();
                cancellation?.Dispose();
                cancellation = null;
                isLoading = false;
                hasLoaded = false;
            }
        }

        private static void StartLoad()
        {
            CancellationToken token;
            long current;
            lock (Gate)
            {
                cancellation?.Dispose();
                cancellation = new CancellationTokenSource();
                token = cancellation.Token;
                current = ++generation;
            }
            Task.Run(() => LoadAsync(current, token));
        }

        internal static void SetLeaguesForTests(IEnumerable<string> values)
        {
            lock (Gate)
            {
                leagues.Clear();
                leagues.AddRange(values);
            }
        }

        private static async Task LoadAsync(long current, CancellationToken token)
        {
            var fetched = new List<string>();
            try
            {
                var json = await BoundedHttp.GetStringAsync(Http, "https://poe2scout.com/api/poe2/Leagues", MaxResponseBytes, token).ConfigureAwait(false);
                var arr = ScoutLeaguePayload.ParseLeagueArray(json, MaxLeagues);
                if (arr.Count > 0)
                {
                    foreach (var league in arr)
                    {
                        var name = league["Value"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name) && name.Length <= MaxLeagueNameLength)
                            fetched.Add(name);
                    }
                }
            }
            catch { }

            lock (Gate)
            {
                if (current != generation || token.IsCancellationRequested) return;
                leagues.Clear();
                if (fetched.Count > 0)
                    leagues.AddRange(fetched);
                hasLoaded = true;
                isLoading = false;
            }
        }
    }
}
