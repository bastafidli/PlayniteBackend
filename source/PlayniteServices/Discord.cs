﻿using ComposableAsync;
using MongoDB.Driver;
using Playnite;
using Playnite.Common;
using Playnite.SDK;
using PlayniteServices.Databases;
using PlayniteServices.Models.Discord;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;

namespace PlayniteServices
{
    public class Discord : IDisposable
    {
        private readonly static ILogger logger = LogManager.GetLogger();
        private const string apiBaseUrl = @"https://discord.com/api/v9/";
        private readonly UpdatableAppSettings settings;
        private readonly Addons addons;
        private readonly Database db;
        private readonly HttpClient httpClient;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpRequestMessage>> messageQueues = new ConcurrentDictionary<string, ConcurrentQueue<HttpRequestMessage>>();
        private readonly ConcurrentDictionary<string, RateLimitHeaders> rateLimits = new ConcurrentDictionary<string, RateLimitHeaders>();

        private string addonsFeedChannel;

        public static Discord Instance { get; set; }

        public Discord(UpdatableAppSettings settings, Addons addons, Database db)
        {
            this.settings = settings;
            this.addons = addons;
            this.db = db;
            httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Accept", MediaTypeNames.Application.Json);
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bot {settings.Settings.Discord.BotToken}");
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PlayniteBot/1.0");
            httpClient.Timeout = new TimeSpan(0, 0, 20);
            Init().Wait();
            addons.InstallerManifestsUpdated += Addons_InstallerManifestsUpdated;
        }

        public async Task Init()
        {
            try
            {
                var guilds = await Get<List<Guild>>(@"users/@me/guilds");
                var channels = await Get<List<Channel>>($"guilds/{guilds[0].id}/channels");
                addonsFeedChannel = channels.First(a => a.name == "addons-feed").id;
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to init Discord bot.");
            }
        }

        public void Dispose()
        {
            addons.InstallerManifestsUpdated -= Addons_InstallerManifestsUpdated;
            httpClient.Dispose();
        }

        private static string AddonTypeToFriendlyString(AddonType type)
        {
            switch (type)
            {
                case AddonType.GameLibrary:
                    return "library plugin";
                case AddonType.MetadataProvider:
                    return "metadata plugin";
                case AddonType.Generic:
                    return "extension";
                case AddonType.ThemeDesktop:
                    return "desktop theme";
                case AddonType.ThemeFullscreen:
                    return "fullscreen theme";
                default:
                    return "uknown";
            }
        }

        private async Task SendNewAddonNotif(AddonManifestBase addon)
        {
            var embed = new EmbedObject
            {
                author = new EmbedAuthor { name = addon.Author },
                description = addon.Description,
                thumbnail = addon.IconUrl.IsNullOrEmpty() ? null : new EmbedThumbnail { url = addon.IconUrl },
                title = $"{addon.Name} {AddonTypeToFriendlyString(addon.Type)} has been released",
                url = "https://playnite.link/addons.html#{0}".Format(Uri.EscapeDataString(addon.AddonId)),
                image = addon.Screenshots.HasItems() ? new EmbedImage { url = addon.Screenshots[0].Image } : null,
                color = 0x19d900
            };

            await SendMessage(addonsFeedChannel, string.Empty, new List<EmbedObject> { embed });
        }

        private async Task SendAddonUpdateNotif(AddonManifestBase addon, AddonInstallerPackage package)
        {
            var embed = new EmbedObject
            {
                author = new EmbedAuthor { name = addon.Author },
                description = $"Updated to {package.Version}:\n\n" + string.Join("\n", package.Changelog.Select(a => $"- {a}")),
                thumbnail = addon.IconUrl.IsNullOrEmpty() ? null : new EmbedThumbnail { url = addon.IconUrl },
                url = "https://playnite.link/addons.html#{0}".Format(Uri.EscapeDataString(addon.AddonId)),
                title = addon.Name,
                color = 0xbf0086
            };

            await SendMessage(addonsFeedChannel, string.Empty, new List<EmbedObject> { embed });
        }

        private async Task ProcessAddonUpdates(bool sendNotifications)
        {
            foreach (var addon in db.Addons.AsQueryable())
            {
                var installer = db.AddonInstallers.AsQueryable().FirstOrDefault(a => a.AddonId == addon.AddonId);
                if (installer == null)
                {
                    continue;
                }

                var latestPackage = installer.Packages?.OrderByDescending(a => a.Version).FirstOrDefault();
                if (latestPackage == null)
                {
                    logger.Error($"Addon {addon.AddonId} has no install packages.");
                    continue;
                }

                var lastNotif = db.DiscordAddonNotifications.AsQueryable().FirstOrDefault(a => a.AddonId == addon.AddonId);
                if (lastNotif == null)
                {
                    if (sendNotifications)
                    {
                        await SendNewAddonNotif(addon);
                    }
                }
                else
                {
                    if (lastNotif.NotifyVersion == latestPackage.Version)
                    {
                        continue;
                    }

                    if (sendNotifications)
                    {
                        await SendAddonUpdateNotif(addon, latestPackage);
                    }
                }

                db.DiscordAddonNotifications.ReplaceOne(
                    a => a.AddonId == addon.AddonId,
                    new AddonUpdateNotification
                    {
                        AddonId = addon.AddonId,
                        Date = DateTimeOffset.Now,
                        NotifyVersion = latestPackage.Version
                    },
                    Database.ItemUpsertOptions);
            }
        }

        private async void Addons_InstallerManifestsUpdated(object sender, EventArgs e)
        {
            if (addonsFeedChannel.IsNullOrEmpty())
            {
                return;
            }

            try
            {
                await ProcessAddonUpdates(true);
            }
            catch (Exception exc)
            {
                logger.Error(exc, "Failed to process addon updates.");
            }
        }

        private async Task<Message> SendMessage(string channelId, string message, List<EmbedObject> embeds = null)
        {
            return await PostJson<Message>($"channels/{channelId}/messages", new MessageCreate
            {
                content = message,
                embeds = embeds
            });
        }

        private async Task<T> SendRequest<T>(HttpRequestMessage message) where T : class
        {
            var route = message.RequestUri.OriginalString.Substring(apiBaseUrl.Length);
            route = route.Substring(0, route.IndexOf('/'));

            var messageQueue = messageQueues.GetOrAdd(route, new ConcurrentQueue<HttpRequestMessage>());
            messageQueue.Enqueue(message);
            while (messageQueue.TryPeek(out var currentMessage) && currentMessage != message)
            {
                await Task.Delay(100);
            }

            var routeLimits = rateLimits.GetOrAdd(route, new RateLimitHeaders());
            if (routeLimits.Remaining <= 0)
            {
                logger.Warn($"Exhausted Discord rate limit on '{route}', waiting {routeLimits.ResetAfter}");
                await Task.Delay(routeLimits.ResetAfter);
            }

            var resp = await httpClient.SendAsync(message);
            var cnt = await resp.Content.ReadAsStringAsync();

            var limitHeaders = new RateLimitHeaders(resp.Headers);
            rateLimits.AddOrUpdate(route, limitHeaders, (_, __) => limitHeaders);
            messageQueue.TryDequeue(out var _);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var limitResponse = Serialization.FromJson<RateLimitResponse>(cnt);
                logger.Warn($"Discord rate limit on '{route}' route, {limitResponse.global}, {limitResponse.retry_after}");
                await Task.Delay(TimeSpan.FromSeconds(limitResponse.retry_after + 0.1));
                return await SendRequest<T>(message);
            }
            else if (resp.StatusCode != HttpStatusCode.OK)
            {
                logger.Error(cnt);
                var error = Serialization.FromJson<Error>(cnt);
                throw new Exception($"Discord: {error.code}, {error.message}");
            }
            else
            {
                return Serialization.FromJson<T>(cnt);
            }
        }

        private async Task<T> Get<T>(string url) where T : class
        {
            var request = new HttpRequestMessage(HttpMethod.Get, apiBaseUrl + url);
            return await SendRequest<T>(request);
        }

        private async Task<T> PostJson<T>(string url, object content) where T : class
        {
            var request = new HttpRequestMessage(HttpMethod.Post, apiBaseUrl + url)
            {
                Content = new StringContent(Serialization.ToJson(content), Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            return await SendRequest<T>(request);
        }
    }
}
