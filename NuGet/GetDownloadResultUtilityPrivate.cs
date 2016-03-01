// Copied from https://github.com/NuGet/NuGet.Client and modified to suppress the cache

using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Logging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;

namespace NuGet.Protocol
{
    public static class GetDownloadResultUtilityPrivate
    {
        public static async Task<DownloadResourceResult> GetDownloadResultAsync(
           HttpSource client,
           PackageIdentity identity,
           Uri uri,
           ISettings settings,
           ILogger logger,
           CancellationToken token)
        {
            // Uri is not null, so the package exists in the source

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    return await client.ProcessStreamAsync(
                        uri: uri,
                        ignoreNotFounds: true,
                        process: async packageStream =>
                        {
                            if (packageStream == null)
                            {
                                return new DownloadResourceResult(DownloadResourceResultStatus.NotFound);
                            }

                            int capacity = 4096;
                            try
                            {
                                capacity = (int)packageStream.Length;
                            }
                            catch { }

                            MemoryStream memoryStream = new MemoryStream(capacity);
                            await packageStream.CopyToAsync(memoryStream);
                            await memoryStream.FlushAsync();
                            memoryStream.Position = 0;

                            return new DownloadResourceResult(memoryStream);
                        },
                        log: logger,
                        token: token);
                }
                catch (OperationCanceledException)
                {
                    return new DownloadResourceResult(DownloadResourceResultStatus.Cancelled);
                }
                catch (IOException ex) when (ex.InnerException is SocketException && i < 2)
                {
                    string message = string.Format(CultureInfo.CurrentCulture, "Error downloading '{0}' from '{1}'.", identity, uri)
                        + Environment.NewLine
                        + ExceptionUtilities.DisplayMessage(ex);
                    logger.LogWarning(message);
                }
                catch (Exception ex)
                {
                    string message = string.Format(CultureInfo.CurrentCulture, "Error downloading '{0}' from '{1}'.", identity, uri);
                    logger.LogError(message + Environment.NewLine + ExceptionUtilities.DisplayMessage(ex));

                    throw new FatalProtocolException(message, ex);
                }
            }

            throw new InvalidOperationException("Reached an unexpected point in the code");
        }
    }
}
