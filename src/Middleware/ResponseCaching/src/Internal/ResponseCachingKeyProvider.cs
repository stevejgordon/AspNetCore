// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    internal class ResponseCachingKeyProviderV2 : IResponseCachingKeyProvider
    {
        // Use the record separator for delimiting components of the cache key to avoid possible collisions
        private static readonly char KeyDelimiter = '\x1e';

        // Use the unit separator for delimiting subcomponents of the cache key to avoid possible collisions
        private static readonly char KeySubDelimiter = '\x1f';

        private static readonly char HChar = 'H';

        private readonly ResponseCachingOptions _options;

        internal ResponseCachingKeyProviderV2(IOptions<ResponseCachingOptions> options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options.Value;
        }

        public IEnumerable<string> CreateLookupVaryByKeys(ResponseCachingContext context)
        {
            yield return CreateStorageVaryByKey(context);
        }

        // GET<delimiter>SCHEME<delimiter>HOST:PORT/PATHBASE/PATH
        public string CreateBaseKey(ResponseCachingContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var request = context.HttpContext.Request;

            var keyLength = CalculateKeyLength();

            var baseKey = string.Create(keyLength, request, (chars, httpRequest) =>
            {
                var position = 0;

                httpRequest.Method.AsSpan().ToUpperInvariant(chars);
                position += httpRequest.Method.Length;

                chars[position++] = KeyDelimiter;

                httpRequest.Scheme.AsSpan().ToUpperInvariant(chars.Slice(position));
                position += httpRequest.Scheme.Length;

                chars[position++] = KeyDelimiter;

                httpRequest.Host.Value.AsSpan().ToUpperInvariant(chars.Slice(position));
                position += httpRequest.Host.Value.Length;

                var pathBaseSpan = httpRequest.PathBase.Value.AsSpan();
                var pathSpan = httpRequest.Path.Value.AsSpan();

                if (_options.UseCaseSensitivePaths)
                {
                    pathBaseSpan.ToUpperInvariant(chars.Slice(position));
                    position += pathBaseSpan.Length;

                    pathSpan.ToUpperInvariant(chars.Slice(position));
                }
                else
                {
                    pathBaseSpan.CopyTo(chars.Slice(position));
                    position += pathBaseSpan.Length;

                    pathSpan.CopyTo(chars.Slice(position));
                }
            });

            return baseKey;

            int CalculateKeyLength() =>
                request.Method.Length +
                1 + // char for KeyDelimiter
                request.Scheme.Length +
                1 + // char for KeyDelimiter
                request.Host.Value.Length +
                request.PathBase.Value.Length +
                request.Path.Value.Length;
        }

        // BaseKey<delimiter>H<delimiter>HeaderName=HeaderValue<delimiter>Q<delimiter>QueryName=QueryValue1<subdelimiter>QueryValue2
        public string CreateStorageVaryByKey(ResponseCachingContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var varyByRules = context.CachedVaryByRules;
            if (varyByRules == null)
            {
                throw new InvalidOperationException(
                    $"{nameof(CachedVaryByRules)} must not be null on the {nameof(ResponseCachingContext)}");
            }

            var hasHeaders = !StringValues.IsNullOrEmpty(varyByRules.Headers);
            var hasQueryKeys = !StringValues.IsNullOrEmpty(varyByRules.QueryKeys);

            if (!hasHeaders && !hasQueryKeys)
            {
                return varyByRules.VaryByKeyPrefix;
            }

            var request = context.HttpContext.Request;

            var keyLength = varyByRules.VaryByKeyPrefix.Length;

            int headersCount, queryKeysCount;

            // calculate header length
            if (hasHeaders && (headersCount = varyByRules.Headers.Count) > 0) // might not need the count. Can StringValues be non-null/empty + have 0 items?
            {
                keyLength += 2; // chars for KeyDelimiter and group separator for the header segment of the cache key

                var requestHeaders = context.HttpContext.Request.Headers;

                for (var i = headersCount - 1; i >= 0; i++) // does this limit a bounds check?
                {
                    var header = varyByRules.Headers[i];
                    var headerValues = requestHeaders[header];

                    keyLength += header.Length + 2; // includes delimiter, header key and equals sign.

                    for (var j = headerValues.Count - 1; j >= 0; j--) // does this limit a bounds check?
                    {
                        keyLength += headerValues[j].Length;
                    }
                }
            }

            bool specialKey; // naming!

            // calculate query keys length
            if (hasQueryKeys && (queryKeysCount = varyByRules.QueryKeys.Count) > 0) // might not need the count. Can StringValues be non-null/empty + have 0 items?
            {
                keyLength += 2; // chars for KeyDelimiter and group separator for the header segment of the cache key

                specialKey = varyByRules.QueryKeys.Count == 1 &&
                             string.Equals(varyByRules.QueryKeys[0], "*", StringComparison.Ordinal);

                if (specialKey)
                {
                    //var queryCount = context.HttpContext.Request.Query.Count;

                    foreach (var (key, value) in context.HttpContext.Request.Query) // foreach here as it's string indexed
                    {
                        keyLength += key.Length + 2; // includes delimiter, query key and equals sign.

                        for (var i = value.Count - 1; i >= 0; i--) // does this limit a bounds check?
                        {
                            keyLength += value[i].Length;
                            if (i != 0) keyLength++;
                        }
                    }
                }
                else
                {
                    for (var i = queryKeysCount - 1; i >= 0; i++) // does this limit a bounds check?
                    {
                        var queryKey = varyByRules.QueryKeys[i];
                        var queryKeyValues = context.HttpContext.Request.Query[queryKey];

                        keyLength += queryKey.Length + 2; // includes delimiter, header key and equals sign.

                        for (var j = queryKeyValues.Count - 1; j >= 0; j--) // does this limit a bounds check?
                        {
                            keyLength += queryKeyValues[j].Length;
                            if (j != 0) keyLength++;
                        }
                    }
                }
            }

            var varyByKey = string.Create(keyLength, varyByRules, (chars, rules) =>
            {
                // TODO
            });

            return varyByKey;
        }
    }

    internal class ResponseCachingKeyProvider : IResponseCachingKeyProvider
    {
        // Use the record separator for delimiting components of the cache key to avoid possible collisions
        private static readonly char KeyDelimiter = '\x1e';
        // Use the unit separator for delimiting subcomponents of the cache key to avoid possible collisions
        private static readonly char KeySubDelimiter = '\x1f';

        private readonly ObjectPool<StringBuilder> _builderPool;
        private readonly ResponseCachingOptions _options;

        internal ResponseCachingKeyProvider(ObjectPoolProvider poolProvider, IOptions<ResponseCachingOptions> options)
        {
            if (poolProvider == null)
            {
                throw new ArgumentNullException(nameof(poolProvider));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _builderPool = poolProvider.CreateStringBuilderPool();
            _options = options.Value;
        }

        public IEnumerable<string> CreateLookupVaryByKeys(ResponseCachingContext context)
        {
            return new string[] { CreateStorageVaryByKey(context) };
        }

        // GET<delimiter>SCHEME<delimiter>HOST:PORT/PATHBASE/PATH
        public string CreateBaseKey(ResponseCachingContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var request = context.HttpContext.Request;
            var builder = _builderPool.Get();

            try
            {
                builder
                    .AppendUpperInvariant(request.Method)
                    .Append(KeyDelimiter)
                    .AppendUpperInvariant(request.Scheme)
                    .Append(KeyDelimiter)
                    .AppendUpperInvariant(request.Host.Value);

                if (_options.UseCaseSensitivePaths)
                {
                    builder
                        .Append(request.PathBase.Value)
                        .Append(request.Path.Value);
                }
                else
                {
                    builder
                        .AppendUpperInvariant(request.PathBase.Value)
                        .AppendUpperInvariant(request.Path.Value);
                }

                return builder.ToString();
            }
            finally
            {
                _builderPool.Return(builder);
            }
        }

        // BaseKey<delimiter>H<delimiter>HeaderName=HeaderValue<delimiter>Q<delimiter>QueryName=QueryValue1<subdelimiter>QueryValue2
        public string CreateStorageVaryByKey(ResponseCachingContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var varyByRules = context.CachedVaryByRules;
            if (varyByRules == null)
            {
                throw new InvalidOperationException($"{nameof(CachedVaryByRules)} must not be null on the {nameof(ResponseCachingContext)}");
            }

            if (StringValues.IsNullOrEmpty(varyByRules.Headers) && StringValues.IsNullOrEmpty(varyByRules.QueryKeys))
            {
                return varyByRules.VaryByKeyPrefix;
            }

            var request = context.HttpContext.Request;
            var builder = _builderPool.Get();

            try
            {
                // Prepend with the Guid of the CachedVaryByRules
                builder.Append(varyByRules.VaryByKeyPrefix);

                // Vary by headers
                var headersCount = varyByRules?.Headers.Count ?? 0;
                if (headersCount > 0)
                {
                    // Append a group separator for the header segment of the cache key
                    builder.Append(KeyDelimiter)
                        .Append('H');

                    var requestHeaders = context.HttpContext.Request.Headers;
                    for (var i = 0; i < headersCount; i++)
                    {
                        var header = varyByRules.Headers[i];
                        var headerValues = requestHeaders[header];
                        builder.Append(KeyDelimiter)
                            .Append(header)
                            .Append('=');

                        var headerValuesArray = headerValues.ToArray();
                        Array.Sort(headerValuesArray, StringComparer.Ordinal);

                        for (var j = 0; j < headerValuesArray.Length; j++)
                        {
                            builder.Append(headerValuesArray[j]);
                        }
                    }
                }

                // Vary by query keys
                if (varyByRules?.QueryKeys.Count > 0)
                {
                    // Append a group separator for the query key segment of the cache key
                    builder.Append(KeyDelimiter)
                        .Append('Q');

                    if (varyByRules.QueryKeys.Count == 1 && string.Equals(varyByRules.QueryKeys[0], "*", StringComparison.Ordinal))
                    {
                        // Vary by all available query keys
                        var queryArray = context.HttpContext.Request.Query.ToArray();
                        // Query keys are aggregated case-insensitively whereas the query values are compared ordinally.
                        Array.Sort(queryArray, QueryKeyComparer.OrdinalIgnoreCase);

                        for (var i = 0; i < queryArray.Length; i++)
                        {
                            builder.Append(KeyDelimiter)
                                .AppendUpperInvariant(queryArray[i].Key)
                                .Append('=');

                            var queryValueArray = queryArray[i].Value.ToArray();
                            Array.Sort(queryValueArray, StringComparer.Ordinal);

                            for (var j = 0; j < queryValueArray.Length; j++)
                            {
                                if (j > 0)
                                {
                                    builder.Append(KeySubDelimiter);
                                }

                                builder.Append(queryValueArray[j]);
                            }
                        }
                    }
                    else
                    {
                        for (var i = 0; i < varyByRules.QueryKeys.Count; i++)
                        {
                            var queryKey = varyByRules.QueryKeys[i];
                            var queryKeyValues = context.HttpContext.Request.Query[queryKey];
                            builder.Append(KeyDelimiter)
                                .Append(queryKey)
                                .Append('=');

                            var queryValueArray = queryKeyValues.ToArray();
                            Array.Sort(queryValueArray, StringComparer.Ordinal);

                            for (var j = 0; j < queryValueArray.Length; j++)
                            {
                                if (j > 0)
                                {
                                    builder.Append(KeySubDelimiter);
                                }

                                builder.Append(queryValueArray[j]);
                            }
                        }
                    }
                }

                return builder.ToString();
            }
            finally
            {
                _builderPool.Return(builder);
            }
        }

        private class QueryKeyComparer : IComparer<KeyValuePair<string, StringValues>>
        {
            private readonly StringComparer _stringComparer;

            public static QueryKeyComparer OrdinalIgnoreCase { get; } = new QueryKeyComparer(StringComparer.OrdinalIgnoreCase);

            public QueryKeyComparer(StringComparer stringComparer)
            {
                _stringComparer = stringComparer;
            }

            public int Compare(KeyValuePair<string, StringValues> x, KeyValuePair<string, StringValues> y) => _stringComparer.Compare(x.Key, y.Key);
        }
    }
}
