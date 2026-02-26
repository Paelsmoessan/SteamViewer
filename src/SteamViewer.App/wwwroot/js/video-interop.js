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
                this.dotNetRef.invokeMethodAsync('OnLog', level, this.peerName, message).catch(() => {});
            }
        } catch (e) {}
    },

    handleRelayedLog(level, message, from) {
        console.log(`[${from}] ${message}`);
        try {
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync('OnLog', level, from, message).catch(() => {});
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
        if (this.sessions.has(sessionId)) return;
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
            const lines = [
                `Video: ${s.fps?.toFixed(0) || '?'} FPS | ${s.bitrateMbps?.toFixed(1) || '?'} Mbps | ${s.resolution || '?'}`,
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

                // Set canvas bitmap to video resolution — drawImage is 1:1 (zero interpolation).
                // CSS object-fit: contain handles display scaling (single GPU-accelerated step).
                if (canvas.width !== meta.w || canvas.height !== meta.h) {
                    canvas.width = meta.w;
                    canvas.height = meta.h;
                }

                // Update session video dims for mouse coordinate mapping
                if (session.videoW !== meta.w || session.videoH !== meta.h) {
                    session.videoW = meta.w;
                    session.videoH = meta.h;
                }

                // 1:1 draw — no scaling, no letterbox (CSS handles both)
                ctx.drawImage(frame, 0, 0);
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

                    // Match canvas bitmap to video resolution (1:1 draw, CSS scales)
                    if (canvas.width !== meta.w || canvas.height !== meta.h) {
                        canvas.width = meta.w; canvas.height = meta.h;
                    }
                    session.videoW = meta.w;
                    session.videoH = meta.h;

                    ctx.drawImage(bitmap, 0, 0);
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
    dotNetRef: null,
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

    initialize(canvasId, dotNetReference, options = {}) {
        const { showLockIndicator = true } = options;

        // Clean up any previous instance
        if (this.canvas) {
            this.stop();
        }

        this.canvas = document.getElementById(canvasId);
        this.dotNetRef = dotNetReference;
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
        this.dotNetRef = null;
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
        if (this.dotNetRef) {
            try {
                this.dotNetRef.invokeMethodAsync('OnInputLockChanged', this.isLocked);
            } catch (e) { /* disposed */ }
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
        this._regulationTimer = setInterval(async () => {
            const c = this._bufferedMouseCoords;
            if (!c || !this.dotNetRef || !this.isLocked) return;

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
            try {
                await this.dotNetRef.invokeMethodAsync('OnMouseMove', c.x, c.y, c.captureW, c.captureH);
            } catch (err) { /* ignore sweep send errors */ }
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
            const coords = this.getScaledCoordsForCanvas(e, sd.canvas);
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
        // Canvas bitmap = video resolution, CSS object-fit: contain handles display.
        // getScaledCoordsForCanvas computes the CSS-level object-fit offset correctly.
        const session = window.SteamViewerVideo.sessions.get(this._activeSessionId);
        const canvas = session?.canvas || this.canvas;
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

    async handleMouseMove(e) {
        this._rawEventCount++;
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
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
            try { await this.dotNetRef.invokeMethodAsync('OnMouseMove', x, y, captureW, captureH); }
            catch (err) { console.error('[Input] OnMouseMove failed:', err); }
        } else {
            this._bufferedMouseCoords = { x, y, captureW, captureH };
        }
    },

    async handleMouseDown(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();
        const { x, y, captureW, captureH } = this._getMouseCoords(e);
        const button = ['left', 'middle', 'right', 'XButton1', 'XButton2'][e.button] || 'left';
        this._lastMouseDownCoords = { x, y, captureW, captureH, button };
        try { await this.dotNetRef.invokeMethodAsync('OnMouseDown', button, x, y, captureW, captureH); }
        catch (err) { console.error('[Input] OnMouseDown failed:', err); }
    },

    async handleMouseUp(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
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
        try { await this.dotNetRef.invokeMethodAsync('OnMouseUp', button, x, y, captureW, captureH); }
        catch (err) { console.error('[Input] OnMouseUp failed:', err); }
    },

    async handleWheel(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();
        let dx = e.deltaX, dy = e.deltaY;
        if (e.deltaMode === 1) { dx *= 40; dy *= 40; }
        else if (e.deltaMode === 2) { dx *= 800; dy *= 800; }
        try { await this.dotNetRef.invokeMethodAsync('OnMouseWheel', dx, dy); }
        catch (e) { /* disposed */ }
    },

    // === Keyboard Handlers ===

    async handleKeyDown(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();

        // Ctrl+V → clipboard paste through C#
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'v' || e.key === 'V')) {
            try {
                await this.dotNetRef.invokeMethodAsync('OnClipboardPaste');
            } catch (err) {
                try { await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e)); }
                catch (e2) { /* disposed */ }
            }
            return;
        }

        // Ctrl+C/X → send keystroke, then request host clipboard
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'c' || e.key === 'x')) {
            try {
                await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
                setTimeout(() => {
                    this.dotNetRef?.invokeMethodAsync('OnClipboardRequest').catch(() => {});
                }, 150);
            } catch (err) { /* disposed */ }
            return;
        }

        try { await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e)); }
        catch (err) { /* disposed */ }
    },

    async handleKeyUp(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();
        try { await this.dotNetRef.invokeMethodAsync('OnKeyUp', e.key, this.getModifiers(e)); }
        catch (err) { /* disposed */ }
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

    activate(canvasId) {
        this.canvas = document.getElementById(canvasId);
        if (this.canvas) {
            this.ctx = this.canvas.getContext('2d', { alpha: false });
            this.isActive = true;
            console.log('[SD] Activated');
        }
    },

    deactivate() {
        this.isActive = false;
        this.canvas = null;
        this.ctx = null;
        console.log('[SD] Deactivated');
    },

    renderFrame(base64Jpeg, width, height) {
        if (!this.isActive || !this.ctx) return;
        this._width = width;
        this._height = height;
        if (this.canvas.width !== width) this.canvas.width = width;
        if (this.canvas.height !== height) this.canvas.height = height;

        const img = new Image();
        img.onload = () => {
            this.ctx.drawImage(img, 0, 0, width, height);
            this._drawCursor();
        };
        img.src = 'data:image/jpeg;base64,' + base64Jpeg;
    },

    _drawCursor() {
        if (!this.ctx) return;
        const x = this._cursorX, y = this._cursorY;
        this.ctx.save();
        this.ctx.fillStyle = '#fff';
        this.ctx.beginPath();
        this.ctx.moveTo(x, y);
        this.ctx.lineTo(x, y + 18);
        this.ctx.lineTo(x + 5, y + 14);
        this.ctx.lineTo(x + 10, y + 20);
        this.ctx.lineTo(x + 13, y + 18);
        this.ctx.lineTo(x + 8, y + 12);
        this.ctx.lineTo(x + 14, y + 12);
        this.ctx.closePath();
        this.ctx.fill();
        this.ctx.strokeStyle = '#000';
        this.ctx.lineWidth = 1;
        this.ctx.stroke();
        this.ctx.restore();
    }
};

console.log('[video-interop.js] Loaded — FFmpeg transport mode');
