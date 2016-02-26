using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Configuration;
using NuGet.Logging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;
using NuGet.Versioning;
using PackageMirror.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace PackageMirror.Controllers
{
    public class PackageAddedController : ApiController
    {
        private static ISettings s_settings = Settings.LoadDefaultSettings(AppContext.BaseDirectory, configFileName: null, machineWideSettings: null);

        // GET api/values
        public IEnumerable<string> Get()
        {
            return new string[] { "value3", "value4" };
        }

        // POST api/values
        public async Task Post([FromBody]WebHookEvent webHookEvent)
        {
            try
            {
                if (webHookEvent?.PayloadType == "PackageAddedWebHookEventPayloadV1")
                {
                    if (webHookEvent.Payload?.PackageType == "NuGet")
                    {
                        if (ShouldMirrorPackage(webHookEvent.Payload.FeedUrl, webHookEvent.Payload.PackageVersion))
                        {
                            string downloadUrl = webHookEvent.Payload.PackageDownloadUrl;

                            PackageSource packageSource = new PackageSource(webHookEvent.Payload.FeedUrl);
                            SourceRepository repo = new SourceRepository(packageSource, Repository.Provider.GetCoreV3());

                            DownloadResource downloadResource = await repo.GetResourceAsync<DownloadResource>();

                            PackageIdentity id = new PackageIdentity(webHookEvent.Payload.PackageIdentifier, new NuGetVersion(webHookEvent.Payload.PackageVersion));
                            DownloadResourceResult downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                                    id,
                                    s_settings,
                                    new NullLogger(),
                                    CancellationToken.None);

                            await PushPackage(downloadResult);

                            // log "success" request
                        }
                        else
                        {
                            // log "diagnostic" request that package didn't match filter
                        }
                    }
                    else
                    {
                        // log invalid request
                    }
                }
                else
                {
                    // log invalid request
                }
            }
            catch
            {
                // log error
                throw;
            }
        }

        private static bool ShouldMirrorPackage(string feedUrl, string packageVersion)
        {
            string feedFilter = ConfigurationManager.AppSettings[feedUrl];

            // setting doesn't exist for this feed
            if (feedFilter == null)
            {
                // log couldn't find feed
                return false;
            }

            // a blank setting exists, which means all packages
            if (feedFilter.Length == 0)
            {
                return true;
            }

            // filter the packageVersion on the feedFilter
            return Regex.IsMatch(packageVersion, feedFilter);
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
    }
}
