using System;
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
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;
using NuGet.Versioning;
using PackageMirror.Models;
using PackageMirror.NuGet;

namespace PackageMirror.Controllers
{
    public class PackageAddedController : ApiController
    {
        private static ISettings s_settings = Settings.LoadDefaultSettings(AppContext.BaseDirectory, configFileName: null, machineWideSettings: null);
        private static ConcurrentDictionary<Uri, List<string>> s_filterCache = new ConcurrentDictionary<Uri, List<string>>();
        private static ConcurrentDictionary<Uri, DownloadResource> s_downloadResourceCache = new ConcurrentDictionary<Uri, DownloadResource>();
        private static PackagePushResource s_packagePushResource;

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
                        Uri feedUrl = new Uri(payload.FeedUrl);
                        if (ShouldMirrorPackage(feedUrl, payload.PackageIdentifier, payload.PackageVersion))
                        {
                            DownloadResource downloadResource = await GetDownloadResource(feedUrl);

                            PackageIdentity id = new PackageIdentity(payload.PackageIdentifier, new NuGetVersion(payload.PackageVersion));
                            using (DownloadResourceResult downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                                    id,
                                    s_settings,
                                    new NullLogger(),
                                    CancellationToken.None))
                            {
                                await PushPackage(downloadResult);
                            }

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

        private static async Task<DownloadResource> GetDownloadResource(Uri feedUrl)
        {
            DownloadResource resource;
            if (!s_downloadResourceCache.TryGetValue(feedUrl, out resource))
            {
                PackageSource packageSource = new PackageSource(feedUrl.AbsoluteUri);
                SourceRepository repo = new SourceRepository(packageSource, PrivateProviders.Get().Union(Repository.Provider.GetCoreV3()));

                resource = await repo.GetResourceAsync<DownloadResource>();
                s_downloadResourceCache.TryAdd(feedUrl, resource);
            }

            return resource;
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
            using (Stream stream = downloadResult.PackageStream)
            {
                PackagePushResource pushCommandResource = GetPackagePushResource();
                await pushCommandResource.Push(stream,
                    GetPushTimeout(),
                    GetApiKey,
                    new NullLogger());
            }
        }

        private static PackagePushResource GetPackagePushResource()
        {
            if (s_packagePushResource == null)
            {
                string destinationUrl = GetDestinationFeedUrl();
                PackageSource packageSource = new PackageSource(destinationUrl);
                SourceRepository repo = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
                HttpSourcePrivate source = HttpSourcePrivate.Create(repo);

                s_packagePushResource = new PackagePushResource(destinationUrl, source);
            }

            return s_packagePushResource;
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

        private static string GetApiKey(string source)
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
