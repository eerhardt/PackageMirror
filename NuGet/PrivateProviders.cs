using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace PackageMirror.NuGet
{
    public class PrivateProviders
    {
        internal static IEnumerable<Lazy<INuGetResourceProvider>> Get()
        {
            yield return new Lazy<INuGetResourceProvider>(() => new DownloadResourceV2PrivateProvider());
        }

        // Copied from https://github.com/NuGet/NuGet.Client and modified to suppress the cache
        private class DownloadResourceV2PrivateProvider : ResourceProvider
        {
            public DownloadResourceV2PrivateProvider()
                : base(typeof(DownloadResource), nameof(DownloadResourceV2PrivateProvider), "DownloadResourceV2FeedProvider")
            {
            }

            public override Task<Tuple<bool, INuGetResource>> TryCreate(SourceRepository source, CancellationToken token)
            {
                DownloadResource resource = null;

                if ((FeedTypeUtility.GetFeedType(source.PackageSource) & FeedType.HttpV2) != FeedType.None)
                {
                    var httpSource = HttpSource.Create(source);
                    var parser = new V2FeedParser(httpSource, source.PackageSource);

                    resource = new DownloadResourceV2FeedPrivate(parser, httpSource);
                }

                return Task.FromResult(new Tuple<bool, INuGetResource>(resource != null, resource));
            }
        }
    }
}