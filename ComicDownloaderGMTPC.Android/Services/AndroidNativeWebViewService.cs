using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Webkit;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

/// <summary>
/// Hiện thực dịch vụ Native WebView Headless chuyên dụng cho Android.
/// Tận dụng trực tiếp Android System WebView (có sẵn trong hệ điều hành, 0MB tăng thêm cho APK)
/// để tải ngầm trang web, thực thi JavaScript, bóc tách DOM và tự động vượt rào cản Cloudflare / Turnstile.
/// </summary>
public class AndroidNativeWebViewService : INativeWebViewService
{
    private readonly Func<Context> _contextProvider;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsAvailable => true;
    public string EngineName => "Android System WebView";

    public AndroidNativeWebViewService(Func<Context>? contextProvider = null)
    {
        _contextProvider = contextProvider ?? (() => MainActivity.CurrentInstance ?? global::Android.App.Application.Context);
    }

    /// <summary>
    /// Lấy chuỗi Cookie đã được lưu trữ trong Android CookieManager cho URL chỉ định.
    /// Giúp đồng bộ Cookie clearance (cf_clearance) sau khi vượt Cloudflare về HttpClient C#.
    /// </summary>
    public string? GetCookies(string url)
    {
        try
        {
            return CookieManager.Instance?.GetCookie(url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tải HTML của trang web chạy ngầm (headless) có thực thi JavaScript và vượt Cloudflare trên Android.
    /// </summary>
    public async Task<string?> FetchHtmlAsync(string url, int timeoutSeconds = 45, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecuteOnWebViewAsync(url, isJson: false, timeoutSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tải dữ liệu JSON chạy ngầm (headless) qua Android System WebView.
    /// </summary>
    public async Task<string?> FetchJsonAsync(string url, int timeoutSeconds = 15, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecuteOnWebViewAsync(url, isJson: true, timeoutSeconds, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<string?> ExecuteOnWebViewAsync(string url, bool isJson, int timeoutSeconds, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainHandler = new Handler(Looper.MainLooper!);

        mainHandler.Post(() =>
        {
            WebView? webView = null;
            CancellationTokenRegistration? reg = null;
            Timer? pollTimer = null;
            bool isCompleted = false;

            void Cleanup()
            {
                if (isCompleted) return;
                isCompleted = true;

                try { pollTimer?.Dispose(); } catch { }
                try { reg?.Dispose(); } catch { }

                if (webView != null)
                {
                    try
                    {
                        webView.StopLoading();
                        webView.ClearHistory();
                        webView.Destroy();
                    }
                    catch { }
                    webView = null;
                }
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                Context ctx = _contextProvider();
                webView = new WebView(ctx);

                // Cấu hình trình duyệt tối ưu: bật JS, lưu trữ DOM, chấp nhận Cookie
                var settings = webView.Settings;
                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.DatabaseEnabled = true;
                settings.LoadsImagesAutomatically = true;
                settings.BlockNetworkImage = false;
                settings.UserAgentString = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36";

                try
                {
                    CookieManager.Instance?.SetAcceptCookie(true);
                    CookieManager.Instance?.SetAcceptThirdPartyCookies(webView, true);
                }
                catch { }

                reg = ct.Register(() =>
                {
                    mainHandler.Post(() =>
                    {
                        Cleanup();
                        tcs.TrySetCanceled(ct);
                    });
                });

                var ctsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                ctsTimeout.Token.Register(() =>
                {
                    mainHandler.Post(() =>
                    {
                        if (!isCompleted)
                        {
                            if (webView != null)
                            {
                                TryExtract(webView, isJson, result =>
                                {
                                    Cleanup();
                                    tcs.TrySetResult(result);
                                });
                            }
                            else
                            {
                                Cleanup();
                                tcs.TrySetResult(null);
                            }
                        }
                    });
                });

                var client = new HeadlessWebViewClient(
                    onPageFinished: () =>
                    {
                        if (isCompleted || webView == null) return;
                        CheckAndExtract(webView, isJson, result =>
                        {
                            Cleanup();
                            tcs.TrySetResult(result);
                        });
                    }
                );

                webView.SetWebViewClient(client);

                // Polling kiểm tra định kỳ mỗi 1 giây để bắt khoảnh khắc Cloudflare Turnstile tự động xác minh xong
                pollTimer = new Timer(_ =>
                {
                    mainHandler.Post(() =>
                    {
                        if (isCompleted || webView == null) return;

                        CheckAndExtract(webView, isJson, result =>
                        {
                            if (!string.IsNullOrEmpty(result))
                            {
                                Cleanup();
                                tcs.TrySetResult(result);
                            }
                        });
                    });
                }, null, 1200, 1000);

                webView.LoadUrl(url);
            }
            catch (Exception ex)
            {
                Cleanup();
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private static void CheckAndExtract(WebView webView, bool isJson, Action<string?> onReady)
    {
        string checkScript = @"(function(){
            try {
                var title = document.title || '';
                var body = document.body ? document.body.innerText : '';
                var isChallenge = (
                    title.indexOf('Just a moment') >= 0 ||
                    title.indexOf('Checking your browser') >= 0 ||
                    title.indexOf('Cloudflare') >= 0 ||
                    title.indexOf('DDoS-Guard') >= 0 ||
                    body.indexOf('Checking your browser before accessing') >= 0 ||
                    body.indexOf('Verify you are human') >= 0 ||
                    body.indexOf('DDoS protection by Cloudflare') >= 0 ||
                    !!document.getElementById('challenge-stage') ||
                    !!document.getElementById('cf-please-wait') ||
                    !!document.getElementById('challenge-running')
                );
                var len = document.documentElement ? document.documentElement.outerHTML.length : 0;
                return JSON.stringify({ challenge: isChallenge, len: len });
            } catch(e) {
                return JSON.stringify({ challenge: true, len: 0 });
            }
        })();";

        webView.EvaluateJavascript(checkScript, new StringValueCallback(raw =>
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return;
            try
            {
                string json = ParseJsString(raw);
                using var doc = JsonDocument.Parse(json);
                bool isChallenge = doc.RootElement.GetProperty("challenge").GetBoolean();
                int len = doc.RootElement.GetProperty("len").GetInt32();

                if (!isChallenge && len > 100)
                {
                    TryExtract(webView, isJson, onReady);
                }
            }
            catch
            {
            }
        }));
    }

    private static void TryExtract(WebView webView, bool isJson, Action<string?> onResult)
    {
        if (isJson)
        {
            webView.EvaluateJavascript(
                "(function(){ var pre = document.querySelector('pre'); return pre ? pre.innerText : (document.body ? document.body.innerText : ''); })();",
                new StringValueCallback(raw =>
                {
                    if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                    {
                        onResult(null);
                        return;
                    }
                    string cleaned = ParseJsString(raw);
                    onResult(cleaned);
                }));
        }
        else
        {
            webView.EvaluateJavascript(
                "document.documentElement ? document.documentElement.outerHTML : ''",
                new StringValueCallback(raw =>
                {
                    if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                    {
                        onResult(null);
                        return;
                    }
                    string cleaned = ParseJsString(raw);
                    onResult(cleaned);
                }));
        }
    }

    private static string ParseJsString(string raw)
    {
        try
        {
            if (raw.StartsWith('"') && raw.EndsWith('"'))
            {
                return JsonSerializer.Deserialize<string>(raw) ?? raw;
            }
        }
        catch
        {
        }
        return raw;
    }

#pragma warning disable CS0618
    [global::Android.Runtime.Preserve(AllMembers = true)]
#pragma warning restore CS0618
    private sealed class HeadlessWebViewClient : WebViewClient
    {
        private readonly Action _onPageFinished;

        public HeadlessWebViewClient(Action onPageFinished)
        {
            _onPageFinished = onPageFinished;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            _onPageFinished();
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            return false;
        }
    }

#pragma warning disable CS0618
    [global::Android.Runtime.Preserve(AllMembers = true)]
#pragma warning restore CS0618
    private sealed class StringValueCallback : Java.Lang.Object, IValueCallback
    {
        private readonly Action<string?> _callback;

        public StringValueCallback(Action<string?> callback)
        {
            _callback = callback;
        }

        public void OnReceiveValue(Java.Lang.Object? value)
        {
            _callback(value?.ToString());
        }
    }
}

