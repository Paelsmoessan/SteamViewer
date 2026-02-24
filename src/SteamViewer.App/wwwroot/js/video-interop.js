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
        session.ctx = canvas.getContext('2d');
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
                const dpr = window.devicePixelRatio || 1;
                const rect = canvas.getBoundingClientRect();
                const cw = Math.round(rect.width * dpr);
                const ch = Math.round(rect.height * dpr);

                // Resize canvas bitmap if container changed
                if (canvas.width !== cw || canvas.height !== ch) {
                    canvas.width = cw;
                    canvas.height = ch;
                    session.lastCanvasW = cw;
                    session.lastCanvasH = ch;
                }

                // Recompute letterbox if video dims or canvas size changed
                if (session.videoW !== meta.w || session.videoH !== meta.h ||
                    session.lastCanvasW !== cw || session.lastCanvasH !== ch) {
                    session.letterbox = computeLetterbox(cw, ch, meta.w, meta.h);
                    session.videoW = meta.w;
                    session.videoH = meta.h;
                    session.lastCanvasW = cw;
                    session.lastCanvasH = ch;
                }

                const lb = session.letterbox;
                ctx.fillStyle = '#000';
                ctx.fillRect(0, 0, cw, ch);
                ctx.drawImage(frame, lb.dx, lb.dy, lb.dw, lb.dh);
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
                    const dpr = window.devicePixelRatio || 1;
                    const rect = canvas.getBoundingClientRect();
                    const cw = Math.round(rect.width * dpr);
                    const ch = Math.round(rect.height * dpr);

                    if (canvas.width !== cw || canvas.height !== ch) {
                        canvas.width = cw; canvas.height = ch;
                    }

                    const lb = computeLetterbox(cw, ch, meta.w, meta.h);
                    session.letterbox = lb;
                    session.videoW = meta.w;
                    session.videoH = meta.h;

                    ctx.fillStyle = '#000';
                    ctx.fillRect(0, 0, cw, ch);
                    ctx.drawImage(bitmap, lb.dx, lb.dy, lb.dw, lb.dh);
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
window.SteamViewerInput = {
    canvas: null,
    dotNetRef: null,
    isCapturing: false,
    isLocked: false,
    _activeSessionId: null,
    _inputEventCount: 0,
    _rawEventCount: 0,
    _lastMouseDownCoords: null,
    _coordLogCount: 0,

    // PID mouse regulation state
    _pidVelocity: 0,
    _pidLastVelocity: 0,
    _pidLastEventTime: 0,
    _pidIntegral: 0,
    _pidColdStartRemaining: 0,
    _bufferedMouseCoords: null,
    _lastSentX: 0,
    _lastSentY: 0,
    // PID tuning parameters
    _pidKp: 0.6,
    _pidKi: 0.15,
    _pidKd: 0.3,
    _pidAlpha: 0.35,
    _pidIDecay: 0.85,
    _pidIMax: 50,
    _pidSendThreshold: 2.5,
    _pidIdleThresholdMs: 100,
    _pidColdStartBurst: 3,
    _pidFlushIntervalMs: 8,
    _pidFlushTimer: null,

    setCapturing(canvasId, dotNetRef, sessionId) {
        this.canvas = document.getElementById(canvasId);
        this.dotNetRef = dotNetRef;
        this._activeSessionId = sessionId;
        this.isCapturing = true;
        this._inputEventCount = 0;
        this._rawEventCount = 0;
        this._coordLogCount = 0;

        if (this.canvas) {
            this.canvas.addEventListener('mousedown', (e) => this.handleMouseDown(e));
            this.canvas.addEventListener('mouseup', (e) => this.handleMouseUp(e));
            this.canvas.addEventListener('mousemove', (e) => this.handleMouseMove(e));
            this.canvas.addEventListener('wheel', (e) => this.handleWheel(e), { passive: false });
            this.canvas.addEventListener('contextmenu', (e) => e.preventDefault());
            document.addEventListener('keydown', (e) => this.handleKeyDown(e));
            document.addEventListener('keyup', (e) => this.handleKeyUp(e));
        }

        // PID flush timer — sends buffered mouse coords at regular intervals
        this._pidFlushTimer = setInterval(() => {
            if (this._bufferedMouseCoords && this.dotNetRef) {
                const c = this._bufferedMouseCoords;
                this._bufferedMouseCoords = null;
                this._lastSentX = c.x;
                this._lastSentY = c.y;
                this.dotNetRef.invokeMethodAsync('OnMouseMove', c.x, c.y, c.captureW, c.captureH).catch(() => {});
            }
        }, this._pidFlushIntervalMs);

        console.log(`[Input] Capture started on ${canvasId} for session ${sessionId}`);
    },

    stopCapturing() {
        this.isCapturing = false;
        this.isLocked = false;
        if (this._pidFlushTimer) { clearInterval(this._pidFlushTimer); this._pidFlushTimer = null; }
        console.log('[Input] Capture stopped');
    },

    setLocked(locked) {
        this.isLocked = locked;
    },

    ensureCanvas() {
        if (!this.canvas && this._activeSessionId) {
            this.canvas = document.getElementById('viewerCanvas');
        }
    },

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
        const lb = session?.letterbox;
        return {
            x: coords.x, y: coords.y,
            captureW: lb?.videoW || (this.canvas?.width || 1920),
            captureH: lb?.videoH || (this.canvas?.height || 1080)
        };
    },

    getScaledCoords(e) {
        const session = window.SteamViewerVideo.sessions.get(this._activeSessionId);
        if (session?.letterbox?.videoW > 0) {
            const lb = session.letterbox;
            const canvas = session.canvas || this.canvas;
            const rect = canvas.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;
            const bitmapX = (e.clientX - rect.left) * dpr;
            const bitmapY = (e.clientY - rect.top) * dpr;
            const relX = bitmapX - lb.dx;
            const relY = bitmapY - lb.dy;
            return {
                x: Math.max(0, Math.min(lb.videoW, relX * lb.videoW / lb.dw)),
                y: Math.max(0, Math.min(lb.videoH, relY * lb.videoH / lb.dh))
            };
        }
        return this.getScaledCoordsForCanvas(e, this.canvas);
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
        catch (e) {}
    },

    handleContextMenu(e) { e.preventDefault(); },

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
                catch (e2) {}
            }
            return;
        }

        // Ctrl+C/X → send keystroke, then request host clipboard
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'c' || e.key === 'x')) {
            try {
                await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
                // Request host clipboard after short delay
                setTimeout(() => {
                    this.dotNetRef?.invokeMethodAsync('OnClipboardRequest').catch(() => {});
                }, 150);
            } catch (err) {}
            return;
        }

        try { await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e)); }
        catch (err) {}
    },

    async handleKeyUp(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();
        try { await this.dotNetRef.invokeMethodAsync('OnKeyUp', e.key, this.getModifiers(e)); }
        catch (err) {}
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
            this.ctx = this.canvas.getContext('2d');
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
