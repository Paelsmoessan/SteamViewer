using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SteamViewer.Common.Protocol;
using SteamViewer.Platform.Windows.Clipboard;

namespace SteamViewer.App.Services.Models;

public sealed partial class HostSession
{
    // Clipboard file transfer -- host is both sender (monitors clipboard) and receiver (serves file chunks)
    private ClipboardMonitor? _clipboardMonitor;
    private ClipboardFileServer? _clipboardFileServer;
    private ClipboardFileWriter? _clipboardFileWriter;

    #region Clipboard

    private async Task HandleClipboardRequestAsync()
    {
        if (_transport == null) return;

        // Win32-first on host (TODO §5 P1 "Host should use Win32 clipboard first" + P1
        // "WebView2 clipboard READ permission prompt 0.0.0.0"):
        // - The WebView2 readText prompt presents an ugly "0.0.0.0 wants to See text and
        //   images copied to the clipboard" dialog because WebView2 base URL is unconfigured.
        // - Win32 GetClipboardData has no prompt + no focus requirement; on host context
        //   it's strictly better.
        // - Browser API kept as a last-resort fallback in case Win32 fails on some
        //   esoteric format. Wrapped in the same 500ms timeout defense the WRITE path uses.
        string? text = TryGetClipboardNative();

        if (string.IsNullOrEmpty(text) && _jsRuntime != null)
        {
            try
            {
                text = await _jsRuntime.InvokeAsync<string>("navigator.clipboard.readText")
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Clipboard read: browser API timed out after 500ms (Win32 already failed) - returning empty");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Clipboard read: browser API failed after Win32 fallback miss - returning empty");
            }
        }

        if (!string.IsNullOrEmpty(text))
        {
            var response = JsonSerializer.Serialize<ClipboardMessage>(
                new ClipboardMessage.Response("text", text));
            await _transport.SendControlAsync(response);
            _logger.LogDebug("Sent clipboard to viewer: {Length} chars", text.Length);
        }
    }

    private async Task HandleClipboardSetAsync(JsonElement root)
    {
        var data = JsonAccessors.GetString(root, "data");
        if (data == null) return;

        // Record self-write BEFORE the clipboard write so the resulting
        // WM_CLIPBOARDUPDATE is suppressed by the monitor's hash check.
        // Without this, host's ClipboardMonitor would detect the text change and
        // auto-push it back to viewer, creating an echo loop (active condition
        // once viewer->host text auto-push subscription is wired).
        _clipboardMonitor?.RecordSelfWriteText(data);

        // Win32-first on host (TODO §5 P1 "Host should use Win32 clipboard first").
        // The browser-API path has been observed to fail under WebView2-not-warm
        // conditions (post-reconnect) and produces avoidable permission prompts.
        // Win32 SetClipboardData has no prompt + no focus requirement. Browser API
        // kept only as a last-resort if Win32 fails.
        bool set = TrySetClipboardNative(data);
        if (set)
        {
            _logger.LogDebug("Set clipboard from viewer: {Length} chars (Win32, primary path)", data.Length);
            return;
        }

        _logger.LogDebug("Clipboard set: Win32 failed, falling back to browser API ({Length} chars)", data.Length);
        if (_jsRuntime != null)
        {
            try
            {
                // 500ms timeout defends against hung InvokeVoidAsync (WebView2 not warm).
                await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", data)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
                _logger.LogDebug("Set clipboard from viewer: {Length} chars (browser API fallback)", data.Length);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Clipboard set: browser API fallback timed out after 500ms ({Length} chars) - clipboard write LOST", data.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Clipboard set: browser API fallback failed ({Length} chars) - clipboard write LOST", data.Length);
            }
        }
    }

    private async Task HandleClipboardPasteAsync(JsonElement root)
    {
        var data = JsonAccessors.GetString(root, "data");
        if (data == null) return;

        // Record self-write BEFORE the clipboard write to suppress echo loop
        // (same reasoning as HandleClipboardSetAsync above).
        _clipboardMonitor?.RecordSelfWriteText(data);

        // Win32-first on host (same rationale as HandleClipboardSetAsync).
        bool clipboardSet = TrySetClipboardNative(data);
        if (clipboardSet)
        {
            _logger.LogDebug("Clipboard paste: set via Win32 ({Length} chars, primary path)", data.Length);
        }
        else if (_jsRuntime != null)
        {
            _logger.LogDebug("Clipboard paste: Win32 failed, falling back to browser API ({Length} chars)", data.Length);
            try
            {
                await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", data)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
                clipboardSet = true;
                _logger.LogDebug("Clipboard paste: set via browser API fallback ({Length} chars)", data.Length);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Clipboard paste: browser API fallback timed out after 500ms ({Length} chars) - paste WILL FAIL", data.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Clipboard paste: browser API fallback failed ({Length} chars) - paste WILL FAIL", data.Length);
            }
        }

        if (!clipboardSet)
        {
            _logger.LogWarning("Failed to set clipboard via both Win32 and browser API - paste aborted");
        }

        if (!clipboardSet) return;

        try
        {
            var ctrlMod = new KeyModifiers(Ctrl: true);
            var noMod = KeyModifiers.None;
            InputEvent[] keystrokes =
            [
                new InputEvent.KeyDown("Control", ctrlMod),
                new InputEvent.KeyDown("v", ctrlMod),
                new InputEvent.KeyUp("v", ctrlMod),
                new InputEvent.KeyUp("Control", noMod),
            ];

            foreach (var keystroke in keystrokes)
            {
                var json = JsonSerializer.Serialize(keystroke);
                if (_elevationService != null && (_elevationService.IsAdminConnected || _elevationService.IsSystemConnected))
                {
                    await _elevationService.InjectInputAsync(json, _lastCaptureWidth, _lastCaptureHeight);
                }
                else
                {
                    _inputInjector.InjectInput(keystroke, _lastCaptureWidth, _lastCaptureHeight);
                }
            }
            _logger.LogDebug("Clipboard paste: Ctrl+V injected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject Ctrl+V for clipboard paste");
        }
    }

    private static string? TryGetClipboardNative()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                var hData = GetClipboardData(CF_UNICODETEXT);
                if (hData == IntPtr.Zero) return null;
                var pData = GlobalLock(hData);
                if (pData == IntPtr.Zero) return null;
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(pData);
                }
                finally { GlobalUnlock(hData); }
            }
            finally { CloseClipboard(); }
        }
        catch { return null; }
    }

    private static bool TrySetClipboardNative(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                int byteCount = (text.Length + 1) * 2;
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                if (hGlobal == IntPtr.Zero) return false;
                var pGlobal = GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt16(pGlobal + text.Length * 2, 0);
                }
                finally { GlobalUnlock(hGlobal); }
                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }
        catch { return false; }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    #endregion

    #region Clipboard File Transfer

    private void StartClipboardFileTransfer()
    {
        if (!OperatingSystem.IsWindows() || _transport == null) return;

        try
        {
            _clipboardFileServer = new ClipboardFileServer(
                _loggerFactory.CreateLogger<ClipboardFileServer>(),
                async (data) => { return await _transport!.SendFileDataAsync(data); },
                async (json) => await _transport!.SendFileSignalingAsync(json));

            _clipboardMonitor = new ClipboardMonitor(_loggerFactory.CreateLogger<ClipboardMonitor>());
            _clipboardMonitor.ClipboardFilesDetected += OnClipboardFilesDetected;
            _clipboardMonitor.ClipboardTextDetected += OnClipboardTextDetected;
            _clipboardMonitor.Start();

            _clipboardFileWriter = new ClipboardFileWriter(
                _loggerFactory.CreateLogger<ClipboardFileWriter>(),
                async (request) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(request);
                    await _transport.SendFileSignalingAsync(json);
                },
                _clipboardMonitor,
                async (startMsg) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(startMsg);
                    await _transport.SendFileSignalingAsync(json);
                },
                async (stopMsg) =>
                {
                    var json = JsonSerializer.Serialize<ClipboardFileMessage>(stopMsg);
                    await _transport.SendFileSignalingAsync(json);
                },
                async (data) => await _transport!.SendFileDataAsync(data));
            _clipboardFileWriter.Start();

            _logger.LogInformation("Clipboard file transfer initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize clipboard file transfer");
        }
    }

    private void OnClipboardFilesDetected(ClipboardFileInfo[] files, string[] localPaths)
    {
        _logger.LogDebug("OnClipboardFilesDetected entry: files={Count} transport={Transport} connected={Connected}",
            files.Length,
            _transport != null ? "set" : "null",
            _transport?.IsConnected);
        if (_transport == null || !_transport.IsConnected)
        {
            _logger.LogWarning("OnClipboardFilesDetected: dropping {Count} file(s) — transport not ready (transport={Transport}, connected={Connected})",
                files.Length, _transport != null ? "set" : "null", _transport?.IsConnected);
            return;
        }

        try
        {
            _clipboardFileServer?.SetFilePaths(localPaths);

            var formatList = new ClipboardFileMessage.FormatList(files);
            var json = JsonSerializer.Serialize<ClipboardFileMessage>(formatList);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Send 3x with 500ms gaps for UDP reliability (idempotent on receiver)
                    for (int i = 0; i < 3; i++)
                    {
                        if (_transport == null || !_transport.IsConnected)
                        {
                            _logger.LogWarning("Clipboard format list send loop break at i={Iteration}: transport={Transport} connected={Connected}",
                                i, _transport != null ? "set" : "null", _transport?.IsConnected);
                            break;
                        }
                        var sent = await _transport.SendFileSignalingAsync(json);
                        if (i == 0) _logger.LogInformation("Sent clipboard file format list: {Count} files (sent={Sent}, attempt={Attempt})", files.Length, sent, i);
                        else _logger.LogDebug("Re-sent clipboard file format list (sent={Sent}, attempt={Attempt})", sent, i);
                        if (i < 2) await Task.Delay(500);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send clipboard file format list");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling clipboard files detected");
        }
    }

    private void OnClipboardTextDetected(string text)
    {
        _logger.LogDebug("OnClipboardTextDetected entry: len={Length} transport={Transport} connected={Connected}",
            text.Length,
            _transport != null ? "set" : "null",
            _transport?.IsConnected);
        if (_transport == null || !_transport.IsConnected)
        {
            _logger.LogWarning("OnClipboardTextDetected: dropping {Length}-char text — transport not ready (transport={Transport}, connected={Connected})",
                text.Length, _transport != null ? "set" : "null", _transport?.IsConnected);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var response = JsonSerializer.Serialize<ClipboardMessage>(
                    new ClipboardMessage.Response("text", text));
                await _transport.SendControlAsync(response);
                _logger.LogDebug("Auto-pushed clipboard text to viewer: {Length} chars", text.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-push clipboard text to viewer");
            }
        });
    }

    private async Task HandleFileChannelMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClipboardFileMessage>(json);
            if (message == null) return;

            switch (message)
            {
                case ClipboardFileMessage.FormatList formatList:
                    _clipboardFileWriter?.SetClipboard(formatList.Files);
                    break;
                case ClipboardFileMessage.FileContentsRequest request:
                    if (_clipboardFileServer != null)
                        await _clipboardFileServer.HandleRequestAsync(request);
                    break;
                case ClipboardFileMessage.StartStreaming startStreaming:
                    _clipboardFileServer?.HandleStartStreaming(startStreaming.FileIndex);
                    break;
                case ClipboardFileMessage.StopStreaming stopStreaming:
                    _clipboardFileServer?.HandleStopStreaming(stopStreaming.FileIndex);
                    break;
                case ClipboardFileMessage.TransferProgress progress:
                    _logger.LogInformation("Remote transfer progress: {FileName} â€” {Transferred}/{Total} ({Speed} MB/s)",
                        progress.FileName, FormatBytes(progress.BytesTransferred), FormatBytes(progress.TotalBytes), progress.SpeedMBps);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle file channel message");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private Task HandleFileDataBinary(byte[] data)
    {
        // Route ACKs to file server (sender), everything else to file writer (receiver)
        if (data.Length >= 8)
        {
            int flags = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4));
            if (flags == ClipboardFileServer.FlagPushAck)
            {
                int fileIndex = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                long bytesAcked = data.Length >= 16
                    ? System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(8, 8))
                    : 0;
                _clipboardFileServer?.HandlePushAck(fileIndex, bytesAcked);
                return Task.CompletedTask;
            }
        }
        _clipboardFileWriter?.HandleBinaryFileContentsResponse(data);
        return Task.CompletedTask;
    }

    private void StopClipboardFileTransfer()
    {
        _clipboardMonitor?.Dispose();
        _clipboardMonitor = null;
        _clipboardFileServer?.Dispose();
        _clipboardFileServer = null;
        _clipboardFileWriter?.Dispose();
        _clipboardFileWriter = null;
    }

    #endregion
}
