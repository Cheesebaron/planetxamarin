﻿using Firehose.Web.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Polly;
using Polly.Caching.Memory;
using Polly.Wrap;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml;

namespace Firehose.Web.Infrastructure
{
    public class NewCombinedFeedSource
    {
        private HttpClient httpClient;
        private AsyncPolicyWrap policy;

        public IEnumerable<IAmACommunityMember> Tamarins { get; }

        public NewCombinedFeedSource(IEnumerable<IAmACommunityMember> tamarins)
        {
            EnsureHttpClient();

            Tamarins = tamarins;

			// cache in memory for an hour
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var memoryCacheProvider = new MemoryCacheProvider(memoryCache);
            var cachePolicy = Policy.CacheAsync(memoryCacheProvider, TimeSpan.FromHours(1));

			// retry policy with max 2 retries, delay by x*x^1.2 where x is retry attempt
			// this will ensure we don't retry too quickly
            var retryPolicy = Policy.Handle<FeedReadFailedException>()
                .WaitAndRetryAsync(2, retry => TimeSpan.FromSeconds(retry * Math.Pow(1.2, retry)));

            policy = Policy.WrapAsync(cachePolicy, retryPolicy);
        }
        
        private void EnsureHttpClient()
        {
            if (httpClient == null)
            {
                httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("PlanetXamarin", $"{GetType().Assembly.GetName().Version}"));
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            }
        }

        public async Task<SyndicationFeed> LoadFeed(int? numberOfItems, string languageCode = "mixed")
        {
            IEnumerable<IAmACommunityMember> tamarins;
            if (languageCode == null || languageCode == "mixed") // use all tamarins
            {
                tamarins = Tamarins;
            }
            else
            {
                tamarins = Tamarins.Where(t => t.FeedLanguageCode == languageCode);
            }

            var feedTasks = tamarins.SelectMany(t => TryReadFeeds(t, GetFilterFunction(t)));

            var syndicationItems = await Task.WhenAll(feedTasks).ConfigureAwait(false);
            var combinedFeed = GetCombinedFeed(syndicationItems.SelectMany(f => f), languageCode, tamarins, numberOfItems);
            return combinedFeed;
        }

        private IEnumerable<Task<IEnumerable<SyndicationItem>>> TryReadFeeds(IAmACommunityMember tamarin, Func<SyndicationItem, bool> filter)
        {
            return tamarin.FeedUris.Select(uri => TryReadFeed(tamarin, uri.AbsoluteUri, filter));
        }

        private async Task<IEnumerable<SyndicationItem>> TryReadFeed(IAmACommunityMember tamarin, string feedUri, Func<SyndicationItem, bool> filter)
        {
            try
            {
                return await policy.ExecuteAsync(context => ReadFeed(feedUri, filter), new Context(feedUri)).ConfigureAwait(false);
            }
            catch (FeedReadFailedException ex)
            {
                Logger.Error(ex, $"{tamarin.FirstName} {tamarin.LastName}'s feed of {ex.Data["FeedUri"]} failed to load.");
            }

            return new SyndicationItem[0];
        }

        private async Task<IEnumerable<SyndicationItem>> ReadFeed(string feedUri, Func<SyndicationItem, bool> filter)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync(feedUri).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using (var feedStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = XmlReader.Create(feedStream))
                    {
                        var feed = SyndicationFeed.Load(reader);
                        var filteredItems = feed.Items
                            .Where(item => TryFilter(item, filter));

                        return filteredItems;
                    }
                }
            }
            catch (HttpRequestException hex)
            {
                throw new FeedReadFailedException("Loading remote syndication feed failed", hex)
                    .WithData("FeedUri", feedUri);
            }
            catch (WebException ex)
            {
                throw new FeedReadFailedException("Loading remote syndication feed timed out", ex)
                    .WithData("FeedUri", feedUri);
            }
            catch (XmlException ex)
            {
                throw new FeedReadFailedException("Failed parsing remote syndication feed", ex)
                    .WithData("FeedUri", feedUri);
            }
            catch (TaskCanceledException ex)
            {
                throw new FeedReadFailedException("Reading feed timed out", ex)
                    .WithData("FeedUri", feedUri);
            }

            throw new FeedReadFailedException("Loading remote syndication feed failed.")
                .WithData("FeedUri", feedUri)
                .WithData("HttpStatusCode", (int)response.StatusCode);
        }

        private SyndicationFeed GetCombinedFeed(IEnumerable<SyndicationItem> items, string languageCode, 
            IEnumerable<IAmACommunityMember> tamarins, int? numberOfItems)
        {
            var beforeNowItems = items
                .Where(item =>
                    item.LastUpdatedTime.UtcDateTime <= DateTimeOffset.UtcNow &&
                    item.PublishDate.UtcDateTime <= DateTimeOffset.UtcNow);

            if (numberOfItems.HasValue)
            {
                beforeNowItems = beforeNowItems.Take(numberOfItems.Value);
            }

            var orderedItems = beforeNowItems.OrderByDescending(item => item.PublishDate);

            var feed = new SyndicationFeed(
                ConfigurationManager.AppSettings["RssFeedTitle"],
                ConfigurationManager.AppSettings["RssFeedDescription"],
                new Uri(ConfigurationManager.AppSettings["BaseUrl"]),
                orderedItems)
            {
                ImageUrl = new Uri(ConfigurationManager.AppSettings["RssFeedImageUrl"]),
                Copyright = new TextSyndicationContent("The copyright for each post is retained by its author."),
                Language = languageCode
            };

            foreach(var tamarin in tamarins)
            {
                feed.Contributors.Add(new SyndicationPerson(
                    tamarin.EmailAddress, $"{tamarin.FirstName} {tamarin.LastName}", tamarin.WebSite.ToString()));
            }

            return feed;
        }

        private static Func<SyndicationItem, bool> GetFilterFunction(IAmACommunityMember tamarin)
        {
            if (tamarin is IFilterMyBlogPosts filterMyBlogPosts)
            {
                return filterMyBlogPosts.Filter;
            }

            return null;
        }

        private static bool TryFilter(SyndicationItem item, Func<SyndicationItem, bool> filterFunc)
        {
            try
            {
                if (filterFunc != null)
                    return filterFunc(item);
            }
            catch (Exception)
            {
            }

            // the authors' filter is derped or has no filter
            // try some sane defaults
            return item.ApplyDefaultFilter();
        }
    }

    public class FeedReadFailedException : Exception
    {
        public FeedReadFailedException(string message) 
            : base(message)
        {
        }

        public FeedReadFailedException(string message, Exception inner) 
            : base(message, inner)
        {
        }
    }
}