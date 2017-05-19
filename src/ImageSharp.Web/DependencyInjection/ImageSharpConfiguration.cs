// <copyright file="ImageSharpConfiguration.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp.Web.DependencyInjection
{
    using System.Collections.Generic;

    using ImageSharp.Web.Caching;
    using ImageSharp.Web.Commands;
    using ImageSharp.Web.Middleware;
    using ImageSharp.Web.Processors;
    using ImageSharp.Web.Services;

    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Provides default configuration settings to be consumeed by the middleware
    /// </summary>
    public class ImageSharpConfiguration : IConfigureOptions<ImageSharpMiddlewareOptions>
    {
        /// <inheritdoc/>
        public void Configure(ImageSharpMiddlewareOptions options)
        {
            options.Configuration = Configuration.Default;
            options.MaxCacheDays = 365;
        }
    }
}