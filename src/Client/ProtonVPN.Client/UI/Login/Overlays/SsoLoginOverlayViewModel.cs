/*
 * Copyright (c) 2023 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using ProtonVPN.Api.Contracts;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Bases.ViewModels;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.Logic.Auth.Contracts;
using ProtonVPN.Client.Logic.Auth.Contracts.Enums;
using ProtonVPN.Client.Logic.Auth.Contracts.Models;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppLogs;

namespace ProtonVPN.Client.UI.Login.Overlays;

public partial class SsoLoginOverlayViewModel :  OverlayViewModelBase<IMainWindowOverlayActivator>
{
    private readonly IUserAuthenticator _userAuthenticator;
    private readonly ISettings _settings;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _ssoHttpClient;
    private readonly Regex _uriRegex = new(".+\\/sso\\/login#token=(?<token>.+)&uid=(?<uid>.+)");

    [ObservableProperty]
    private bool _isLoadingPage;

    [ObservableProperty]
    private WebView2? _ssoWebView;

    private string? _ssoResponseToken;
    private Uri? _ssoBootstrapUri;

    public SsoLoginOverlayViewModel(
        IMainWindowOverlayActivator overlayActivator,
        IUserAuthenticator userAuthenticator,
        ISettings settings,
        IConfiguration configuration,
        ISsoWebViewHttpClientFactory ssoWebViewHttpClientFactory,
        IViewModelHelper viewModelHelper)
        : base(overlayActivator, viewModelHelper)
    {
        _userAuthenticator = userAuthenticator;
        _settings = settings;
        _configuration = configuration;
        _ssoHttpClient = ssoWebViewHttpClientFactory.GetHttpClient();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        CleanupWebView();
    }

    private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Intercept redirection to the account login page and extract the response token from the Uri
        Match match = _uriRegex.Match(e.Uri);
        if (match.Success && match.Groups["uid"]?.Value == _settings.UnauthUniqueSessionId)
        {
            // Cancel navigation and extract response token
            e.Cancel = true;

            _ssoResponseToken = match.Groups["token"]?.Value;

            OverlayActivator.CloseCurrentOverlay();
        }
    }

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsLoadingPage = false;
    }

    public async Task<AuthResult> AuthenticateAsync(string ssoChallengeToken)
    {
        if (string.IsNullOrEmpty(ssoChallengeToken))
        {
            return AuthResult.Fail(AuthError.SsoAuthFailed);
        }

        _ssoResponseToken = null;
        IsLoadingPage = true;
        await InitializeWebViewAsync(ssoChallengeToken);
        await OverlayActivator.ShowOverlayAsync(this);
        return await _userAuthenticator.CompleteSsoAuthAsync(_ssoResponseToken ?? string.Empty);
    }

    private async Task InitializeWebViewAsync(string ssoChallengeToken)
    {
        try
        {
            CreateWebView();

            WebView2? webView = SsoWebView;
            if (webView is null)
            {
                return;
            }

            if (webView.CoreWebView2 == null)
            {
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _configuration.WebViewFolder, null);

                // WinUI3 does not support creating CoreWebView2 with custom environment. Set environment variable instead.
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", environment.UserDataFolder);

                await webView.EnsureCoreWebView2Async();

                if (webView.CoreWebView2 is null || SsoWebView != webView)
                {
                    return;
                }

                webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            }

            // Delete cookies to prevent auto authentication after a first successful login.
            webView.CoreWebView2.CookieManager.DeleteAllCookies();

            Uri requestUri = new(new Uri(_configuration.Urls.ApiUrl), $"auth/sso/{ssoChallengeToken}");
            _ssoBootstrapUri = requestUri;

            webView.CoreWebView2.AddWebResourceRequestedFilter(
                requestUri.AbsoluteUri,
                CoreWebView2WebResourceContext.Document);
            webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

            webView.CoreWebView2.Navigate(requestUri.AbsoluteUri);
        }
        catch (Exception e)
        {
            Logger.Error<AppLog>($"Error occured when trying to navigate to the SSO login page.", e);
        }
    }

    private async void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_ssoBootstrapUri is null ||
            !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? requestUri) ||
            requestUri != _ssoBootstrapUri)
        {
            return;
        }

        CoreWebView2Deferral deferral = args.GetDeferral();
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, _ssoBootstrapUri);
            request.Headers.TryAddWithoutValidation("x-pm-uid", _settings.UnauthUniqueSessionId);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.UnauthAccessToken}");

            using HttpResponseMessage response = await _ssoHttpClient.SendAsync(request);
            byte[] content = await response.Content.ReadAsByteArrayAsync();
            args.Response = CreateWebResourceResponse(sender, response, content);
        }
        catch (Exception e)
        {
            Logger.Error<AppLog>("Failed to fetch the SSO bootstrap through the pinned HTTP client.", e);
            args.Response = sender.Environment.CreateWebResourceResponse(
                new MemoryStream().AsRandomAccessStream(),
                502,
                "Bad Gateway",
                string.Empty);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static CoreWebView2WebResourceResponse CreateWebResourceResponse(
        CoreWebView2 webView,
        HttpResponseMessage response,
        byte[] content)
    {
        CoreWebView2WebResourceResponse webViewResponse = webView.Environment.CreateWebResourceResponse(
            new MemoryStream(content).AsRandomAccessStream(),
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            string.Empty);

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
        {
            foreach (string value in header.Value)
            {
                webViewResponse.Headers.AppendHeader(header.Key, value);
            }
        }

        return webViewResponse;
    }

    private void CreateWebView()
    {
        CleanupWebView();
        WebView2 webView = new() { VerticalAlignment = VerticalAlignment.Stretch };
        webView.NavigationStarting += OnNavigationStarting;
        webView.NavigationCompleted += OnNavigationCompleted;
        SsoWebView = webView;
    }

    private void CleanupWebView()
    {
        if (SsoWebView is null)
        {
            return;
        }

        WebView2 webView = SsoWebView;
        SsoWebView = null;
        _ssoBootstrapUri = null;

        webView.NavigationStarting -= OnNavigationStarting;
        webView.NavigationCompleted -= OnNavigationCompleted;

        if (webView.CoreWebView2 is not null)
        {
            webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
        }

        try
        {
            webView.Close();
        }
        catch (Exception ex)
        {
            Logger.Error<AppLog>("Failed to close WebView2.", ex);
        }
    }
}
