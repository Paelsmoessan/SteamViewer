#if WINDOWS
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using SteamViewer.App.Services.Models;
using SteamViewer.Common.Protocol;

namespace SteamViewer.App.Services;

/// <summary>
/// Routes input messages from WebView2 postMessage to the active ViewerSession.
/// Bypasses Blazor's DotNetObjectReference (which silently fails intermittently)
/// using the native WebView2 WebMessageReceived channel instead.
///
/// JS sends: chrome.webview.postMessage(JSON.stringify({type:'input', method:'...', ...}))
/// C# receives: CoreWebView2.WebMessageReceived → parse → SendInputAsync
/// </summary>
public sealed class InputMessageRouter : IDisposable
{
    private readonly ILogger<InputMessageRouter> _logger;
    private CoreWebView2? _coreWebView2;
    private ViewerSession? _activeSession;
    private bool _disposed;

    /// <summary>
    /// Fired when JS sends Ctrl+Alt+End (mapped to Ctrl+Alt+Del).
    /// RemoteViewer subscribes to send the special control message.
    /// </summary>
    public event Action? OnCtrlAltDelRequested;

    /// <summary>
    /// Fired when JS sends Ctrl+V clipboard paste.
    /// RemoteViewer subscribes to read local clipboard and send to host.
    /// </summary>
    public event Func<Task>? OnClipboardPasteRequested;

    /// <summary>
    /// Fired when JS reports input lock state changed.
    /// RemoteViewer subscribes to install/remove system key interceptor.
    /// </summary>
    public event Action<bool>? OnLockChanged;

    public InputMessageRouter(ILogger<InputMessageRouter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attach to a CoreWebView2 instance. Called from ViewerPage/MainPage InitializeFrameBridge.
    /// Can be called multiple times — detaches from previous instance first.
    /// </summary>
    public void Initialize(CoreWebView2 coreWebView2)
    {
        if (_coreWebView2 == coreWebView2) return;

        Detach();

        _coreWebView2 = coreWebView2;
        _coreWebView2.WebMessageReceived += HandleWebMessage;
        _logger.LogInformation("InputMessageRouter attached to CoreWebView2");
    }

    /// <summary>
    /// Set the active session for input routing. Called by RemoteViewer on tab change.
    /// </summary>
    public void SetActiveSession(ViewerSession? session)
    {
        _activeSession = session;
    }

    private void Detach()
    {
        if (_coreWebView2 != null)
        {
            _coreWebView2.WebMessageReceived -= HandleWebMessage;
            _coreWebView2 = null;
        }
    }

    private void HandleWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;

            // Skip Blazor WebView internal messages (__bwv: prefix, not JSON)
            if (json[0] != '{') return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var messageType = typeProp.GetString();

            if (messageType == "resolution") { HandleResolution(root); return; }
            if (messageType != "input") return;

            if (!root.TryGetProperty("method", out var methodProp)) return;
            var method = methodProp.GetString();
            var session = _activeSession;

            switch (method)
            {
                case "mouseMove":      if (session != null) HandleMouseMove(root, session); break;
                case "mouseDown":      if (session != null) HandleMouseDown(root, session); break;
                case "mouseUp":        if (session != null) HandleMouseUp(root, session); break;
                case "mouseWheel":     if (session != null) HandleMouseWheel(root, session); break;
                case "keyDown":        if (session != null) HandleKeyDown(root, session); break;
                case "keyUp":          if (session != null) HandleKeyUp(root, session); break;
                case "clipboardPaste": HandleClipboardPaste(); break;
                case "lockChanged":    HandleLockChanged(root); break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process input web message");
        }
    }

    private void HandleResolution(JsonElement root)
    {
        var w = root.TryGetProperty("width", out var wp) ? wp.GetInt32() : 0;
        var h = root.TryGetProperty("height", out var hp) ? hp.GetInt32() : 0;
        if (w > 0 && h > 0 && _activeSession != null)
            _ = _activeSession.SendDesiredResolutionAsync(w, h);
    }

    private static void HandleMouseMove(JsonElement root, ViewerSession session)
        => _ = session.SendInputAsync(new InputEvent.MouseMove(
            root.GetProperty("x").GetDouble(),
            root.GetProperty("y").GetDouble(),
            root.GetProperty("captureW").GetInt32(),
            root.GetProperty("captureH").GetInt32()));

    private static void HandleMouseDown(JsonElement root, ViewerSession session)
        => _ = session.SendInputAsync(new InputEvent.MouseDown(
            ParseMouseButton(root.GetProperty("button").GetString()),
            root.GetProperty("x").GetDouble(),
            root.GetProperty("y").GetDouble(),
            root.GetProperty("captureW").GetInt32(),
            root.GetProperty("captureH").GetInt32()));

    private static void HandleMouseUp(JsonElement root, ViewerSession session)
        => _ = session.SendInputAsync(new InputEvent.MouseUp(
            ParseMouseButton(root.GetProperty("button").GetString()),
            root.GetProperty("x").GetDouble(),
            root.GetProperty("y").GetDouble(),
            root.GetProperty("captureW").GetInt32(),
            root.GetProperty("captureH").GetInt32()));

    private static void HandleMouseWheel(JsonElement root, ViewerSession session)
        => _ = session.SendInputAsync(new InputEvent.MouseWheel(
            root.GetProperty("deltaX").GetDouble(),
            root.GetProperty("deltaY").GetDouble()));

    private readonly record struct KeyEventInfo(string Key, KeyModifiers Mods, string? Code, bool AltGr);

    private static KeyEventInfo ParseKeyEvent(JsonElement root)
    {
        var key = root.GetProperty("key").GetString() ?? "";
        var mods = ParseModifiers(root.GetProperty("modifiers"));
        var code = root.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
        var altGr = root.TryGetProperty("altGr", out var altGrProp) && altGrProp.GetBoolean();
        return new KeyEventInfo(key, mods, code, altGr);
    }

    // Ctrl+Alt+End → Ctrl+Alt+Del (standard RDP convention). Named so the dispatch
    // logic reads as intent, not as a 3-conjunct check.
    private static bool IsCtrlAltEnd(KeyEventInfo k)
        => k.Key == "End" && k.Mods.Ctrl && k.Mods.Alt;

    private void HandleKeyDown(JsonElement root, ViewerSession session)
    {
        var k = ParseKeyEvent(root);
        if (IsCtrlAltEnd(k))
        {
            OnCtrlAltDelRequested?.Invoke();
            return;
        }
        _ = session.SendInputAsync(new InputEvent.KeyDown(k.Key, k.Mods, k.Code, k.AltGr));
    }

    private static void HandleKeyUp(JsonElement root, ViewerSession session)
    {
        var k = ParseKeyEvent(root);
        if (IsCtrlAltEnd(k)) return; // Suppress End key-up for Ctrl+Alt+End→Del
        _ = session.SendInputAsync(new InputEvent.KeyUp(k.Key, k.Mods, k.Code, k.AltGr));
    }

    private void HandleClipboardPaste()
    {
        _ = Task.Run(async () =>
        {
            try { if (OnClipboardPasteRequested != null) await OnClipboardPasteRequested.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Clipboard paste handler failed"); }
        });
    }

    private void HandleLockChanged(JsonElement root)
    {
        if (root.TryGetProperty("locked", out var lockedProp))
            OnLockChanged?.Invoke(lockedProp.GetBoolean());
    }

    private static MouseButton ParseMouseButton(string? button) => (button?.ToLowerInvariant()) switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        "xbutton1" => MouseButton.XButton1,
        "xbutton2" => MouseButton.XButton2,
        _ => MouseButton.Left
    };

    private static KeyModifiers ParseModifiers(JsonElement modifiers)
    {
        bool ctrl = false, shift = false, alt = false;
        if (modifiers.TryGetProperty("ctrl", out var ctrlProp)) ctrl = ctrlProp.GetBoolean();
        if (modifiers.TryGetProperty("shift", out var shiftProp)) shift = shiftProp.GetBoolean();
        if (modifiers.TryGetProperty("alt", out var altProp)) alt = altProp.GetBoolean();
        return new KeyModifiers(ctrl, shift, alt, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _activeSession = null;
    }
}
#endif
