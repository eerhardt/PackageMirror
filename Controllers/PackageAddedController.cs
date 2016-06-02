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

            string packageId = null;
            string packageVersion = null;
            try
            {
                if (webHookEvent?.PayloadType == "PackageAddedWebHookEventPayloadV1")
                {
                    if (webHookEvent.Payload?.PackageType == "NuGet")
                    {
                        PackageAddedWebHookEventPayloadV1 payload = webHookEvent.Payload;
                        packageId = payload.PackageIdentifier;
                        packageVersion = payload.PackageVersion;

                        Uri sourceFeedUrl = new Uri(payload.FeedUrl);
                        if (ShouldMirrorPackage(sourceFeedUrl, packageId, packageVersion))
                        {
                            TraceInfo($"Downloading package {packageId} {packageVersion}");

                            DownloadResource downloadResource = await GetDownloadResource(sourceFeedUrl);

                            PackageIdentity id = new PackageIdentity(packageId, new NuGetVersion(packageVersion));
                            using (DownloadResourceResult downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                                    id,
                                    s_settings,
                                    new NullLogger(),
                                    CancellationToken.None))
                            {
                                if (downloadResult.Status == DownloadResourceResultStatus.Available)
                                {
                                    await PushPackage(downloadResult, sourceFeedUrl);
                                    TraceInfo($"Successfully pushed package {packageId} {packageVersion}");
                                }
                                else
                                {
                                    string message = $"The package {packageId} {packageVersion} was not available.";
                                    TraceError(message);
                                    throw new Exception(message);
                                }
                            }
                        }
                        else
                        {
                            TraceInfo($"Package didn't match any filters {packageId} {packageVersion}");
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
                TraceError($"An unhandled exception occurred handling package {packageId} {packageVersion} : {e}");
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

            return ShouldIncludePackage(filters, packageId, packageVersion) &&
                !ShouldExcludePackage(filters, packageId, packageVersion);
        }

        private static bool ShouldIncludePackage(List<string> filters, string packageId, string packageVersion)
        {
            var includeFilters = filters
                .Where(f => !f.StartsWith("x", StringComparison.Ordinal));

            // if there are no include filters for this feed, all packages should be included
            return !includeFilters.Any() ||
                includeFilters.Any(f => EvaluateIncludeFilter(f, packageId, packageVersion));
        }

        private static bool EvaluateIncludeFilter(string filter, string packageId, string packageVersion)
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

        private static bool ShouldExcludePackage(List<string> filters, string packageId, string packageVersion)
        {
            return filters
                .Where(f => f.StartsWith("x", StringComparison.Ordinal))
                .Any(f => EvaluateExcludeFilter(f, packageId, packageVersion));
        }

        private static bool EvaluateExcludeFilter(string filter, string packageId, string packageVersion)
        {
            if (filter.StartsWith("xID-", StringComparison.Ordinal))
            {
                return Regex.IsMatch(packageId, filter.Substring(4));
            }
            else if (filter.StartsWith("xV-", StringComparison.Ordinal))
            {
                return Regex.IsMatch(packageVersion, filter.Substring(3));
            }
            else
            {
                TraceError($"Invalid filter: {filter}");
                return false;
            }
        }

        private static async Task PushPackage(DownloadResourceResult downloadResult, Uri sourceFeedUrl)
        {
            List<PackagePushResource> destinations = GetAllPackageDestinations(sourceFeedUrl);

            if (destinations.Count == 1)
            {
                // special case 1 so we don't have to duplicate any memory and copy streams around
                await PushPackage(destinations[0], downloadResult.PackageStream);
            }
            else
            {
                // if we have to push it to more than 1 place, copy the package in-memory so it can be reset
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    await downloadResult.PackageStream.CopyToAsync(memoryStream);

                    foreach (PackagePushResource pushCommandResource in destinations)
                    {
                        memoryStream.Position = 0;

                        using (MemoryStream clonedStream = new MemoryStream((int)Math.Min(memoryStream.Length, int.MaxValue)))
                        {
                            await memoryStream.CopyToAsync(clonedStream);
                            clonedStream.Position = 0;

                            await PushPackage(pushCommandResource, clonedStream);
                        }
                    }
                }
            }
        }

        private static Task PushPackage(PackagePushResource pushCommandResource, Stream stream)
        {
            TraceInfo($"Pushing package to {pushCommandResource.ToString()}");
            
            // NOTE: pushCommandResource.Push will dispose of the stream passed into it
            
            return pushCommandResource.Push(stream,
                GetPushTimeout(),
                GetApiKey,
                new NullLogger());
        }

        private static List<PackagePushResource> GetAllPackageDestinations(Uri sourceFeedUrl)
        {
            List<PackagePushResource> result = new List<PackagePushResource>();
            result.Add(GetPackagePushResource());

            List<ExtraDestination> extraDestinations = ExtraDestination.Parse(ConfigurationManager.AppSettings["ExtraDestinations"]);

            foreach (ExtraDestination extraDestination in extraDestinations)
            {
                if (Uri.Compare(
                    extraDestination.SourceFeedUrl, 
                    sourceFeedUrl, 
                    UriComponents.AbsoluteUri, 
                    UriFormat.SafeUnescaped, 
                    StringComparison.OrdinalIgnoreCase) == 0)
                {
                    result.Add(CreatePackagePushResource(extraDestination.DestinationFeedUrl.ToString()));
                }
            }

            return result;
        }

        private static PackagePushResource GetPackagePushResource()
        {
            if (s_packagePushResource == null)
            {
                string destinationUrl = GetDestinationFeedUrl();
                s_packagePushResource = CreatePackagePushResource(destinationUrl);
            }

            return s_packagePushResource;
        }

        private static PackagePushResource CreatePackagePushResource(string destinationUrl)
        {
            PackageSource packageSource = new PackageSource(destinationUrl);
            SourceRepository repo = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());
            HttpSourcePrivate source = HttpSourcePrivate.Create(repo);

            return new PackagePushResource(destinationUrl, source);
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
