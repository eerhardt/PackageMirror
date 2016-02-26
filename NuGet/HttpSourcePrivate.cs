// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See NuGet-LICENSE.txt in the project root for license information.

// Copied from https://github.com/NuGet/NuGet.Client and modified substantially to allow pushing an in-memory stream

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Logging;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;

namespace NuGet.Protocol
{
    public class HttpSourcePrivate : IDisposable
    {
        private readonly Func<Task<HttpHandlerResource>> _messageHandlerFactory;
        private readonly Uri _baseUri;
        private HttpClient _httpClient;
        private int _authRetries;
        private HttpHandlerResource _httpHandler;
        private CredentialHelper _credentials;
        private Guid _lastAuthId = Guid.NewGuid();
        private readonly PackageSource _packageSource;
        private readonly HttpRetryHandler _retryHandler;

        // Only one thread may re-create the http client at a time.
        private readonly SemaphoreSlim _httpClientLock = new SemaphoreSlim(1, 1);

        // In order to avoid too many open files error, set concurrent requests number to 16 on Mac
        private readonly static int ConcurrencyLimit = 16;

        // Only one source may prompt at a time
        private readonly static SemaphoreSlim _credentialPromptLock = new SemaphoreSlim(1, 1);

        // Limiting concurrent requests to limit the amount of files open at a time on Mac OSX
        // the default is 256 which is easy to hit if we don't limit concurrency
        private readonly static SemaphoreSlim _throttle =
            RuntimeEnvironmentHelper.IsMacOSX
                ? new SemaphoreSlim(ConcurrencyLimit, ConcurrencyLimit)
                : null;

        public HttpSourcePrivate(PackageSource source, Func<Task<HttpHandlerResource>> messageHandlerFactory)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (messageHandlerFactory == null)
            {
                throw new ArgumentNullException(nameof(messageHandlerFactory));
            }

            _packageSource = source;
            _baseUri = new Uri(source.Source);
            _messageHandlerFactory = messageHandlerFactory;
            _retryHandler = new HttpRetryHandler();
        }

        /// <summary>
        /// Wraps logging of the initial request and throttling.
        /// This method does not use the cache.
        /// </summary>
        internal async Task<HttpResponseMessage> SendAsync(
            Func<HttpRequestMessage> requestFactory,
            ILogger log,
            CancellationToken cancellationToken)
        {
            // Read the response headers before reading the entire stream to avoid timeouts from large packages.
            Func<Task<HttpResponseMessage>> throttledRequest = () => SendWithCredentialSupportAsync(
                    requestFactory,
                    HttpCompletionOption.ResponseHeadersRead,
                    log,
                    cancellationToken);

            return await GetThrottled(throttledRequest);
        }

        private static async Task<HttpResponseMessage> GetThrottled(Func<Task<HttpResponseMessage>> request)
        {
            if (_throttle == null)
            {
                return await request();
            }
            else
            {
                try
                {
                    await _throttle.WaitAsync();

                    return await request();
                }
                finally
                {
                    _throttle.Release();
                }
            }
        }

        private async Task<HttpResponseMessage> SendWithCredentialSupportAsync(
            Func<HttpRequestMessage> requestFactory,
            HttpCompletionOption completionOption,
            ILogger log,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            ICredentials promptCredentials = null;

            // Create the http client on the first call
            if (_httpClient == null)
            {
                await _httpClientLock.WaitAsync();
                try
                {
                    // Double check
                    if (_httpClient == null)
                    {
                        await UpdateHttpClient();
                    }
                }
                finally
                {
                    _httpClientLock.Release();
                }
            }

            // Update the request for STS
            Func<HttpRequestMessage> requestWithStsFactory = () =>
            {
                var request = requestFactory();
                STSAuthHelper.PrepareSTSRequest(_baseUri, CredentialStore.Instance, request);
                return request;
            };

            // Authorizing may take multiple attempts
            while (true)
            {
                // Clean up any previous responses
                if (response != null)
                {
                    response.Dispose();
                }

                // store the auth state before sending the request
                var beforeLockId = _lastAuthId;

                // Read the response headers before reading the entire stream to avoid timeouts from large packages.
                response = await _retryHandler.SendAsync(
                    _httpClient,
                    requestWithStsFactory,
                    completionOption,
 //                   log,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    try
                    {
                        // Only one request may prompt and attempt to auth at a time
                        await _httpClientLock.WaitAsync();

                        // Auth may have happened on another thread, if so just continue
                        if (beforeLockId != _lastAuthId)
                        {
                            continue;
                        }

                        // Give up after 3 tries.
                        _authRetries++;
                        if (_authRetries > 3)
                        {
                            return response;
                        }

                        // Windows auth
                        if (STSAuthHelper.TryRetrieveSTSToken(_baseUri, CredentialStore.Instance, response))
                        {
                            // Auth token found, create a new message handler and retry.
                            await UpdateHttpClient();
                            continue;
                        }

                        // Prompt the user
                        promptCredentials = await PromptForCredentials(cancellationToken);

                        if (promptCredentials != null)
                        {
                            // The user entered credentials, create a new message handler that includes
                            // these and retry.
                            await UpdateHttpClient(promptCredentials);
                            continue;
                        }
                    }
                    finally
                    {
                        _httpClientLock.Release();
                    }
                }

                if (promptCredentials != null && HttpHandlerResourceV3.CredentialsSuccessfullyUsed != null)
                {
                    HttpHandlerResourceV3.CredentialsSuccessfullyUsed(_baseUri, promptCredentials);
                }

                return response;
            }
        }

        private async Task<ICredentials> PromptForCredentials(CancellationToken cancellationToken)
        {
            ICredentials promptCredentials = null;

            if (HttpHandlerResourceV3.PromptForCredentials != null)
            {
                try
                {
                    // Only one prompt may display at a time.
                    await _credentialPromptLock.WaitAsync();

                    promptCredentials =
                        await HttpHandlerResourceV3.PromptForCredentials(_baseUri, cancellationToken);
                }
                finally
                {
                    _credentialPromptLock.Release();
                }
            }

            return promptCredentials;
        }

        private async Task UpdateHttpClient()
        {
            // Get package source credentials
            var credentials = CredentialStore.Instance.GetCredentials(_baseUri);

            if (credentials == null
                && !String.IsNullOrEmpty(_packageSource.UserName)
                && !String.IsNullOrEmpty(_packageSource.Password))
            {
                credentials = new NetworkCredential(_packageSource.UserName, _packageSource.Password);
            }

            if (credentials != null)
            {
                CredentialStore.Instance.Add(_baseUri, credentials);
            }

            await UpdateHttpClient(credentials);
        }

        private async Task UpdateHttpClient(ICredentials credentials)
        {
            if (_httpHandler == null)
            {
                _httpHandler = await _messageHandlerFactory();
                _httpClient = new HttpClient(_httpHandler.MessageHandler);
                _httpClient.Timeout = Timeout.InfiniteTimeSpan;

                // Create a new wrapper for ICredentials that can be modified
                _credentials = new CredentialHelper();
                _httpHandler.ClientHandler.Credentials = _credentials;

                // Always take the credentials from the helper.
                _httpHandler.ClientHandler.UseDefaultCredentials = false;

                // Set user agent
                UserAgent.SetUserAgent(_httpClient);
            }

            // Modify the credentials on the current handler
            _credentials.Credentials = credentials;

            // Mark that auth has been updated
            _lastAuthId = Guid.NewGuid();
        }

        public static HttpSourcePrivate Create(SourceRepository source)
        {
            Func<Task<HttpHandlerResource>> factory = () => source.GetResourceAsync<HttpHandlerResource>();

            return new HttpSourcePrivate(source.PackageSource, factory);
        }

        public void Dispose()
        {
            if (_httpClient != null)
            {
                _httpClient.Dispose();
            }

            _httpClientLock.Dispose();
        }
    }
}