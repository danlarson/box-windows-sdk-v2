﻿using Box.V2.Config;
using Box.V2.Utility;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Box.V2.Request
{
    public class HttpRequestHandler : IRequestHandler
    {
        const HttpStatusCode TooManyRequests = (HttpStatusCode)429;

        public async Task<IBoxResponse<T>> ExecuteAsync<T>(IBoxRequest request)
            where T : class
        {
            // Need to account for special cases when the return type is a stream
            bool isStream = typeof(T) == typeof(Stream);
            var numRetries = 3;

            try
            {
                // TODO: yhu@ better handling of different request
                var isMultiPartRequest = request.GetType() == typeof(BoxMultiPartRequest);
                var isBinaryRequest = request.GetType() == typeof(BoxBinaryRequest);

                while (true)
                {
                    HttpRequestMessage httpRequest = null;

                    if (isMultiPartRequest)
                    {
                        httpRequest = BuildMultiPartRequest(request as BoxMultiPartRequest);
                    }
                    else if (isBinaryRequest)
                    {
                        httpRequest = BuildBinaryRequest(request as BoxBinaryRequest);
                    }
                    else
                    {
                        httpRequest = BuildRequest(request);
                    }

                    // Add headers
                    foreach (var kvp in request.HttpHeaders)
                    {
                        // They could not be added to the headers directly
                        if (kvp.Key == Constants.RequestParameters.ContentMD5
                            || kvp.Key == Constants.RequestParameters.ContentRange)
                        {
                            httpRequest.Content.Headers.Add(kvp.Key, kvp.Value);
                        }
                        else
                        {
                            httpRequest.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                        }
                    }

                    // If we are retrieving a stream, we should return without reading the entire response
                    HttpCompletionOption completionOption = isStream ?
                        HttpCompletionOption.ResponseHeadersRead :
                        HttpCompletionOption.ResponseContentRead;

                    Debug.WriteLine(string.Format("RequestUri: {0}", httpRequest.RequestUri));//, RequestHeader: {1} , httpRequest.Headers.Select(i => string.Format("{0}:{1}", i.Key, i.Value)).Aggregate((i, j) => i + "," + j)));

                    HttpClient client = CreateClient(request);
                    BoxResponse<T> boxResponse = new BoxResponse<T>();

                    HttpResponseMessage response = await client.SendAsync(httpRequest, completionOption).ConfigureAwait(false);
            
                    // If we get a 429 error code and this is not a multi part request (meaning a file upload, which cannot be retried
                    // because the stream cannot be reset) and we haven't exceeded the number of allowed retries, then retry the request.
                    if((response.StatusCode == TooManyRequests && !isMultiPartRequest) && numRetries-- > 0)
                    {
                        //need to wait for Retry-After seconds and then retry request
                        var retryAfterHeader = response.Headers.RetryAfter;

                        TimeSpan delay;
                        if (retryAfterHeader.Delta.HasValue)
                            delay = retryAfterHeader.Delta.Value;
                        else
                            delay = TimeSpan.FromMilliseconds(2000);

                        Debug.WriteLine("TooManyRequests error (429). Waiting for {0} seconds to retry request. RequestUri: {1}", delay.Seconds, httpRequest.RequestUri);

                        await CrossPlatform.Delay(Convert.ToInt32(delay.TotalMilliseconds));
                    }
                    else
                    {
                        boxResponse.Headers = response.Headers;

                        // Translate the status codes that interest us 
                        boxResponse.StatusCode = response.StatusCode;
                        switch (response.StatusCode)
                        {
                            case HttpStatusCode.OK:
                            case HttpStatusCode.Created:
                            case HttpStatusCode.NoContent:
                            case HttpStatusCode.Found:
                                boxResponse.Status = ResponseStatus.Success;
                                break;
                            case HttpStatusCode.Accepted:
                                boxResponse.Status = ResponseStatus.Pending;
                                break;
                            case HttpStatusCode.Unauthorized:
                                boxResponse.Status = ResponseStatus.Unauthorized;
                                break;
                            case HttpStatusCode.Forbidden:
                                boxResponse.Status = ResponseStatus.Forbidden;
                                break;
                            case TooManyRequests:
                                boxResponse.Status = ResponseStatus.TooManyRequests;
                                break;
                            default:
                                boxResponse.Status = ResponseStatus.Error;
                                break;
                        }

                        if (isStream && boxResponse.Status == ResponseStatus.Success)
                        {
                            var resObj = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            boxResponse.ResponseObject = resObj as T;
                        }
                        else
                        {
                            boxResponse.ContentString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }

                        return boxResponse;
                    }         
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format("Exception: {0}", ex.Message));
                throw;
            }
        }

        private HttpClient CreateClient(IBoxRequest request)
        {
            HttpClientHandler handler = new HttpClientHandler() { AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip };
            handler.AllowAutoRedirect = request.FollowRedirect;

            HttpClient client = new HttpClient(handler);
            
            if (request.Timeout.HasValue)
                client.Timeout = request.Timeout.Value;

            return client;
        }

        private HttpRequestMessage BuildRequest(IBoxRequest request)
        {
            HttpRequestMessage httpRequest = new HttpRequestMessage();
            httpRequest.RequestUri = request.AbsoluteUri;
            httpRequest.Method = GetHttpMethod(request.Method);
            if (httpRequest.Method == HttpMethod.Get)
            {
                return httpRequest;
            }

            HttpContent content = null;

            // Set request content to string or form-data
            if (!string.IsNullOrWhiteSpace(request.Payload))
            {
                if (string.IsNullOrEmpty(request.ContentType))
                {
                    content = new StringContent(request.Payload);
                }
                else
                {
                    content = new StringContent(request.Payload, request.ContentEncoding, request.ContentType);
                }
            }
            else
            {
                content = new FormUrlEncodedContent(request.PayloadParameters);
            }

            httpRequest.Content = content;

            return httpRequest;
        }

        private HttpRequestMessage BuildBinaryRequest(BoxBinaryRequest request)
        {
            HttpRequestMessage httpRequest = new HttpRequestMessage();
            httpRequest.RequestUri = request.AbsoluteUri;
            httpRequest.Method = GetHttpMethod(request.Method);

            HttpContent content = null;

            var filePart = request.Part as BoxFilePart;
            if (filePart != null)
            {
                content = new StreamContent(filePart.Value);
            }

            httpRequest.Content = content;

            return httpRequest;
        }

        private HttpMethod GetHttpMethod(RequestMethod requestMethod)
        {
            switch (requestMethod)
            {
                case RequestMethod.Get:
                    return HttpMethod.Get;
                case RequestMethod.Put:
                    return HttpMethod.Put;
                case RequestMethod.Delete:
                    return HttpMethod.Delete;
                case RequestMethod.Post:
                    return HttpMethod.Post;
                case RequestMethod.Options:
                    return HttpMethod.Options;
                default:
                    throw new InvalidOperationException("Http method not supported");
            }
        }

        private HttpRequestMessage BuildMultiPartRequest(BoxMultiPartRequest request)
        {
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, request.AbsoluteUri);
            MultipartFormDataContent multiPart = new MultipartFormDataContent();

            // Break out the form parts from the request
            var filePart = request.Parts.Where(p => p.GetType() == typeof(BoxFileFormPart))
                .Select(p => p as BoxFileFormPart)
                .FirstOrDefault(); // Only single file upload is supported at this time
            var stringParts = request.Parts.Where(p => p.GetType() == typeof(BoxStringFormPart))
                .Select(p => p as BoxStringFormPart);

            // Create the string parts
            foreach (var sp in stringParts)
                multiPart.Add(new StringContent(sp.Value), ForceQuotesOnParam(sp.Name));

            // Create the file part
            if (filePart != null)
            {
                StreamContent fileContent = new StreamContent(filePart.Value);
                fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = ForceQuotesOnParam(filePart.Name),
                    FileName = ForceQuotesOnParam(filePart.FileName)
                };
                multiPart.Add(fileContent);
            }

            httpRequest.Content = multiPart;

            return httpRequest;
        }

        /// <summary>
        /// Adds quotes around the named parameters
        /// This is required as the API will currently not take multi-part parameters without quotes
        /// </summary>
        /// <param name="name">The name parameter to add quotes to</param>
        /// <returns>The name parameter surrounded by quotes</returns>
        private string ForceQuotesOnParam(string name)
        {
            return string.Format("\"{0}\"", name);
        }
    }
}

