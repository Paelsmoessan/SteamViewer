// video-interop.js — FFmpeg transport video rendering + input capture
// Replaces webrtc-interop.js (WebRTC removed)
// Keeps: SharedBuffer rendering, mouse/keyboard capture, stats overlay, logger bridge

// === Logger Bridge ===
window.SteamViewerLogger = {
    dotNetRef: null,
    peerName: 'LOCAL',
    relayEnabled: false,

    initialize(dotNetReference) {
        this.dotNetRef = dotNetReference;
        console.log('JS Logger initialized');
    },

    setMode(isHost, customName = null) {
        this.peerName = customName || (isHost ? 'HOST' : 'VIEWER');
        this.relayEnabled = true;
        console.log(`Logger mode: ${this.peerName}`);
    },

    log(level, message) {
        try {
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnJSLog', level, `[${this.peerName}] ${message}`).catch(() => {});
            }
        } catch (e) {}
    },

    handleRelayedLog(level, message, from) {
        console.log(`[${from}] ${message}`);
        try {
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnJSLog', level, `[${from}] ${message}`).catch(() => {});
            }
        } catch (e) {}
    }
};

// Console interceptor
(() => {
    const origLog = console.log;
    const origWarn = console.warn;
    const origError = console.error;

    function fmt(args) {
        return Array.from(args).map(a => {
            if (typeof a === 'object') { try { return JSON.stringify(a); } catch { return String(a); } }
            return String(a);
        }).join(' ');
    }

    console.log = function(...args) { origLog.apply(console, args); window.SteamViewerLogger?.log('INFO', fmt(args)); };
    console.warn = function(...args) { origWarn.apply(console, args); window.SteamViewerLogger?.log('WARN', fmt(args)); };
    console.error = function(...args) { origError.apply(console, args); window.SteamViewerLogger?.log('ERROR', fmt(args)); };
})();

window.onerror = function(msg, src, line, col) { console.error(`[Uncaught] ${msg} at ${src}:${line}:${col}`); return false; };
window.addEventListener('unhandledrejection', function(e) { console.error(`[UnhandledPromise] ${e.reason}`); });

// === Letterbox Computation ===
function computeLetterbox(canvasW, canvasH, videoW, videoH) {
    if (!videoW || !videoH) return { dx: 0, dy: 0, dw: canvasW, dh: canvasH, videoW: 0, videoH: 0 };
    const canvasAspect = canvasW / canvasH;
    const videoAspect = videoW / videoH;
    let dx, dy, dw, dh;
    if (canvasAspect > videoAspect) {
        dh = canvasH; dw = canvasH * videoAspect; dx = (canvasW - dw) / 2; dy = 0;
    } else {
        dw = canvasW; dh = canvasW / videoAspect; dx = 0; dy = (canvasH - dh) / 2;
    }
    return { dx, dy, dw, dh, videoW, videoH };
}

// === Video Session Manager ===
window.SteamViewerVideo = {
    sessions: new Map(),

    initialize(sessionId) {
        // Force-reset if session already exists (reconnect scenario — stale canvas/context)
        if (this.sessions.has(sessionId)) {
            console.log(`[Video] Session ${sessionId} re-initializing (clearing stale state)`);
            this.dispose(sessionId);
        }
        this.sessions.set(sessionId, {
            canvas: null,
            ctx: null,
            letterbox: null,
            lastCanvasW: 0,
            lastCanvasH: 0,
            videoW: 0,
            videoH: 0,
            frameCount: 0,
            dotNetRef: null,
            // Display tracking for downscale detection
            lastDisplayW: 0,
            lastDisplayH: 0,
            isDownscaling: false,
            // Stats
            statsOverlayEl: null,
            statsVisible: false,
            statsData: null,
        });
        console.log(`[Video] Session ${sessionId} initialized`);
    },

    setRenderTarget(sessionId, canvasId) {
        const session = this.sessions.get(sessionId);
        if (!session) { console.warn(`[Video] No session: ${sessionId}`); return false; }

        const canvas = document.getElementById(canvasId);
        if (!canvas) { console.warn(`[Video] Canvas not found: ${canvasId}`); return false; }

        session.canvas = canvas;
        session.ctx = canvas.getContext('2d', { alpha: false });
        console.log(`[Video] Render target set: ${canvasId}`);
        return true;
    },

    setDotNetRef(sessionId, dotNetRef) {
        const session = this.sessions.get(sessionId);
        if (session) session.dotNetRef = dotNetRef;
    },

    // Called from C# when transport stats are available
    updateStats(sessionId, statsJson) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session.statsData = JSON.parse(statsJson);
        if (session.statsOverlayEl) {
            const s = session.statsData;
            const videoInfo = s.resolution || '?';
            // Canvas and display dimensions for diagnostics
            let canvasInfo = '?', displayInfo = '?', scaleStr = '?';
            if (session.canvas) {
                canvasInfo = `${session.canvas.width}x${session.canvas.height}`;
                const rect = session.canvas.getBoundingClientRect();
                const dpr = window.devicePixelRatio || 1;
                const dw = Math.round(rect.width * dpr);
                const dh = Math.round(rect.height * dpr);
                displayInfo = `${dw}x${dh}`;
                if (session.videoW && session.videoH) {
                    const scale = Math.min(dw / session.videoW, dh / session.videoH);
                    scaleStr = `${scale.toFixed(2)}x${session.isDownscaling ? ' ↓' : ''}`;
                }
            }
            const encInfo = (session.encodeW && session.encodeH)
                ? `${session.encodeW}x${session.encodeH}` : '?';
            const lines = [
                `Video: ${s.fps?.toFixed(0) || '?'} FPS | ${s.bitrateMbps?.toFixed(1) || '?'} Mbps`,
                `Src:   ${videoInfo} | Enc: ${encInfo} | Canvas: ${canvasInfo} | Disp: ${displayInfo} | Scale: ${scaleStr}`,
                `Lat:   Enc ${s.encodeMs?.toFixed(0) || '?'}ms | Dec ${s.decodeMs?.toFixed(0) || '?'}ms`,
                `Net:   ${s.bytesSent ? fmtBytes(s.bytesSent) : '?'} sent | ${s.bytesReceived ? fmtBytes(s.bytesReceived) : '?'} rcvd`,
            ];
            session.statsOverlayEl.textContent = lines.join('\n');
            session.statsOverlayEl.style.whiteSpace = 'pre';
        }
    },

    toggleStatsOverlay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session.statsVisible = !session.statsVisible;
        if (session.statsVisible) {
            this._createStatsOverlay(sessionId);
        } else {
            this._removeStatsOverlay(sessionId);
        }
    },

    _createStatsOverlay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session || session.statsOverlayEl) return;
        const el = document.createElement('div');
        el.id = `svStatsOverlay-${sessionId}`;
        el.style.cssText = `
            position: fixed; top: 40px; right: 10px; z-index: 10000;
            background: rgba(17,17,27,0.85); color: #a6adc8;
            font-family: 'Consolas','Courier New',monospace; font-size: 12px;
            padding: 8px 12px; border-radius: 6px; border: 1px solid #45475a;
            pointer-events: none; line-height: 1.6; min-width: 260px;
            backdrop-filter: blur(4px);
        `;
        el.textContent = 'Collecting stats...';
        document.body.appendChild(el);
        session.statsOverlayEl = el;
    },

    _removeStatsOverlay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        if (session.statsOverlayEl) { session.statsOverlayEl.remove(); session.statsOverlayEl = null; }
    },

    dispose(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        this._removeStatsOverlay(sessionId);
        this.sessions.delete(sessionId);
        console.log(`[Video] Session ${sessionId} disposed`);
    },

    /// Returns [width, height] of the available display area in physical pixels (for resolution negotiation).
    /// Reads the parent container size, not the canvas — canvas may be smaller after encodeInfo convergence.
    getDisplaySize(canvasId) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) return [0, 0];
        const container = canvas.parentElement;
        const rect = container.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        return [Math.round(rect.width * dpr), Math.round(rect.height * dpr)];
    },

    /// Set capture aspect ratio for pre-encodeInfo AR constraint.
    /// Once 1:1 path takes over (encodeW set), this is cleared automatically.
    setCaptureAspectRatio(sessionId, w, h) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session.captureW = w;
        session.captureH = h;
        // Only apply AR constraint if 1:1 path hasn't set explicit dims yet
        const canvas = session.canvas;
        if (canvas && !session.encodeW) {
            canvas.style.aspectRatio = `${w} / ${h}`;
            canvas.style.height = 'auto';
            canvas.style.maxHeight = '100%';
        }
        console.log(`[Video] Session ${sessionId}: captureAspectRatio ${w}/${h}`);
    },

    /// Set the host's actual encode resolution for a session.
    /// Stores dims only — CSS resize deferred to 1:1 render path so CSS always matches actual frame bitmap.
    setEncodeResolution(sessionId, encW, encH) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session.encodeW = encW;
        session.encodeH = encH;
        console.log(`[Video] Session ${sessionId}: encodeResolution ${encW}x${encH}`);
    },

    /// Start a debounced resize listener that sends resolution updates via postMessage.
    /// The C# side picks this up through the InputMessageRouter.
    _resizeDebounceTimer: null,
    _resizeCanvasId: null,
    startResizeListener(canvasId) {
        this._resizeCanvasId = canvasId;
        // Remove any previous listener
        if (this._resizeHandler) window.removeEventListener('resize', this._resizeHandler);

        this._resizeHandler = () => {
            clearTimeout(this._resizeDebounceTimer);
            this._resizeDebounceTimer = setTimeout(() => {
                // Read parent container (available space), not canvas (may be converged smaller)
                const canvas = document.getElementById(this._resizeCanvasId);
                if (!canvas) return;
                const container = canvas.parentElement;
                const rect = container.getBoundingClientRect();
                const dpr = window.devicePixelRatio || 1;
                const w = Math.round(rect.width * dpr);
                const h = Math.round(rect.height * dpr);
                if (w > 0 && h > 0 && window.chrome?.webview) {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'resolution', width: w, height: h
                    }));
                    console.log(`[Video] Resize → resolution ${w}x${h}`);
                }
            }, 300); // 300ms debounce
        };
        window.addEventListener('resize', this._resizeHandler);
        console.log(`[Video] Resize listener started for ${canvasId}`);
    }
};

function fmtBytes(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
    if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(1)} MB`;
    return `${(bytes / 1073741824).toFixed(2)} GB`;
}

// === SharedBuffer Receiver (decoded BGRA frames from C# FFmpeg decoder) ===
if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('sharedbufferreceived', (e) => {
        try {
            const meta = e.additionalData;
            const buf = e.getBuffer();

            const session = window.SteamViewerVideo.sessions.get(meta.sid);
            if (!session || !session.canvas || !session.ctx) {
                chrome.webview.releaseBuffer(buf);
                return;
            }

            if (meta.lossless) {
                // Lossless QOI-decoded BGRA frame — paint into existing canvas (no resize!)
                // Dimensions match H.264 (viewer requests at decoder resolution), so
                // we paint exactly like the last H.264 frame but with nearest-neighbor.
                const bgraCopy = new Uint8Array(buf, 0, meta.len).slice();
                chrome.webview.releaseBuffer(buf);

                const frame = new VideoFrame(bgraCopy, {
                    format: 'BGRA',
                    codedWidth: meta.w,
                    codedHeight: meta.h,
                    timestamp: performance.now() * 1000,
                });

                const canvas = session.canvas;
                const ctx = session.ctx;

                // Paint into current canvas — never resize (prevents jitter)
                const savedSmoothing = ctx.imageSmoothingEnabled;
                ctx.imageSmoothingEnabled = false;

                if (session.isDownscaling) {
                    // Canvas is at display pixels — use same letterbox as H.264
                    const scale = Math.min(canvas.width / meta.w, canvas.height / meta.h);
                    const fitW = Math.round(meta.w * scale);
                    const fitH = Math.round(meta.h * scale);
                    const dx = Math.round((canvas.width - fitW) / 2);
                    const dy = Math.round((canvas.height - fitH) / 2);
                    if (dx > 0 || dy > 0) ctx.clearRect(0, 0, canvas.width, canvas.height);
                    ctx.drawImage(frame, dx, dy, fitW, fitH);
                } else {
                    // Canvas is at video resolution — draw 1:1
                    ctx.drawImage(frame, 0, 0);
                }

                ctx.imageSmoothingEnabled = savedSmoothing;
                frame.close();
                session.frameCount++;
                return;
            }

            if (meta.raw) {
                // Raw BGRA path — VideoFrame directly from pixel data
                const bgraCopy = new Uint8Array(buf, 0, meta.len).slice();
                chrome.webview.releaseBuffer(buf);

                const frame = new VideoFrame(bgraCopy, {
                    format: 'BGRA',
                    codedWidth: meta.w,
                    codedHeight: meta.h,
                    timestamp: performance.now() * 1000,
                });

                const canvas = session.canvas;
                const ctx = session.ctx;

                // Update session video dims for mouse coordinate mapping
                if (session.videoW !== meta.w || session.videoH !== meta.h) {
                    session.videoW = meta.w;
                    session.videoH = meta.h;
                }

                // When encodeResolution is known, canvas = video resolution → 1:1 drawImage.
                // CSS object-fit: contain handles display scaling (GPU compositor).
                // Host already downscaled to near-display size, so CSS upscale is ≤7px (imperceptible).
                const use1to1 = session.encodeW > 0 && session.encodeH > 0 &&
                    meta.w === session.encodeW && meta.h === session.encodeH;

                if (use1to1) {
                    // 1:1 pixel-perfect: canvas bitmap = encode resolution
                    if (canvas.width !== meta.w || canvas.height !== meta.h) {
                        canvas.width = meta.w;
                        canvas.height = meta.h;
                    }
                    // Sync CSS to bitmap — deferred from setEncodeResolution so CSS
                    // never leads the frame data (eliminates race → no fractional smear)
                    const dpr = window.devicePixelRatio || 1;
                    const targetCssW = `${meta.w / dpr}px`;
                    const targetCssH = `${meta.h / dpr}px`;
                    if (canvas.style.width !== targetCssW || canvas.style.height !== targetCssH) {
                        canvas.style.width = targetCssW;
                        canvas.style.height = targetCssH;
                        canvas.style.objectFit = '';
                        canvas.style.aspectRatio = '';
                        canvas.style.maxHeight = '';
                    }
                    ctx.drawImage(frame, 0, 0);
                    session.isDownscaling = false;
                } else {
                    // Fallback: display-pixel canvas with Canvas 2D scaling
                    // (before encodeInfo arrives, or if frame doesn't match encode resolution)
                    const rect = canvas.getBoundingClientRect();
                    const dpr = window.devicePixelRatio || 1;
                    const displayW = Math.round(rect.width * dpr);
                    const displayH = Math.round(rect.height * dpr);
                    const scale = Math.min(displayW / meta.w, displayH / meta.h);
                    const isDownscaling = scale < 0.95;

                    if (isDownscaling) {
                        const displayChanged = displayW !== session.lastDisplayW || displayH !== session.lastDisplayH;
                        if (canvas.width !== displayW || canvas.height !== displayH || displayChanged) {
                            canvas.width = displayW;
                            canvas.height = displayH;
                            ctx.imageSmoothingEnabled = true;
                            ctx.imageSmoothingQuality = 'high';
                            session.lastDisplayW = displayW;
                            session.lastDisplayH = displayH;
                        }
                        const fitW = Math.round(meta.w * scale);
                        const fitH = Math.round(meta.h * scale);
                        const dx = Math.round((displayW - fitW) / 2);
                        const dy = Math.round((displayH - fitH) / 2);
                        if (dx > 0 || dy > 0) ctx.clearRect(0, 0, displayW, displayH);
                        ctx.drawImage(frame, dx, dy, fitW, fitH);
                        session.isDownscaling = true;
                    } else {
                        if (canvas.width !== meta.w || canvas.height !== meta.h) {
                            canvas.width = meta.w;
                            canvas.height = meta.h;
                            session.lastDisplayW = 0;
                            session.lastDisplayH = 0;
                        }
                        ctx.drawImage(frame, 0, 0);
                        session.isDownscaling = false;
                    }
                }
                frame.close();
            } else {
                // JPEG fallback (if ever needed)
                const jpegBytes = new Uint8Array(buf, 0, meta.len).slice();
                chrome.webview.releaseBuffer(buf);

                const blob = new Blob([jpegBytes], { type: 'image/jpeg' });
                createImageBitmap(blob).then(bitmap => {
                    if (!session.canvas || !session.ctx) { bitmap.close(); return; }

                    const canvas = session.canvas;
                    const ctx = session.ctx;
                    session.videoW = meta.w;
                    session.videoH = meta.h;

                    const rect = canvas.getBoundingClientRect();
                    const dpr = window.devicePixelRatio || 1;
                    const displayW = Math.round(rect.width * dpr);
                    const displayH = Math.round(rect.height * dpr);
                    const scale = Math.min(displayW / meta.w, displayH / meta.h);

                    if (scale < 0.95) {
                        if (canvas.width !== displayW || canvas.height !== displayH) {
                            canvas.width = displayW; canvas.height = displayH;
                            ctx.imageSmoothingEnabled = true;
                            ctx.imageSmoothingQuality = 'high';
                        }
                        const fitW = Math.round(meta.w * scale);
                        const fitH = Math.round(meta.h * scale);
                        const dx = Math.round((displayW - fitW) / 2);
                        const dy = Math.round((displayH - fitH) / 2);
                        if (dx > 0 || dy > 0) ctx.clearRect(0, 0, displayW, displayH);
                        ctx.drawImage(bitmap, dx, dy, fitW, fitH);
                    } else {
                        if (canvas.width !== meta.w || canvas.height !== meta.h) {
                            canvas.width = meta.w; canvas.height = meta.h;
                        }
                        ctx.drawImage(bitmap, 0, 0);
                    }
                    bitmap.close();
                }).catch(() => {});
            }

            session.frameCount++;

            // Report dimensions on first frame
            if (session.frameCount === 1 && session.dotNetRef) {
                try {
                    session.dotNetRef.invokeMethodAsync('OnVideoStartedCallback');
                    console.log(`[Video] First frame: ${meta.w}x${meta.h}`);
                } catch (err) { console.warn('OnVideoStartedCallback failed:', err); }
            }
        } catch (err) {
            console.warn('SharedBuffer frame error:', err);
        }
    });
    console.log('[SharedBuffer] Video receiver registered');
}

// === Input Capture (mouse/keyboard) ===
// Ported from webrtc-interop.js SteamViewerInput — adapted for FFmpeg transport
// (no SteamViewerWebRTC references, uses SteamViewerVideo for letterbox/session data)
window.SteamViewerInput = {
    canvas: null,
    isCapturing: false,
    isLocked: false,
    showLockIndicator: false,
    lockIndicator: null,
    _activeSessionId: null,
    _inputEventCount: 0,
    _rawEventCount: 0,
    _lastMouseDownCoords: null,
    _coordLogCount: 0,
    _focusWatchdogId: null,

    // Bound handler references (for proper removal)
    _boundMouseMove: null,
    _boundMouseDown: null,
    _boundMouseUp: null,
    _boundWheel: null,
    _boundKeyDown: null,
    _boundKeyUp: null,

    // PID mouse regulation tuning
    _pidAlpha: 0.35,
    _pidKp: 0.6,
    _pidKi: 0.15,
    _pidKd: 0.3,
    _pidSendThreshold: 2.5,
    _pidIDecay: 0.85,
    _pidIMax: 50,
    _pidIdleThresholdMs: 100,
    _pidColdStartBurst: 3,
    // Dynamic cooldown — timer send rate scales with velocity
    _pidMinCooldown: 16,
    _pidMaxCooldown: 100,
    _pidVelocityCap: 2.0,
    // PID internal state
    _pidVelocity: 0,
    _pidLastVelocity: 0,
    _pidLastEventTime: 0,
    _pidIntegral: 0,
    _pidColdStartRemaining: 0,
    _lastTimerSendTime: 0,
    _lastSentX: 0,
    _lastSentY: 0,
    _bufferedMouseCoords: null,
    _regulationTimer: null,
    // Remote cursor shape from host
    _remoteCursorShape: 'default',

    // === Lifecycle ===

    initialize(canvasId, options = {}) {
        const { showLockIndicator = true } = options;
        const foundCanvas = document.getElementById(canvasId);

        // Clean up any previous instance
        if (this.canvas) {
            this.stop();
        }

        this.canvas = foundCanvas;
        this.showLockIndicator = showLockIndicator;

        if (!this.canvas) {
            console.error(`Canvas '${canvasId}' not found`);
            return false;
        }

        // Create lock indicator overlay (optional)
        if (showLockIndicator) {
            this.createLockIndicator();
        }

        // Bind event handlers (store references for removal)
        this._boundMouseMove = (e) => this.handleMouseMove(e);
        this._boundMouseDown = (e) => this.handleMouseDown(e);
        this._boundMouseUp = (e) => this.handleMouseUp(e);
        this._boundWheel = (e) => this.handleWheel(e);
        this._boundKeyDown = (e) => this.handleKeyDown(e);
        this._boundKeyUp = (e) => this.handleKeyUp(e);

        // Mouse events on canvas
        this.canvas.addEventListener('mousemove', this._boundMouseMove);
        this.canvas.addEventListener('mousedown', this._boundMouseDown);
        this.canvas.addEventListener('mouseup', this._boundMouseUp);
        this.canvas.addEventListener('wheel', this._boundWheel, { passive: false });
        this.canvas.addEventListener('contextmenu', (e) => e.preventDefault());

        // Make canvas focusable for keyboard events
        this.canvas.tabIndex = 0;
        this.canvas.style.outline = 'none';
        this.canvas.addEventListener('keydown', this._boundKeyDown);
        this.canvas.addEventListener('keyup', this._boundKeyUp);

        this.isCapturing = true;
        this.isLocked = false;
        this._inputEventCount = 0;
        this._rawEventCount = 0;
        this._coordLogCount = 0;

        // Focus canvas immediately so keyboard events are captured from the start
        this.canvas.focus();
        setTimeout(() => { this.canvas?.focus(); }, 200);
        setTimeout(() => { this.canvas?.focus(); }, 500);

        // Start periodic focus watchdog (restores focus if lost while locked)
        this._startFocusWatchdog();

        console.log(`[Input] Initialized on ${canvasId} (lockIndicator=${showLockIndicator})`);
        return true;
    },

    stop() {
        this.isCapturing = false;
        this.isLocked = false;

        // Stop focus watchdog
        if (this._focusWatchdogId) {
            clearInterval(this._focusWatchdogId);
            this._focusWatchdogId = null;
        }

        // Remove lock indicator
        if (this.lockIndicator) {
            this.lockIndicator.remove();
            this.lockIndicator = null;
        }

        // Clear regulation timer
        this._stopRegulationTimer();

        // Remove event listeners from canvas
        if (this.canvas) {
            this.canvas.removeEventListener('mousemove', this._boundMouseMove);
            this.canvas.removeEventListener('mousedown', this._boundMouseDown);
            this.canvas.removeEventListener('mouseup', this._boundMouseUp);
            this.canvas.removeEventListener('wheel', this._boundWheel);
            this.canvas.removeEventListener('keydown', this._boundKeyDown);
            this.canvas.removeEventListener('keyup', this._boundKeyUp);
        }

        this.canvas = null;
        this._activeSessionId = null;
        console.log('[Input] Stopped');
    },

    setActiveSession(sessionId) {
        this._activeSessionId = sessionId;
    },

    // === Lock / Unlock ===

    lock() {
        this.ensureCanvas();
        this.isLocked = true;
        this.updateLockIndicator();
        this.notifyLockChange();
        this.canvas.focus();
        // Apply remote cursor shape — shows the host's current cursor type locally
        this.canvas.style.cursor = this._remoteCursorShape || 'default';
        // Start mouse regulation interval timer (sweep mode sends)
        this._startRegulationTimer();
        console.log('[Input] LOCKED');
    },

    unlock() {
        this.isLocked = false;
        this.updateLockIndicator();
        this.notifyLockChange();
        this._stopRegulationTimer();
        // Restore default cursor
        if (this.canvas) this.canvas.style.cursor = '';
        console.log('[Input] UNLOCKED');
    },

    notifyLockChange() {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'input', method: 'lockChanged', locked: this.isLocked
            }));
        }
    },

    // === Lock Indicator ===

    createLockIndicator() {
        this.lockIndicator = document.createElement('div');
        this.lockIndicator.id = 'inputLockIndicator';
        this.lockIndicator.style.cssText = `
            position: fixed; top: 10px; right: 10px; padding: 8px 16px;
            border-radius: 4px; font-size: 12px; font-family: sans-serif;
            z-index: 9999; pointer-events: none; transition: all 0.2s;
        `;
        this.updateLockIndicator();
        document.body.appendChild(this.lockIndicator);
    },

    updateLockIndicator() {
        if (!this.lockIndicator || !this.showLockIndicator) return;
        if (this.isLocked) {
            this.lockIndicator.textContent = 'Input Locked';
            this.lockIndicator.style.background = '#a6e3a1';
            this.lockIndicator.style.color = '#1e1e2e';
        } else {
            this.lockIndicator.textContent = 'Input Unlocked';
            this.lockIndicator.style.background = '#45475a';
            this.lockIndicator.style.color = '#cdd6f4';
        }
    },

    // === Focus Watchdog ===

    _startFocusWatchdog() {
        if (this._focusWatchdogId) clearInterval(this._focusWatchdogId);
        this._focusWatchdogId = setInterval(() => {
            if (this.isLocked && this.canvas && document.activeElement !== this.canvas) {
                const active = document.activeElement;
                if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA'
                    || active.tagName === 'SELECT' || active.closest('.menu-dropdown')
                    || active.closest('.connection-dialog'))) {
                    return;
                }
                this.canvas.focus();
            }
        }, 500);
    },

    // === Canvas DOM Recovery ===

    _reattachCount: 0,
    reattachIfNeeded() {
        this._reattachCount++;
        const current = document.getElementById('viewerCanvas');
        const same = current === this.canvas;
        if (this._reattachCount <= 5 || !same) {
            console.log(`[Input] reattachIfNeeded #${this._reattachCount}: canvasSame=${same}, capturing=${this.isCapturing}, locked=${this.isLocked}`);
        }
        this.ensureCanvas();
        if (this.canvas) {
            this.canvas.focus();
        }
        if (!this._focusWatchdogId && this.isCapturing) {
            this._startFocusWatchdog();
        }
    },

    ensureCanvas() {
        const current = document.getElementById('viewerCanvas');
        if (current && current !== this.canvas) {
            console.warn('[Input] Canvas DOM node changed — re-attaching listeners');
            if (this.canvas) {
                this.canvas.removeEventListener('mousemove', this._boundMouseMove);
                this.canvas.removeEventListener('mousedown', this._boundMouseDown);
                this.canvas.removeEventListener('mouseup', this._boundMouseUp);
                this.canvas.removeEventListener('keydown', this._boundKeyDown);
                this.canvas.removeEventListener('keyup', this._boundKeyUp);
            }
            this.canvas = current;
            this.canvas.addEventListener('mousemove', this._boundMouseMove);
            this.canvas.addEventListener('mousedown', this._boundMouseDown);
            this.canvas.addEventListener('mouseup', this._boundMouseUp);
            this.canvas.addEventListener('wheel', this._boundWheel, { passive: false });
            this.canvas.addEventListener('contextmenu', (e) => e.preventDefault());
            this.canvas.tabIndex = 0;
            this.canvas.style.outline = 'none';
            this.canvas.addEventListener('keydown', this._boundKeyDown);
            this.canvas.addEventListener('keyup', this._boundKeyUp);
            this.isCapturing = true;
            this.canvas.focus();
        } else if (!this.canvas && this._activeSessionId) {
            this.canvas = document.getElementById('viewerCanvas');
        }
    },

    // === PID Regulation Timer ===

    _startRegulationTimer() {
        this._stopRegulationTimer();
        this._pidVelocity = 0;
        this._pidIntegral = 0;
        this._pidLastVelocity = 0;
        this._pidLastEventTime = 0;
        this._lastTimerSendTime = 0;
        this._pidColdStartRemaining = 0;
        this._regulationTimer = setInterval(() => {
            const c = this._bufferedMouseCoords;
            if (!c || !window.chrome?.webview || !this.isLocked) return;

            const now = performance.now();
            const elapsed = now - this._lastTimerSendTime;
            // Dynamic cooldown: scales linearly with velocity
            const t = Math.min(this._pidVelocity / this._pidVelocityCap, 1);
            const cooldown = this._pidMinCooldown + (this._pidMaxCooldown - this._pidMinCooldown) * t;

            if (elapsed < cooldown) return;

            this._lastSentX = c.x;
            this._lastSentY = c.y;
            this._bufferedMouseCoords = null;
            this._lastTimerSendTime = now;
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'input', method: 'mouseMove',
                x: c.x, y: c.y, captureW: c.captureW, captureH: c.captureH
            }));
        }, 16); // Poll at 60Hz, actual send rate governed by dynamic cooldown
    },

    _stopRegulationTimer() {
        if (this._regulationTimer) {
            clearInterval(this._regulationTimer);
            this._regulationTimer = null;
        }
        this._bufferedMouseCoords = null;
        this._pidVelocity = 0;
        this._pidIntegral = 0;
        this._pidLastVelocity = 0;
        this._pidLastEventTime = 0;
        this._lastTimerSendTime = 0;
    },

    // === Coordinate Mapping ===

    getModifiers(e) {
        return { ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey, meta: e.metaKey };
    },

    _getMouseCoords(e) {
        const sd = window.SteamViewerSecureDesktop;
        const sdActive = sd?.isActive && sd._width && sd._height && sd.canvas;
        if (sdActive) {
            // SD canvas may be sized to display pixels (downscaling) — need letterbox mapping
            const canvas = sd.canvas;
            const rect = canvas.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;
            const displayW = Math.round(rect.width * dpr);
            const displayH = Math.round(rect.height * dpr);
            const scale = Math.min(displayW / sd._width, displayH / sd._height);
            if (scale < 0.95 && canvas.width === displayW && canvas.height === displayH) {
                // Downscale: canvas = display pixels, SD frame drawn with letterbox
                const canvasX = (e.clientX - rect.left) * dpr;
                const canvasY = (e.clientY - rect.top) * dpr;
                const fitW = sd._width * scale;
                const fitH = sd._height * scale;
                const dx = (displayW - fitW) / 2;
                const dy = (displayH - fitH) / 2;
                return {
                    x: Math.max(0, Math.min(sd._width, (canvasX - dx) * sd._width / fitW)),
                    y: Math.max(0, Math.min(sd._height, (canvasY - dy) * sd._height / fitH)),
                    captureW: sd._width, captureH: sd._height
                };
            }
            const coords = this.getScaledCoordsForCanvas(e, canvas);
            return { x: coords.x, y: coords.y, captureW: sd._width, captureH: sd._height };
        }
        const coords = this.getScaledCoords(e);
        const session = window.SteamViewerVideo.sessions.get(this._activeSessionId);
        return {
            x: coords.x, y: coords.y,
            captureW: session?.videoW || (this.canvas?.width || 1920),
            captureH: session?.videoH || (this.canvas?.height || 1080)
        };
    },

    getScaledCoords(e) {
        const session = window.SteamViewerVideo.sessions.get(this._activeSessionId);
        const canvas = session?.canvas || this.canvas;

        if (session?.isDownscaling && session.videoW && session.videoH) {
            // Downscale mode: canvas bitmap = display pixels, video drawn with letterbox inside.
            // Need to map mouse CSS position → canvas pixel → video pixel.
            const rect = canvas.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;
            const canvasX = (e.clientX - rect.left) * dpr;
            const canvasY = (e.clientY - rect.top) * dpr;
            // Recompute letterbox (same math as render path)
            const scale = Math.min(canvas.width / session.videoW, canvas.height / session.videoH);
            const fitW = session.videoW * scale;
            const fitH = session.videoH * scale;
            const dx = (canvas.width - fitW) / 2;
            const dy = (canvas.height - fitH) / 2;
            // Canvas pixel → video pixel
            return {
                x: Math.max(0, Math.min(session.videoW, (canvasX - dx) * session.videoW / fitW)),
                y: Math.max(0, Math.min(session.videoH, (canvasY - dy) * session.videoH / fitH))
            };
        }

        // Upscale/1:1: canvas = video resolution, CSS object-fit handles display
        return this.getScaledCoordsForCanvas(e, canvas);
    },

    getScaledCoordsForCanvas(e, canvas) {
        if (!canvas) return { x: 0, y: 0 };
        const rect = canvas.getBoundingClientRect();
        const canvasAspect = canvas.width / canvas.height;
        const rectAspect = rect.width / rect.height;
        let renderWidth, renderHeight, offsetX, offsetY;
        if (rectAspect > canvasAspect) {
            renderHeight = rect.height; renderWidth = rect.height * canvasAspect;
            offsetX = (rect.width - renderWidth) / 2; offsetY = 0;
        } else {
            renderWidth = rect.width; renderHeight = rect.width / canvasAspect;
            offsetX = 0; offsetY = (rect.height - renderHeight) / 2;
        }
        const relX = e.clientX - rect.left - offsetX;
        const relY = e.clientY - rect.top - offsetY;
        return {
            x: Math.max(0, Math.min(canvas.width, relX * canvas.width / renderWidth)),
            y: Math.max(0, Math.min(canvas.height, relY * canvas.height / renderHeight))
        };
    },

    // === Mouse Handlers ===

    handleMouseMove(e) {
        this._rawEventCount++;
        if (!this.isCapturing || !this.isLocked || !window.chrome?.webview) return;
        this._inputEventCount++;
        this.ensureCanvas();
        this._lastMouseDownCoords = null;

        const { x, y, captureW, captureH } = this._getMouseCoords(e);

        // PID regulation
        const now = performance.now();
        let dt = now - this._pidLastEventTime;
        if (dt <= 0) dt = 1;

        if (dt > this._pidIdleThresholdMs) {
            this._pidColdStartRemaining = this._pidColdStartBurst;
        }

        const rawVelocity = Math.hypot(e.movementX, e.movementY) / dt;
        const velocity = this._pidAlpha * rawVelocity + (1 - this._pidAlpha) * this._pidVelocity;
        const P = this._pidKp * velocity;
        this._pidIntegral = Math.min(this._pidIntegral * this._pidIDecay + velocity * dt, this._pidIMax);
        const I = this._pidKi * this._pidIntegral;
        const D = this._pidKd * (velocity - this._pidLastVelocity) / dt;
        const score = P + I + D;

        this._pidLastVelocity = velocity;
        this._pidVelocity = velocity;
        this._pidLastEventTime = now;

        const coldStart = this._pidColdStartRemaining > 0;
        if (coldStart) this._pidColdStartRemaining--;

        if (coldStart || score < this._pidSendThreshold) {
            this._lastSentX = x; this._lastSentY = y;
            this._bufferedMouseCoords = null;
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'input', method: 'mouseMove', x, y, captureW, captureH
            }));
        } else {
            this._bufferedMouseCoords = { x, y, captureW, captureH };
        }
    },

    handleMouseDown(e) {
        if (!this.isCapturing || !this.isLocked || !window.chrome?.webview) return;
        e.preventDefault();
        const { x, y, captureW, captureH } = this._getMouseCoords(e);
        const button = ['left', 'middle', 'right', 'XButton1', 'XButton2'][e.button] || 'left';
        this._lastMouseDownCoords = { x, y, captureW, captureH, button };
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'input', method: 'mouseDown', button, x, y, captureW, captureH
        }));
    },

    handleMouseUp(e) {
        if (!this.isCapturing || !this.isLocked || !window.chrome?.webview) return;
        e.preventDefault();
        const button = ['left', 'middle', 'right', 'XButton1', 'XButton2'][e.button] || 'left';
        let x, y, captureW, captureH;
        const cached = this._lastMouseDownCoords;
        if (cached && cached.button === button) {
            x = cached.x; y = cached.y; captureW = cached.captureW; captureH = cached.captureH;
        } else {
            ({ x, y, captureW, captureH } = this._getMouseCoords(e));
        }
        this._lastMouseDownCoords = null;
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'input', method: 'mouseUp', button, x, y, captureW, captureH
        }));
    },

    handleWheel(e) {
        if (!this.isCapturing || !this.isLocked || !window.chrome?.webview) return;
        e.preventDefault();
        let dx = e.deltaX, dy = e.deltaY;
        if (e.deltaMode === 1) { dx *= 40; dy *= 40; }
        else if (e.deltaMode === 2) { dx *= 800; dy *= 800; }
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'input', method: 'mouseWheel', deltaX: dx, deltaY: dy
        }));
    },

    // === Keyboard Handlers ===

    handleKeyDown(e) {
        if (!this.isCapturing || !this.isLocked || !window.chrome?.webview) return;
        e.preventDefault();

        // Ctrl+V → clipboard paste through C#
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'v' || e.key === 'V')) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'input', method: 'clipboardPaste'
            }));
            return;
        }

        // Ctrl+C/X → send keystroke (host clipboard request handled by menu button)
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'input', method: 'keyDown',
            key: e.key, modifiers: this.getModifiers(e)
        }));
    },

    handleKeyUp(e) {
        if (!this.isCapturing || !this.isLocked || !window.chrome?.webview) return;
        e.preventDefault();
        window.chrome.webview.postMessage(JSON.stringify({
            type: 'input', method: 'keyUp',
            key: e.key, modifiers: this.getModifiers(e)
        }));
    }
};

// === Secure Desktop Overlay (kept for UAC/lock screen viewing) ===
window.SteamViewerSecureDesktop = {
    isActive: false,
    canvas: null,
    ctx: null,
    _width: 0,
    _height: 0,
    _cursorX: 0,
    _cursorY: 0,

    show(canvasId) {
        this.canvas = document.getElementById(canvasId);
        if (this.canvas) {
            this.ctx = this.canvas.getContext('2d', { alpha: false });
            this.isActive = true;
            console.log('[SD] Overlay shown');
        }
    },

    hide(canvasId) {
        this.isActive = false;
        this.canvas = null;
        this.ctx = null;
        console.log('[SD] Overlay hidden');
    }
};

console.log('[video-interop.js] Loaded — FFmpeg transport mode');
