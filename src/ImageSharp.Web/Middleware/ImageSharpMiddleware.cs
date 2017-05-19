﻿// <copyright file="ImageSharpMiddleware.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp.Web.Middleware
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using ImageSharp.Memory;
    using ImageSharp.Web.Caching;
    using ImageSharp.Web.Commands;
    using ImageSharp.Web.Helpers;
    using ImageSharp.Web.Processors;
    using ImageSharp.Web.Services;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Middleware for handling the processing of images via a URI querystring API.
    /// </summary>
    public class ImageSharpMiddleware
    {
        /// <summary>
        /// The function processing the Http request.
        /// </summary>
        private readonly RequestDelegate next;

        /// <summary>
        /// The configuration options
        /// </summary>
        private readonly ImageSharpMiddlewareOptions options;

        /// <summary>
        /// The hosting environment the application is running in.
        /// </summary>
        private readonly IHostingEnvironment environment;

        /// <summary>
        /// The type used for performing logging.
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// The URI parser for parsing commands.
        /// </summary>
        private readonly IUriParser uriParser;

        /// <summary>
        /// The collection of image services.
        /// </summary>
        private readonly IList<IImageService> services;

        /// <summary>
        /// The collection of image processors.
        /// </summary>
        private readonly IList<IImageWebProcessor> processors;

        /// <summary>
        /// The service for caching images.
        /// </summary>
        private readonly IImageCache cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageSharpMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next middleware in the pipeline</param>
        /// <param name="environment">The <see cref="IHostingEnvironment"/> used by this middleware</param>
        /// <param name="options">The configuration options</param>
        /// <param name="loggerFactory">An <see cref="ILoggerFactory"/> instance used to create loggers</param>
        /// <param name="uriParser">The URI parser for parsing commands</param>
        /// <param name="services">The collection of image services</param>
        /// <param name="processors">The collection of image processors</param>
        /// <param name="cache">The service for caching images</param>
        public ImageSharpMiddleware(
            RequestDelegate next,
            IHostingEnvironment environment,
            IOptions<ImageSharpMiddlewareOptions> options,
            ILoggerFactory loggerFactory,
            IUriParser uriParser,
            IEnumerable<IImageService> services,
            IEnumerable<IImageWebProcessor> processors,
            IImageCache cache)
        {
            Guard.NotNull(next, nameof(next));
            Guard.NotNull(environment, nameof(environment));
            Guard.NotNull(options, nameof(options));
            Guard.NotNull(loggerFactory, nameof(loggerFactory));
            Guard.NotNull(uriParser, nameof(uriParser));
            Guard.NotNull(services, nameof(services));
            Guard.NotNull(processors, nameof(processors));
            Guard.NotNull(cache, nameof(cache));

            this.next = next;
            this.environment = environment;
            this.options = options.Value;
            this.logger = loggerFactory.CreateLogger<ImageSharpMiddleware>();
            this.uriParser = uriParser;
            this.services = services.ToList();
            this.processors = processors.ToList();
            this.cache = cache;
        }

        /// <summary>
        /// Performs operations upon the current request.
        /// </summary>
        /// <param name="context">The current HTTP request context</param>
        /// <returns>The <see cref="Task"/></returns>
        public async Task Invoke(HttpContext context)
        {
            // TODO: Parse the request path and application path?
            PathString path = context.Request.Path;
            PathString applicationPath = context.Request.PathBase;
            IDictionary<string, string> commands = this.uriParser.ParseUriCommands(context);

            if (!commands.Any())
            {
                // Nothing to do. call the next delegate/middleware in the pipeline
                await this.next(context);
                return;
            }

            // Get the correct service for the request.
            IImageService service = await this.AssignServiceAsync(context, path, applicationPath);

            if (service == null)
            {
                // Nothing to do. call the next delegate/middleware in the pipeline
                await this.next(context);
                return;
            }

            // TODO: Check querystring against list of known parameters. Only continue if valid.
            IImageCache cache = this.cache;

            // TODO: Add event handler to allow augmenting the querystring value.
            string uri = path + QueryString.Create(commands);
            string key = CacheHash.Create(uri, this.options.Configuration);

            CachedInfo info = await cache.IsExpiredAsync(key, DateTime.UtcNow.AddDays(-this.options.MaxCacheDays));

            var imageContext = new ImageContext(context, this.options);

            if (!info.Expired)
            {
                // Image is a cached image. Return the correct response now.
                await this.SendResponse(imageContext, cache, key, info.LastModifiedUtc, null, (int)info.Length);
                return;
            }

            // Not cached? Let's get it from the image service.
            byte[] inBuffer = await service.ResolveImageAsync(context, this.logger, path);
            byte[] outBuffer = null;
            MemoryStream inStream = null;
            MemoryStream outStream = null;
            try
            {
                // No allocations here for inStream since we are passing the buffer.
                // TODO: How to prevent the allocation in outStream? Passing a pooled buffer won't let stream grow if needed.
                inStream = new MemoryStream(inBuffer);
                outStream = new MemoryStream();
                using (var image = Image.Load(this.options.Configuration, inStream))
                {
                    image.Process(this.logger, this.processors, commands)
                         .Save(outStream);
                }

                // TODO: Add an event for post processing the image.
                // Copy the outstream to the pooled buffer.
                int outLength = (int)outStream.Position + 1;
                outStream.Position = 0;
                outBuffer = BufferDataPool.Rent(outLength);
                await outStream.ReadAsync(outBuffer, 0, outLength);

                DateTimeOffset cachedDate = await cache.SetAsync(key, outBuffer, outLength);
                await this.SendResponse(imageContext, cache, key, cachedDate, outBuffer, outLength);
            }
            catch (Exception ex)
            {
                // TODO: Create an extension that does this interpolation globally.
                this.logger.LogCritical($"{ex.Message}{Environment.NewLine}StackTrace:{ex.StackTrace}");
            }
            finally
            {
                inStream?.Dispose();
                outStream?.Dispose();

                // Buffer should have been rented in IImageService
                BufferDataPool.Return(inBuffer);
                BufferDataPool.Return(outBuffer);
            }
        }

        private async Task SendResponse(ImageContext imageContext, IImageCache cache, string key, DateTimeOffset lastModified, byte[] buffer, int length)
        {
            imageContext.ComprehendRequestHeaders(lastModified, length);

            string contentType = FormatHelpers.GetContentType(this.options.Configuration, key);

            switch (imageContext.GetPreconditionState())
            {
                case ImageContext.PreconditionState.Unspecified:
                case ImageContext.PreconditionState.ShouldProcess:
                    if (imageContext.IsHeadRequest())
                    {
                        await imageContext.SendStatusAsync(ResponseConstants.Status200Ok, contentType);
                    }

                    // logger.LogFileServed(fileContext.SubPath, fileContext.PhysicalPath);
                    if (buffer == null)
                    {
                        // We're pulling the buffer from the cache. This should be pooled.
                        CachedBuffer cachedBuffer = await cache.GetAsync(key);
                        await imageContext.SendAsync(contentType, cachedBuffer.Buffer, cachedBuffer.Length);
                        BufferDataPool.Return(cachedBuffer.Buffer);
                    }
                    else
                    {
                        await imageContext.SendAsync(contentType, buffer, length);
                    }

                    break;

                case ImageContext.PreconditionState.NotModified:
                    // _logger.LogPathNotModified(fileContext.SubPath);
                    await imageContext.SendStatusAsync(ResponseConstants.Status304NotModified, contentType);
                    break;
                case ImageContext.PreconditionState.PreconditionFailed:
                    // _logger.LogPreconditionFailed(fileContext.SubPath);
                    await imageContext.SendStatusAsync(ResponseConstants.Status412PreconditionFailed, contentType);
                    break;
                default:
                    var exception = new NotImplementedException(imageContext.GetPreconditionState().ToString());
                    Debug.Fail(exception.ToString());
                    throw exception;
            }
        }

        private async Task<IImageService> AssignServiceAsync(HttpContext context, string uri, string applicationPath)
        {
            // Remove the Application Path from the Request.Path.
            // This allows applications running on localhost as sub applications to work.
            string path = uri.TrimStart(applicationPath.ToCharArray());
            foreach (IImageService service in this.services)
            {
                string key = service.Key;
                if (string.IsNullOrWhiteSpace(key) || !path.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await service.IsValidRequestAsync(context, this.logger, path))
                {
                    return service;
                }
            }

            // Return the file based service.
            Type physicalType = typeof(PhysicalFileImageService);

            IImageService physicalService = this.services.FirstOrDefault(s => s.GetType() == physicalType);
            if (physicalService != null)
            {
                if (await physicalService.IsValidRequestAsync(context, this.logger, path))
                {
                    return physicalService;
                }
            }

            // Return the next unprefixed service.
            foreach (IImageService service in this.services.Where(s => string.IsNullOrEmpty(s.Key) && s.GetType() != physicalType))
            {
                if (await service.IsValidRequestAsync(context, this.logger, path))
                {
                    return service;
                }
            }

            return null;
        }
    }
}