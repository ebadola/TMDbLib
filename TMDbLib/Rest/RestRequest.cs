using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TMDbLib.Objects.Exceptions;
using TMDbLib.Objects.General;

namespace TMDbLib.Rest
{
    internal class RestRequest
    {
        private readonly RestClient _client;
        private readonly string _endpoint;

        private List<KeyValuePair<string, string>> _queryString;
        private List<KeyValuePair<string, string>> _urlSegment;

        private object _bodyObj;

        public RestRequest(RestClient client, string endpoint)
        {
            _client = client;
            _endpoint = endpoint;
        }

        private void AppendQueryString(StringBuilder sb, string key, string value)
        {
            if (sb.Length > 0)
                sb.Append("&");

            sb.Append(key);
            sb.Append("=");
            sb.Append(WebUtility.UrlEncode(value));
        }

        private void AppendQueryString(StringBuilder sb, KeyValuePair<string, string> value)
        {
            AppendQueryString(sb, value.Key, value.Value);
        }
        
        public RestRequest AddParameter(string key, string value, ParameterType type = ParameterType.QueryString)
        {
            switch (type)
            {
                case ParameterType.QueryString:
                    return AddQueryString(key, value);
                case ParameterType.UrlSegment:
                    return AddUrlSegment(key, value);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public RestRequest AddUrlSegment(string key, string value)
        {
            if (_urlSegment == null)
                _urlSegment = new List<KeyValuePair<string, string>>();

            _urlSegment.Add(new KeyValuePair<string, string>(key, value));

            return this;
        }

        public RestRequest AddQueryString(string key, string value)
        {
            if (_queryString == null)
                _queryString = new List<KeyValuePair<string, string>>();

            _queryString.Add(new KeyValuePair<string, string>(key, value));

            return this;
        }

        public RestRequest SetBody(object obj)
        {
            _bodyObj = obj;

            return this;
        }

        private HttpRequestMessage PrepRequest(HttpMethod method)
        {
            StringBuilder queryStringSb = new StringBuilder();

            // Query String
            if (_queryString != null)
            {
                foreach (KeyValuePair<string, string> pair in _queryString)
                    AppendQueryString(queryStringSb, pair);
            }

            foreach (KeyValuePair<string, string> pair in _client.DefaultQueryString)
                AppendQueryString(queryStringSb, pair);

            // Url
            string endpoint = _endpoint;
            if (_urlSegment != null)
            {
                foreach (KeyValuePair<string, string> pair in _urlSegment)
                    endpoint = endpoint.Replace("{" + pair.Key + "}", pair.Value);
            }

            // Build
            UriBuilder builder = new UriBuilder(new Uri(_client.BaseUrl, endpoint));
            builder.Query = queryStringSb.ToString();

            HttpRequestMessage req = new HttpRequestMessage(method, builder.Uri);

            // Body
            if (method == HttpMethod.Post && _bodyObj != null)
            {
                string json = JsonConvert.SerializeObject(_bodyObj);

                req.Content = new StringContent(json);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            return req;
        }
        
        private void CheckResponse(ResponseContainer response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException("Call to TMDb returned unauthorized. Most likely the provided API key is invalid.");
        }

        public async Task<RestResponse> ExecuteGet()
        {
            return await Execute(HttpMethod.Get);
        }

        public async Task<RestResponse<T>> ExecuteGet<T>()
        {
            return await Execute<T>(HttpMethod.Get);
        }

        public async Task<RestResponse> ExecutePost()
        {
            return await Execute(HttpMethod.Post);
        }

        public async Task<RestResponse<T>> ExecutePost<T>()
        {
            return await Execute<T>(HttpMethod.Post);
        }

        public async Task<RestResponse> ExecuteDelete()
        {
            return await Execute(HttpMethod.Delete);
        }

        public async Task<RestResponse<T>> ExecuteDelete<T>()
        {
            return await Execute<T>(HttpMethod.Delete);
        }

        private async Task<RestResponse> Execute(HttpMethod method)
        {
            ResponseContainer resp = await SendInternal(method).ConfigureAwait(false);

            CheckResponse(resp);

            return new RestResponse(resp.Headers, resp.StatusCode, resp.IsSuccessStatusCode, resp.ResponseContent, resp.ErrorMessage);
        }

        private async Task<RestResponse<T>> Execute<T>(HttpMethod method)
        {
            ResponseContainer resp = await SendInternal(method).ConfigureAwait(false);

            CheckResponse(resp);

            return new RestResponse<T>(resp.Headers, resp.StatusCode, resp.IsSuccessStatusCode, resp.ResponseContent, resp.ErrorMessage);
        }

        private async Task<ResponseContainer> SendInternal(HttpMethod method)
        {
            // Account for the following settings:
            // - MaxRetryCount                          Max times to retry
            // DEPRECATED RetryWaitTimeInSeconds        Time to wait between retries
            // DEPRECATED ThrowErrorOnExeedingMaxCalls  Throw an exception if we hit a ratelimit

            int timesToTry = _client.MaxRetryCount;
            Debug.Assert(timesToTry >= 0);

            using (HttpClient httpClient = new HttpClient())
            {
                TmdbStatusMessage statusMessage;
                do
                {
                    statusMessage = null;

                    HttpRequestMessage req = PrepRequest(method);
                    HttpResponseMessage resp;
                    try
                    {
                        resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new TmdbNetworkException(ex.Message, ex);
                    }

                    if (resp.Content.Headers.ContentType?.MediaType != "application/json")
                    {
                        throw new BadResponseTypeException(resp.StatusCode);
                    }

                    string responseContent = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        // Try to get status message
                        statusMessage = JsonConvert.DeserializeObject<TmdbStatusMessage>(responseContent);
                    }

                    if (resp.StatusCode == (HttpStatusCode)429)
                    {
                        // The previous result was a ratelimit, read the Retry-After header and wait the allotted time
                        TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta.Value;

                        if (retryAfter.HasValue && retryAfter.Value.TotalSeconds > 0)
                            await Task.Delay(retryAfter.Value).ConfigureAwait(false);
                        else
                            // TMDb sometimes gives us 0-second waits, which can lead to rapid succession of requests
                            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

                        continue;
                    }

                    if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
                    {
                        // We got a bad response that was NOT 404 - not found.
                        // We want to let the caller handle 404's
                        throw new GenericWebException(resp.StatusCode, statusMessage);
                    }

                    return new ResponseContainer
                    {
                        Headers = resp.Headers,
                        StatusCode = resp.StatusCode,
                        IsSuccessStatusCode = resp.IsSuccessStatusCode,
                        ErrorMessage = statusMessage,
                        ResponseContent = responseContent
                    };
                } while (timesToTry-- > 0);

                // We never reached a success
                throw new RequestLimitExceededException((HttpStatusCode)429, statusMessage);
            }
        }

        private class ResponseContainer
        {
            public bool IsSuccessStatusCode { get; set; }

            public HttpStatusCode StatusCode { get; set; }

            public TmdbStatusMessage ErrorMessage { get; set; }

            public string ResponseContent { get; set; }

            public HttpResponseHeaders Headers { get; set; }
        }
    }
}