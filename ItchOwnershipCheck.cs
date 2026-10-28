using System;
using System.Collections;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;


/// Checks itch.io ownership once (OAuth implicit flow on a local loopback
/// listener), then saves a machine-bound offline license. Later launches use
/// the license and need no internet. Quiet re-checks run during play.
///
/// Setup:
/// 1. Unity: Tools > itch.io Licensing > Generate Secrets. The license secret,
///    port and file settings are randomized for your project automatically.
/// 2. itch.io > Settings > OAuth applications: create an app with the SAME
///    account that owns the game page. Set its callback URL to the one shown
///    in the Generate Secrets window (http://127.0.0.1:<your port>/callback).
/// 3. Back in the window, enter the Client ID and numeric Game ID, then Generate.
/// 4. Add this component to a startup object and assign the access objects.
///
/// The offline license lasts ItchLicenseConfig.OfflineDays after each online
/// login (set in the same Generate Secrets window; 0 = never expires).
///
/// Developer access (you count as an owner of your own game) works only in
/// the Editor and Development Builds; release builds don't contain it.
/// Not for WebGL builds (no HttpListener there).
public class ItchOwnershipCheck : MonoBehaviour
{
    [Header("Login")]
    [SerializeField] private float loginTimeoutMinutes = 5f;

    [Header("Game Access")]
    [SerializeField] private GameObject[] objectsToEnable;
    [SerializeField] private GameObject[] objectsToDisable;

    [Header("Background Re-checks")]
    [Tooltip("Random time range between quiet license re-checks during play.")]
    [SerializeField] private float recheckMinMinutes = 10f;
    [SerializeField] private float recheckMaxMinutes = 25f;

    [Tooltip("Random delay between a failed re-check and its consequence, " +
             "so the effect is hard to trace back to the check.")]
    [SerializeField] private float failureDelayMinSeconds = 60f;
    [SerializeField] private float failureDelayMaxSeconds = 180f;

    [Tooltip("Called (after the delay) when a re-check fails. Hook something here, " +
             "e.g. return to the main menu or break a later level.")]
    [SerializeField] private UnityEvent onLicenseInvalid;

    // Random per project, chosen in the Generate Secrets window.
    private static int Port => ItchStrings.Port;
    private const string CallbackPath = "/callback";
    private const string TokenPath = "/token";

    // Longest token we accept. Real itch.io tokens are far shorter.
    private const int MaxTokenLength = 512;

    // itch.io returns the token in the URL fragment (#access_token=...), which
    // browsers never send to servers. This script forwards it to the game with
    // a background fetch (not a navigation), and first strips the fragment
    // from the address bar, so the token does not end up in browser history.
    // The response is shown with textContent, so it can never be run as HTML.
    private const string CallbackScript =
        "(function(){" +
        "var h=window.location.hash.slice(1);" +
        "try{history.replaceState(null,'','" + CallbackPath + "');}catch(e){}" +
        "var m=document.getElementById('m');" +
        "fetch('" + TokenPath + "?'+h,{cache:'no-store',credentials:'omit',referrerPolicy:'no-referrer'})" +
        ".then(function(r){return r.text();})" +
        ".then(function(t){m.textContent=t;})" +
        ".catch(function(){m.textContent='Could not reach the game. Return to the game and try again.';});" +
        "})();";

    private const string CallbackHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Login</title></head>" +
        "<body><p id=\"m\">Finishing login...</p><script>" + CallbackScript + "</script></body></html>";

    // Only the exact script above may run on the callback page, and it may only
    // talk back to this listener.
    private static readonly string CallbackCsp =
        "default-src 'none'; script-src '" + Sha256Base64(CallbackScript) + "'; " +
        "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static string ListenerHost => $"127.0.0.1:{Port}";
    private static string ListenerOrigin => $"http://127.0.0.1:{Port}";
    private string RedirectUri => $"http://127.0.0.1:{Port}{CallbackPath}";

    private sealed class CallbackOutcome
    {
        public string Token;
        public bool Failed;
    }

    // Covers the rare case where ownership was verified online but the
    // license file could not be written (e.g. disk error).
    private static bool verifiedOnlineThisSession;

    private bool checking;
    private HttpListener activeListener;
    private Coroutine recheckRoutine;

    [Serializable]
    private class OwnershipResponse
    {
        public bool owns;
        public int game_id;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [Serializable]
    private class ProfileGame
    {
        public int id;
    }

    [Serializable]
    private class ProfileGamesResponse
    {
        public ProfileGame[] games;
    }
#endif

    private async void Start()
    {
        SetGameAccess(false);

        if (!SecretsConfigured())
        {
            ItchLog.Error("Secrets not set. Use Tools > itch.io Licensing > Generate Secrets.");
            return;
        }

        bool licensed = await ItchLicenseCache.HasValidLicenseAsync(ItchSecrets.GameId);

        if (this == null)
            return;

        if (licensed)
        {
            Grant();
            return;
        }

        await StartItchLogin();
    }

    private void OnDestroy()
    {
        StopListener();
    }

    /// Quietly re-verifies the license without going online. Call it from a
    /// few unrelated places (level loads, saving, unlocking content) and react
    /// to a false result later rather than immediately.
    ///
    /// A license that expires during the session still passes here: the
    /// player was licensed at startup, so they can finish the session. The
    /// expiry is enforced on the next launch. 
    public static async Task<bool> VerifyLicenseAsync()
    {
        if (!SecretsConfigured())
            return false;

        if (verifiedOnlineThisSession)
            return true;

        LicenseStatus status = await ItchLicenseCache.CheckAsync(ItchSecrets.GameId);
        return status != LicenseStatus.Invalid;
    }

    /// Starts the itch.io login. For UI buttons, which can only call methods
    /// that return nothing (connect a Button's On Click to this).
    public void Login()
    {
        _ = StartItchLogin();
    }

    /// Runs the itch.io login and ownership check.
    public async Task StartItchLogin()
    {
        if (checking)
            return;

        checking = true;

        try
        {
            if (!SecretsConfigured())
            {
                ItchLog.Error("Secrets not set. Use Tools > itch.io Licensing > Generate Secrets.");
                return;
            }

            string token = await GetAccessToken();
            bool owns = await CheckOwnership(token);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!owns && await IsDeveloper(token))
            {
                ItchLog.Info("Logged-in user is a developer of this game (dev builds only).");
                owns = true;
            }
#endif

            if (owns)
            {
                ItchLog.Info("Ownership verified.");
                verifiedOnlineThisSession = true;

                await ItchLicenseCache.SaveAsync(ItchSecrets.GameId);

                if (this == null)
                    return;

                Grant();
            }
            else
            {
                ItchLog.Warn("This itch.io account does not own the game.");
                SetGameAccess(false);
            }
        }
        catch (Exception e)
        {
            ItchLog.Error("Verification failed: " + e.GetType().Name + " " + e.Message);
            SetGameAccess(false);
        }
        finally
        {
            StopListener();
            checking = false;
        }
    }

    private void Grant()
    {
        SetGameAccess(true);

        if (recheckRoutine == null)
            recheckRoutine = StartCoroutine(RecheckLoop());
    }

    private IEnumerator RecheckLoop()
    {
        float minWait = Mathf.Max(30f, Mathf.Min(recheckMinMinutes, recheckMaxMinutes) * 60f);
        float maxWait = Mathf.Max(minWait, Mathf.Max(recheckMinMinutes, recheckMaxMinutes) * 60f);

        while (true)
        {
            yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(minWait, maxWait));

            Task<bool> check = VerifyLicenseAsync();
            while (!check.IsCompleted)
                yield return null;

            if (check.Status == TaskStatus.RanToCompletion && check.Result)
                continue;

            ItchLog.Warn("Background re-check failed.");

            float minDelay = Mathf.Max(0f, Mathf.Min(failureDelayMinSeconds, failureDelayMaxSeconds));
            float maxDelay = Mathf.Max(minDelay, Mathf.Max(failureDelayMinSeconds, failureDelayMaxSeconds));
            yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(minDelay, maxDelay));

            SetGameAccess(false);
            onLicenseInvalid?.Invoke();

            recheckRoutine = null;
            yield break;
        }
    }

    private async Task<string> GetAccessToken()
    {
        string state = RandomBase64Url(32);

        string scope = ItchStrings.ScopeOwnership;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        scope += " " + ItchStrings.ScopeProfileGames;
#endif

        string authUrl =
            ItchStrings.AuthUrl +
            "?client_id=" + Escape(ItchSecrets.ClientId) +
            "&scope=" + Escape(scope) +
            "&redirect_uri=" + Escape(RedirectUri) +
            "&response_type=token" +
            "&state=" + Escape(state);

        activeListener = new HttpListener();
        activeListener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        try
        {
            activeListener.Start();
        }
        catch (Exception e)
        {
            ItchLog.Error($"Could not listen on port {Port} (in use or blocked): {e.Message}");
            throw;
        }

        ItchLog.Info("Opening itch.io login...");
        Application.OpenURL(authUrl);

        Task timeout = Task.Delay(TimeSpan.FromMinutes(Mathf.Max(1f, loginTimeoutMinutes)));

        while (true)
        {
            Task<HttpListenerContext> next = activeListener.GetContextAsync();

            if (await Task.WhenAny(next, timeout) != next)
            {
                ItchLog.Error("Login timed out.");
                throw new TimeoutException();
            }

            HttpListenerContext ctx = await next;
            CallbackOutcome outcome;

            // A single bad or aborted request (from a web page, another program,
            // or a closed tab) must never end the login. Only a request that
            // carries our secret state value can finish or cancel it.
            try
            {
                outcome = await HandleLoginRequest(ctx, state);
            }
            catch (Exception e)
            {
                ItchLog.Warn("Ignored a failed request on the login listener: " + e.GetType().Name);
                AbortQuietly(ctx);
                continue;
            }

            if (outcome == null)
                continue;

            if (outcome.Failed)
                throw new OperationCanceledException();

            return outcome.Token;
        }
    }

    /// Handles one request on the loopback listener. Returns null to keep
    /// waiting. Only reads the URL and a few headers; the request body is never
    /// read, nothing from the request is executed, written to disk, or echoed
    /// back, and all responses are fixed text.
    private static async Task<CallbackOutcome> HandleLoginRequest(HttpListenerContext ctx, string state)
    {
        HttpListenerRequest request = ctx.Request;

        if (!IsTrustedRequest(request))
        {
            await Respond(ctx, 403, "text/plain", "Forbidden");
            return null;
        }

        if (request.HttpMethod != "GET")
        {
            await Respond(ctx, 405, "text/plain", "Method not allowed");
            return null;
        }

        Uri url = request.Url;
        string returnedState = QueryValue(url, "state");

        if (url.AbsolutePath == CallbackPath)
        {
            // Some errors come back in the query instead of the fragment.
            if (!string.IsNullOrEmpty(QueryValue(url, "error")))
            {
                await Respond(ctx, 200, "text/plain", "Login cancelled. You can return to the game.");

                if (StateMatches(returnedState, state))
                {
                    ItchLog.Error("Login was cancelled or denied.");
                    return new CallbackOutcome { Failed = true };
                }

                ItchLog.Warn("Ignored an error callback with a wrong state.");
                return null;
            }

            await Respond(ctx, 200, "text/html", CallbackHtml, CallbackCsp);
            return null;
        }

        if (url.AbsolutePath == TokenPath)
        {
            // The token is only ever sent by our own callback page.
            string fetchSite = request.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(fetchSite) && fetchSite != "same-origin")
            {
                await Respond(ctx, 403, "text/plain", "Forbidden");
                return null;
            }

            if (!StateMatches(returnedState, state))
            {
                // Not from this login (forged, stale, or a different tab):
                // ignore it and keep waiting for the real one.
                ItchLog.Warn("Ignored a token callback with a wrong state.");
                await Respond(ctx, 400, "text/plain",
                    "This login link is no longer valid. Return to the game and try again.");
                return null;
            }

            if (!string.IsNullOrEmpty(QueryValue(url, "error")))
            {
                await Respond(ctx, 200, "text/plain", "Login cancelled. You can return to the game.");
                ItchLog.Error("Login was cancelled or denied.");
                return new CallbackOutcome { Failed = true };
            }

            string token = QueryValue(url, "access_token");

            if (!IsWellFormedToken(token))
            {
                await Respond(ctx, 400, "text/plain", "Login failed. Return to the game and try again.");
                ItchLog.Error("Invalid OAuth callback. Check the registered callback URL.");
                return new CallbackOutcome { Failed = true };
            }

            await Respond(ctx, 200, "text/plain",
                "Login complete. You can close this tab and return to the game.");

            return new CallbackOutcome { Token = token };
        }

        // Stray requests such as /favicon.ico.
        await Respond(ctx, 404, "text/plain", "Not found");
        return null;
    }

    /// Defence in depth on top of binding to 127.0.0.1 only: the request must
    /// come from this computer and be addressed to 127.0.0.1:port by name,
    /// which blocks DNS-rebinding pages, and must not come from another
    /// web site's script.
    private static bool IsTrustedRequest(HttpListenerRequest request)
    {
        IPEndPoint remote = request.RemoteEndPoint;
        if (remote == null || !IPAddress.IsLoopback(remote.Address))
            return false;

        if (request.Url == null)
            return false;

        string host = request.Headers["Host"];
        if (!string.Equals(host, ListenerHost, StringComparison.OrdinalIgnoreCase))
            return false;

        string origin = request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) &&
            !string.Equals(origin, ListenerOrigin, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool StateMatches(string received, string expected)
    {
        if (received == null || expected == null || received.Length != expected.Length)
            return false;

        int diff = 0;
        for (int i = 0; i < expected.Length; i++)
            diff |= received[i] ^ expected[i];
        return diff == 0;
    }

    /// Accepts only plain token characters, so nothing unexpected (such as
    /// line breaks) can reach the Authorization header of the API request.
    private static bool IsWellFormedToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
            return false;

        foreach (char c in token)
        {
            bool ok =
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                c == '-' || c == '_' || c == '.' || c == '~' || c == '+' || c == '/' || c == '=';
            if (!ok)
                return false;
        }

        return true;
    }

    private async Task<bool> CheckOwnership(string token)
    {
        string gameId = ItchSecrets.GameId;
        string url = ItchStrings.ApiBase + ItchStrings.GamesPath + gameId + ItchStrings.OwnershipPath;

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.SetRequestHeader("Authorization", "Bearer " + token);

            await SendAsync(request);

            string body = request.downloadHandler != null ? request.downloadHandler.text : "";

            if (request.result != UnityWebRequest.Result.Success)
            {
                ItchLog.Error("Ownership request failed: " + request.error + " " + body);
                throw new InvalidOperationException();
            }

            ItchLog.Info("Ownership response: " + body);

            OwnershipResponse response = JsonUtility.FromJson<OwnershipResponse>(body);

            if (response == null)
            {
                ItchLog.Error("Unexpected ownership response.");
                throw new InvalidOperationException();
            }

            // game_id is 0 if the field is missing; only reject a real mismatch.
            if (response.game_id != 0 && response.game_id.ToString() != gameId)
            {
                ItchLog.Error("Ownership response was for a different game.");
                throw new InvalidOperationException();
            }

            return response.owns;
        }
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private async Task<bool> IsDeveloper(string token)
    {
        string gameId = ItchSecrets.GameId;

        using (UnityWebRequest request =
            UnityWebRequest.Get(ItchStrings.ApiBase + ItchStrings.ProfileGamesPath))
        {
            request.SetRequestHeader("Authorization", "Bearer " + token);

            await SendAsync(request);

            string body = request.downloadHandler != null ? request.downloadHandler.text : "";

            if (request.result != UnityWebRequest.Result.Success)
            {
                ItchLog.Warn("Developer check failed: " + request.error + " " + body);
                return false;
            }

            ProfileGamesResponse response = JsonUtility.FromJson<ProfileGamesResponse>(body);

            if (response == null || response.games == null)
                return false;

            foreach (ProfileGame game in response.games)
            {
                if (game != null && game.id.ToString() == gameId)
                    return true;
            }

            return false;
        }
    }
#endif

    private static bool SecretsConfigured()
    {
        return ItchSecrets.IsConfigured && int.TryParse(ItchSecrets.GameId, out int id) && id > 0;
    }

    // Works on all Unity versions (no reliance on awaitable AsyncOperation).
    private static Task SendAsync(UnityWebRequest request)
    {
        var tcs = new TaskCompletionSource<bool>();
        UnityWebRequestAsyncOperation op = request.SendWebRequest();

        if (op.isDone)
            tcs.SetResult(true);
        else
            op.completed += _ => tcs.TrySetResult(true);

        return tcs.Task;
    }

    private static async Task Respond(HttpListenerContext ctx, int status, string contentType,
        string body, string csp = null)
    {
        HttpListenerResponse response = ctx.Response;
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = status;
        response.KeepAlive = false;
        response.ContentType = contentType + "; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.AddHeader("Cache-Control", "no-store");
        response.AddHeader("Pragma", "no-cache");
        response.AddHeader("X-Content-Type-Options", "nosniff");
        response.AddHeader("Referrer-Policy", "no-referrer");
        response.AddHeader("X-Frame-Options", "DENY");
        response.AddHeader("Content-Security-Policy",
            csp ?? "default-src 'none'; frame-ancestors 'none'");

        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.Close();
    }

    private static void AbortQuietly(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.Abort();
        }
        catch
        {
            // Connection already gone.
        }
    }

    private static string Sha256Base64(string text)
    {
        using (SHA256 sha = SHA256.Create())
            return "sha256-" + Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private void StopListener()
    {
        if (activeListener == null)
            return;

        try
        {
            if (activeListener.IsListening)
                activeListener.Stop();
            activeListener.Close();
        }
        catch
        {
            // Already closed.
        }

        activeListener = null;
    }

    private void SetGameAccess(bool allowed)
    {
        if (objectsToEnable != null)
        {
            foreach (GameObject obj in objectsToEnable)
                if (obj != null)
                    obj.SetActive(allowed);
        }

        if (objectsToDisable != null)
        {
            foreach (GameObject obj in objectsToDisable)
                if (obj != null)
                    obj.SetActive(!allowed);
        }
    }

    private static string RandomBase64Url(int length)
    {
        byte[] bytes = new byte[length];

        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Escape(string value)
    {
        return UnityWebRequest.EscapeURL(value);
    }

    private static string QueryValue(Uri uri, string key)
    {
        string query = uri.Query.TrimStart('?');

        foreach (string part in query.Split('&'))
        {
            string[] pair = part.Split(new[] { '=' }, 2);

            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == key)
                return Uri.UnescapeDataString(pair[1].Replace("+", " "));
        }

        return null;
    }
}
