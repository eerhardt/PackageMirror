// Copied from https://github.com/NuGet/NuGet.Client and modified substantially to allow pushing an in-memory stream

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Logging;

namespace NuGet.Protocol.Core.Types
{
    /// <summary>
    /// Contains logics to push packages in Http server
    /// </summary>
    public class PackagePushResource : INuGetResource
    {
        private const string ServiceEndpoint = "/api/v2/package";
        private const string ApiKeyHeader = "X-NuGet-ApiKey";

        private HttpSourcePrivate _httpSource;
        private string _source;

        public PackagePushResource(string source,
            HttpSourcePrivate httpSource)
        {
            _source = source;
            _httpSource = httpSource;
        }

        public async Task Push(Stream packageStream,
            int timeoutInSecond,
            Func<string, string> getApiKey,
            ILogger log)
        {
            using (var tokenSource = new CancellationTokenSource())
            {
                if (timeoutInSecond > 0)
                {
                    tokenSource.CancelAfter(TimeSpan.FromSeconds(timeoutInSecond));
                }

                var apiKey = getApiKey(_source);

                await PushPackage(packageStream, _source, apiKey, log, tokenSource.Token);
            }
        }

        private async Task PushPackage(Stream packageStream,
            string source,
            string apiKey,
            ILogger log,
            CancellationToken token)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                log.LogWarning(string.Format(CultureInfo.CurrentCulture,
                    "No API Key: {0}",
                    GetSourceDisplayName(source)));
            }

            await PushPackageCore(source, apiKey, packageStream, log, token);
        }

        private async Task PushPackageCore(string source,
            string apiKey,
            Stream packageStream,
            ILogger log,
            CancellationToken token)
        {
            await PushPackageToServer(source, apiKey, packageStream, log, token);

            log.LogInformation("Package Pushed");
        }

        private static string GetSourceDisplayName(string source)
        {
            if (string.IsNullOrEmpty(source) || source.Equals(NuGetConstants.DefaultGalleryServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                return /*Strings.LiveFeed +*/ " (" + NuGetConstants.DefaultGalleryServerUrl + ")";
            }
            if (source.Equals(NuGetConstants.DefaultSymbolServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                return /*Strings.DefaultSymbolServer +*/ " (" + NuGetConstants.DefaultSymbolServerUrl + ")";
            }
            return "'" + source + "'";
        }

        // Pushes a package to the Http server.
        private async Task PushPackageToServer(string source,
            string apiKey,
            Stream packageStream,
            ILogger logger,
            CancellationToken token)
        {
            var response = await _httpSource.SendAsync(
                () => CreateRequest(source, packageStream, apiKey),
                logger,
                token);

            using (response)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        private HttpRequestMessage CreateRequest(string source,
            Stream packageStream,
            string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, GetServiceEndpointUrl(source, string.Empty));
            var content = new MultipartFormDataContent();

            var packageContent = new StreamContent(packageStream);
            packageContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            //"package" and "package.nupkg" are random names for content deserializing
            //not tied to actual package name.  
            content.Add(packageContent, "package", "package.nupkg");
            request.Content = content;

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add(ApiKeyHeader, apiKey);
            }
            return request;
        }

        // Calculates the URL to the package to.
        private Uri GetServiceEndpointUrl(string source, string path)
        {
            var baseUri = EnsureTrailingSlash(source);
            Uri requestUri;
            if (String.IsNullOrEmpty(baseUri.AbsolutePath.TrimStart('/')))
            {
                // If there's no host portion specified, append the url to the client.
                requestUri = new Uri(baseUri, ServiceEndpoint + '/' + path);
            }
            else
            {
                requestUri = new Uri(baseUri, path);
            }
            return requestUri;
        }

        // Ensure a trailing slash at the end
        private static Uri EnsureTrailingSlash(string value)
        {
            if (!value.EndsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                value += "/";
            }

            return new Uri(value);
        }
    }
}