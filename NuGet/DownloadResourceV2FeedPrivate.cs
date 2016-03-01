// Copied from https://github.com/NuGet/NuGet.Client and modified to suppress the cache

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Logging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;

namespace NuGet.Protocol
{
    public class DownloadResourceV2FeedPrivate : DownloadResource
    {
        private readonly V2FeedParser _parser;
        private readonly HttpSource _source;

        public DownloadResourceV2FeedPrivate(V2FeedParser parser, HttpSource source)
        {
            _parser = parser;
            _source = source;
        }

        public override async Task<DownloadResourceResult> GetDownloadResourceResultAsync(
            PackageIdentity identity,
            ISettings settings,
            ILogger logger,
            CancellationToken token)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            token.ThrowIfCancellationRequested();

            var sourcePackage = identity as SourcePackageDependencyInfo;
            bool isFromUri = sourcePackage?.PackageHash != null
                            && sourcePackage?.DownloadUri != null;

            Uri sourceUri;
            if (isFromUri)
            {
                sourceUri = sourcePackage.DownloadUri;
            }
            else
            {
                V2FeedPackageInfo packageInfo = await _parser.GetPackage(identity, logger, token);

                if (packageInfo == null)
                {
                    return new DownloadResourceResult(DownloadResourceResultStatus.NotFound);
                }

                sourceUri = new Uri(packageInfo.DownloadUrl);
            }

            try
            {
                return await GetDownloadResultUtilityPrivate.GetDownloadResultAsync(_source, sourcePackage, sourceUri, settings, logger, token);
            }
            catch (OperationCanceledException)
            {
                return new DownloadResourceResult(DownloadResourceResultStatus.Cancelled);
            }
            catch (Exception ex) when (!(ex is FatalProtocolException))
            {
                // if the expcetion is not FatalProtocolException, catch it.
                string message = string.Format(CultureInfo.CurrentCulture, "Error downloading '{0}' from '{1}'.", identity, sourceUri);
                logger.LogError(message + Environment.NewLine + ExceptionUtilities.DisplayMessage(ex));

                throw new FatalProtocolException(message, ex);
            }
        }
    }
}
