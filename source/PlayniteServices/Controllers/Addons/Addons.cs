﻿using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using Playnite;
using Playnite.Common;
using Playnite.SDK;
using PlayniteServices.Controllers.Webhooks;
using PlayniteServices.Databases;
using PlayniteServices.Filters;
using PlayniteServices.Models.GitHub;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace PlayniteServices.Controllers.Addons
{
    public class AddonRequest
    {
        public string AddonId { get; set; }
        public string SearchTerm { get; set; }
        public AddonType? Type { get; set; }
    }

    [Route("addons")]
    public class AddonsController : Controller
    {
        private readonly static ILogger logger = LogManager.GetLogger();
        private UpdatableAppSettings settings;

        public AddonsController(UpdatableAppSettings settings)
        {
            this.settings = settings;
        }

        [HttpGet("blacklist")]
        public ServicesResponse<string[]> GetBlackList()
        {
            return new ServicesResponse<string[]>(settings.Settings.Addons.Blacklist);
        }

        [HttpGet()]
        public ServicesResponse<List<AddonManifestBase>> GetAddons([FromQuery]AddonRequest request)
        {
            var col = Database.Instance.Addons;
            List<AddonManifestBase> addons = new List<AddonManifestBase>();
            if (!request.AddonId.IsNullOrEmpty())
            {
                var filter = Builders<AddonManifestBase>.Filter.Eq(u => u.AddonId, request.AddonId);
                addons = col.Find(filter).ToList();
            }
            else
            {
                // TODO convert to proper query
                foreach (var addon in col.Find(new BsonDocument()).ToCursor().ToEnumerable())
                {
                    if (request.Type != null && addon.Type != request.Type)
                    {
                        continue;
                    }

                    if (!request.SearchTerm.IsNullOrEmpty())
                    {
                        if (!(addon.Name.Contains(request.SearchTerm, StringComparison.OrdinalIgnoreCase) ||
                              addon.Tags.ContainsString(request.SearchTerm, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                    }

                    addons.Add(addon);
                }
            }

            return new ServicesResponse<List<AddonManifestBase>>(addons);
        }

        [ServiceFilter(typeof(ServiceKeyFilter))]
        [HttpPost("build")]
        public async Task<ActionResult> RegenerateDatabase()
        {
            await PlayniteServices.Addons.Instance.RegenerateAddonDatabase();
            return Ok();
        }

        [HttpPost("githubhook")]
        public async Task<ActionResult> GithubWebhook()
        {
            if (Request.Headers.TryGetValue("X-Hub-Signature", out var sig))
            {
                if (!Request.Headers.TryGetValue("X-GitHub-Event", out var eventType))
                {
                    logger.Error("X-GitHub-Event missing.");
                    return BadRequest("No event.");
                }

                string payloadString = null;
                using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
                {
                    payloadString = await reader.ReadToEndAsync();
                }

                var payloadHash = GitHubController.GetPayloadHash(payloadString, settings.Settings.Addons.GitHubSecret);
                if (sig != $"sha1={payloadHash}")
                {
                    logger.Error("PayloadHash signature check failed.");
                    return BadRequest("Signature check failed.");
                }

                if (eventType == WebHookEvents.Push)
                {
                    var payload = Serialization.FromJson<PushEvent>(payloadString);
                    if (payload.@ref?.EndsWith("master") == true)
                    {
#pragma warning disable CS4014
                        PlayniteServices.Addons.Instance.RegenerateAddonDatabase();
#pragma warning restore CS4014
                    }
                }

                return Ok();
            }

            logger.Error("Addons github webhook not processed correctly.");
            return BadRequest();
        }
    }
}
