// <copyright file="ServiceCollectionExtensions.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp.Web.DependencyInjection
{
    using System;
    using ImageSharp.Web.Caching;
    using ImageSharp.Web.Commands;
    using ImageSharp.Web.Middleware;
    using ImageSharp.Web.Processors;
    using ImageSharp.Web.Services;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Extension methods for <see cref="IServiceCollection"/> to simplify middleware service registration.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers ImageSharp with the default services
        /// </summary>
        /// <param name="services">The contract for the collection of service descriptors</param>
        /// <returns><see cref="IServiceCollection"/></returns>
        public static IServiceCollection AddImageSharp(this IServiceCollection services)
        {
            Guard.NotNull(services, nameof(services));

            services.TryAddSingleton<IUriParser, QueryCollectionUriParser>();
            services.TryAddSingleton<IImageCache, PhysicalFileSystemCache>();

            // TODO: Make these optional - with this setup you can easily add additional ones, but not remove these
            // Need to think about ordering of these default ones too
            services.TryAddEnumerable(
                new ServiceDescriptor(typeof(IImageService), typeof(PhysicalFileImageService), ServiceLifetime.Singleton));
            services.TryAddEnumerable(
                new ServiceDescriptor(typeof(IImageWebProcessor), typeof(ResizeWebProcessor), ServiceLifetime.Singleton));

            return services.AddSingleton<IConfigureOptions<ImageSharpMiddlewareOptions>, ImageSharpConfiguration>();
        }

        /// <summary>
        /// Registers ImageSharp with the default services
        /// </summary>
        /// <param name="services">The contract for the collection of service descriptors</param>
        /// <param name="setupAction">An <see cref="Action{ImageSharpMiddlewareOptions}"/> to configure the provided <see cref="ImageSharpMiddlewareOptions"/>.</param>
        /// <returns><see cref="IServiceCollection"/></returns>
        public static IServiceCollection AddImageSharp(this IServiceCollection services, Action<ImageSharpMiddlewareOptions> setupAction)
        {
            Guard.NotNull(services, nameof(services));
            Guard.NotNull(setupAction, nameof(setupAction));

            services.AddImageSharp();
            services.Configure(setupAction);

            return services;
        }
    }
}