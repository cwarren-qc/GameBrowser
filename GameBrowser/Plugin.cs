﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using GameBrowser.Api;
using GameBrowser.Configuration;
using GameBrowser.Providers.EmuMovies;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace GameBrowser
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public readonly SemaphoreSlim TgdbSemiphore = new SemaphoreSlim(5, 5);
        public readonly SemaphoreSlim EmuMoviesSemiphore = new SemaphoreSlim(5, 5);

        private const string EmuMoviesApiKey = @"4D8621EE919A13EB6E89B7EDCA6424FC33D6";

        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly GameBrowserUriService _gameBrowserUriService;

        /// <summary>
        /// Gets the name of the plugin
        /// </summary>
        /// <value>The name.</value>
        public override string Name
        {
            get { return "GameBrowser"; }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "GameBrowser",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "GbMetaConfigurationPage",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.metaConfig.html"
                }
            };
        }

        private Guid _id = new Guid("4C2FDA1C-FD5E-433A-AD2B-718E0B73E9A9");
        public override Guid Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Gets the plugin's configuration
        /// </summary>
        /// <value>The configuration.</value>
        public new PluginConfiguration Configuration
        {
            get
            {
                return base.Configuration;
            }
            set
            {
                base.Configuration = value;
            }
        }



        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; }



        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin" /> class.
        /// </summary>
        public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, IUserManager userManager, ILogManager logManager, IHttpClient httpClient)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger("GameBrowser");
            _httpClient = httpClient;

            _gameBrowserUriService = new GameBrowserUriService(_logger, libraryManager);
        }

        private DateTime _keyDate;
        private string _emuMoviesToken;
        private readonly SemaphoreSlim _emuMoviesApiKeySemaphore = new SemaphoreSlim(1, 1);
        private const double TokenExpirationMinutes = 9.5;

        private bool IsTokenValid
        {
            get
            {
                return !String.IsNullOrEmpty(_emuMoviesToken) &&
                       (DateTime.Now - _keyDate).TotalMinutes <= TokenExpirationMinutes;
            }
        }

        /// <summary>
        /// Gets the EmuMovies token.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.String}.</returns>
        public async Task<string> GetEmuMoviesToken(CancellationToken cancellationToken)
        {
            if (!IsTokenValid)
            {
                await _emuMoviesApiKeySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Check if it was set by another thread while waiting
                if (IsTokenValid)
                {
                    _emuMoviesApiKeySemaphore.Release();
                    return _emuMoviesToken;
                }

                try
                {
                    var token = await GetEmuMoviesTokenInternal(cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(token))
                    {
                        _keyDate = DateTime.Now;
                    }

                    _emuMoviesToken = token;
                }
                catch (Exception ex)
                {
                    // Log & throw
                    _logger.ErrorException("Error getting token from EmuMovies", ex);

                    throw;
                }
                finally
                {
                    _emuMoviesApiKeySemaphore.Release();
                }
            }

            return _emuMoviesToken;
        }

        /// <summary>
        /// Gets the emu db token internal.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.String}.</returns>
        private async Task<string> GetEmuMoviesTokenInternal(CancellationToken cancellationToken)
        {
            var url = String.Format(EmuMoviesUrls.Login, Instance.Configuration.EmuMoviesUsername, Instance.Configuration.EmuMoviesPassword, EmuMoviesApiKey);

            try
            {
                using (var response = await _httpClient.SendAsync(new HttpRequestOptions
                {

                    Url = url,
                    CancellationToken = cancellationToken,
                    ResourcePool = Plugin.Instance.EmuMoviesSemiphore

                }, "GET").ConfigureAwait(false))
                {
                    using (var stream = response.Content)
                    {
                        var doc = new XmlDocument();
                        doc.Load(stream);

                        if (doc.HasChildNodes)
                        {
                            var resultNode = doc.SelectSingleNode("Results/Result");

                            if (resultNode != null && resultNode.Attributes != null)
                            {
                                var sessionId = resultNode.Attributes["Session"].Value;

                                if (sessionId != null)
                                    return sessionId;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.ErrorException("Error retrieving EmuMovies token", e);
            }

            return "";
        }
    }
}
