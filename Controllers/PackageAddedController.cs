﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using NuGet.Configuration;
using NuGet.Logging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;
using NuGet.Versioning;
using PackageMirror.Models;

namespace PackageMirror.Controllers
{
    public class PackageAddedController : ApiController
    {
        private static ISettings s_settings = Settings.LoadDefaultSettings(AppContext.BaseDirectory, configFileName: null, machineWideSettings: null);
        private static ConcurrentDictionary<Uri, List<string>> s_filterCache = new ConcurrentDictionary<Uri, List<string>>();

        public async Task Post([FromBody]WebHookEvent webHookEvent)
        {
            TraceInfo($"POST PackageAdded Started");

            try
            {
                if (webHookEvent?.PayloadType == "PackageAddedWebHookEventPayloadV1")
                {
                    if (webHookEvent.Payload?.PackageType == "NuGet")
                    {
                        PackageAddedWebHookEventPayloadV1 payload = webHookEvent.Payload;
                        if (ShouldMirrorPackage(new Uri(payload.FeedUrl), payload.PackageIdentifier, payload.PackageVersion))
                        {
                            PackageSource packageSource = new PackageSource(payload.FeedUrl);
                            SourceRepository repo = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());

                            DownloadResource downloadResource = await repo.GetResourceAsync<DownloadResource>();

                            PackageIdentity id = new PackageIdentity(payload.PackageIdentifier, new NuGetVersion(payload.PackageVersion));
                            DownloadResourceResult downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                                    id,
                                    s_settings,
                                    new NullLogger(),
                                    CancellationToken.None);

                            await PushPackage(downloadResult);

                            TraceInfo($"Successfully pushed package {payload.PackageIdentifier} {payload.PackageVersion}");
                        }
                        else
                        {
                            TraceInfo($"Package didn't match any filters {payload.PackageIdentifier} {payload.PackageVersion}");
                        }
                    }
                    else
                    {
                        TraceInfo($"Non NuGet package added: {webHookEvent.Payload?.PackageType}");
                    }
                }
                else
                {
                    TraceError($"Not a valid PayloadType: {webHookEvent?.PayloadType}");
                }
            }
            catch (Exception e)
            {
                TraceError($"An unhandled exception occurred: {e}");
                throw;
            }
            finally
            {
                TraceInfo($"POST PackageAdded Complete");
            }
        }

        private static bool ShouldMirrorPackage(Uri feedUrl, string packageId, string packageVersion)
        {
            List<string> filters;
            if (!s_filterCache.TryGetValue(feedUrl, out filters))
            {
                filters = new List<string>();

                foreach (string key in ConfigurationManager.AppSettings)
                {
                    Uri keyUri;
                    if (Uri.TryCreate(key, UriKind.Absolute, out keyUri))
                    {
                        if (feedUrl.Equals(keyUri))
                        {
                            filters.AddRange(ConfigurationManager.AppSettings[key].Split('|'));
                        }
                    }
                }

                s_filterCache.TryAdd(feedUrl, filters);
            }

            // no settings exist for this feed
            if (filters.Count == 0)
            {
                TraceError($"Couldn't find feed: {feedUrl}");
                return false;
            }

            // if there is only a blank filter for this feed, all packages should be included
            if (filters.Count == 1 && filters[0].Length == 0)
            {
                return true;
            }

            return filters.Any(f => EvaluateFilter(f, packageId, packageVersion));
        }

        private static bool EvaluateFilter(string filter, string packageId, string packageVersion)
        {
            if (filter.StartsWith("ID-", StringComparison.Ordinal))
            {
                return Regex.IsMatch(packageId, filter.Substring(3));
            }
            else if (filter.StartsWith("V-", StringComparison.Ordinal))
            {
                return Regex.IsMatch(packageVersion, filter.Substring(2));
            }
            else
            {
                TraceError($"Invalid filter: {filter}");
                return false;
            }
        }

        private static async Task PushPackage(DownloadResourceResult downloadResult)
        {
            PackageSource packageSource = new PackageSource(GetDestinationFeedUrl());
            SourceRepository repo = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());

            string tempFile = Path.GetTempFileName();
            try
            {
                using (Stream stream = downloadResult.PackageStream)
                using (FileStream fileStream = File.OpenWrite(tempFile))
                {
                    await stream.CopyToAsync(fileStream);
                }

                PackageUpdateResource pushCommandResource = await repo.GetResourceAsync<PackageUpdateResource>();
                await pushCommandResource.Push(tempFile,
                            GetPushTimeout(),
                            GetApiKey,
                            new NullLogger());
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        private static string GetDestinationFeedUrl()
        {
            return ConfigurationManager.AppSettings["DestinationUrl"];
        }

        private static int GetPushTimeout()
        {
            int timeout;
            if (!int.TryParse(ConfigurationManager.AppSettings["PushTimeOut"], out timeout))
            {
                timeout = 180;
            }
            return timeout;
        }

        private static string GetApiKey(string s)
        {
            return ConfigurationManager.AppSettings["DestinationApiKey"];
        }

        private static void TraceInfo(string message)
        {
            Trace.TraceInformation(message);
        }

        private static void TraceError(string message)
        {
            Trace.TraceError(message);
        }
    }
}
