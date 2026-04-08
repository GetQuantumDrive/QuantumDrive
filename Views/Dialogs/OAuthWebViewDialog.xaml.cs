using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace quantum_drive.Views.Dialogs;

internal enum OAuthFlowKind { Code, Token }

public sealed partial class OAuthWebViewDialog : ContentDialog
{
    private readonly string _authUrl;
    private readonly string _redirectUriPrefix;
    private readonly OAuthFlowKind _flowKind;

    private TaskCompletionSource<string>? _codeTcs;
    private TaskCompletionSource<Dictionary<string, string>>? _tokenTcs;
    private CancellationTokenRegistration _ctReg;

    private OAuthWebViewDialog(string authUrl, string redirectUriPrefix, OAuthFlowKind flowKind)
    {
        InitializeComponent();
        _authUrl           = authUrl;
        _redirectUriPrefix = redirectUriPrefix;
        _flowKind          = flowKind;

        AuthWebView.Loaded             += (_, _) => AuthWebView.Source = new Uri(_authUrl);
        AuthWebView.NavigationStarting += OnNavigationStarting;
        AuthWebView.NavigationCompleted += OnNavigationCompleted;
        Closed += OnClosed;
    }

    // ── Public factory methods ─────────────────────────────────────────────────

    public static async Task<string> GetAuthCodeAsync(
        string authUrl, string redirectUriPrefix, XamlRoot xamlRoot, CancellationToken ct)
    {
        var dialog = new OAuthWebViewDialog(authUrl, redirectUriPrefix, OAuthFlowKind.Code);
        dialog.XamlRoot = xamlRoot;
        dialog._codeTcs  = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog._ctReg    = ct.Register(() => dialog.Hide());
        _ = dialog.ShowAsync();
        return await dialog._codeTcs.Task;
    }

    public static async Task<Dictionary<string, string>> GetTokenParamsAsync(
        string authUrl, string redirectUriPrefix, XamlRoot xamlRoot, CancellationToken ct)
    {
        var dialog = new OAuthWebViewDialog(authUrl, redirectUriPrefix, OAuthFlowKind.Token);
        dialog.XamlRoot  = xamlRoot;
        dialog._tokenTcs = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog._ctReg    = ct.Register(() => dialog.Hide());
        _ = dialog.ShowAsync();
        return await dialog._tokenTcs.Task;
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(_redirectUriPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;

        var query  = new Uri(e.Uri).Query;
        var params_ = ParseQueryString(query);

        if (params_.TryGetValue("error", out var error))
        {
            Fail(new InvalidOperationException($"Authorization failed: {error}"));
            return;
        }

        if (_flowKind == OAuthFlowKind.Code)
        {
            if (!params_.TryGetValue("code", out var code))
                Fail(new InvalidOperationException("Authorization redirect did not include a code."));
            else
                _codeTcs!.TrySetResult(code);
        }
        else
        {
            if (!params_.TryGetValue("access_token", out _))
                Fail(new InvalidOperationException("Authorization redirect did not include an access_token."));
            else
                _tokenTcs!.TrySetResult(params_);
        }

        Hide();
    }

    private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // The cancelled navigation to 127.0.0.1 may fire a failed NavigationCompleted — ignore it.
        if (!e.IsSuccess && sender.Source?.Host != "127.0.0.1")
        {
            Fail(new InvalidOperationException(
                $"Failed to load authorization page (status: {e.WebErrorStatus})."));
            return;
        }

        LoadingRing.IsActive   = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        AuthWebView.Visibility = Visibility.Visible;
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs e)
    {
        _codeTcs?.TrySetCanceled();
        _tokenTcs?.TrySetCanceled();
        _ctReg.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void Fail(Exception ex)
    {
        _codeTcs?.TrySetException(ex);
        _tokenTcs?.TrySetException(ex);
        Hide();
    }

    /// <summary>Parses a URL query string (with or without leading '?') into a dictionary.</summary>
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q    = query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key = Uri.UnescapeDataString(pair[..idx]);
            var val = Uri.UnescapeDataString(pair[(idx + 1)..]);
            dict.TryAdd(key, val);
        }
        return dict;
    }
}
