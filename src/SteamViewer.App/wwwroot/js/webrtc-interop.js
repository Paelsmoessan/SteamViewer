// WebRTC Interop for SteamViewer
// Provides browser WebRTC API access to Blazor via JS interop
// Multi-session architecture: each connection identified by sessionId

// Console interceptor - forwards all console output to C# for file logging
(function() {
    const originalLog = console.log;
    const originalWarn = console.warn;
    const originalError = console.error;

    function formatArgs(args) {
        return Array.from(args).map(arg => {
            if (typeof arg === 'object') {
                try { return JSON.stringify(arg); }
                catch { return String(arg); }
            }
            return String(arg);
        }).join(' ');
    }

    console.log = function(...args) {
        originalLog.apply(console, args);
        window.SteamViewerLogger?.log('INFO', formatArgs(args));
    };

    console.warn = function(...args) {
        originalWarn.apply(console, args);
        window.SteamViewerLogger?.log('WARN', formatArgs(args));
    };

    console.error = function(...args) {
        originalError.apply(console, args);
        window.SteamViewerLogger?.log('ERROR', formatArgs(args));
    };
})();

// Global error handlers - captured by console interceptor above → written to log file
window.onerror = function(msg, src, line, col, error) {
    console.error(`[Uncaught] ${msg} at ${src}:${line}:${col}`);
    return false;
};
window.addEventListener('unhandledrejection', function(e) {
    console.error(`[UnhandledPromise] ${e.reason}`);
});

// Logger bridge to C# with bidirectional WebRTC relay
window.SteamViewerLogger = {
    dotNetRef: null,
    peerName: 'LOCAL',      // Name for this peer (HOST/VIEWER/custom)
    relayEnabled: false,    // Enable log relay through WebRTC

    initialize(dotNetReference) {
        this.dotNetRef = dotNetReference;
        console.log('JS Logger initialized - logs will be written to file');
    },

    setMode(isHost, customName = null) {
        this.peerName = customName || (isHost ? 'HOST' : 'VIEWER');
        this.relayEnabled = true;
        console.log(`Logger mode: ${this.peerName} (bidirectional relay enabled)`);
    },

    log(level, message) {
        // Always log locally via C#
        if (this.dotNetRef) {
            try {
                this.dotNetRef.invokeMethodAsync('OnJSLog', level, message);
            } catch (e) {
                // Ignore if C# not ready
            }
        }

        // Relay to all active sessions (bidirectional - both host and viewer send)
        if (this.relayEnabled) {
            try {
                const logMsg = JSON.stringify({
                    _logRelay: true,
                    level,
                    message,
                    from: this.peerName,
                    timestamp: Date.now()
                });
                // Send through all sessions' data channels
                for (const [, session] of window.SteamViewerWebRTC.sessions) {
                    if (session.dataChannel?.readyState === 'open') {
                        try { session.dataChannel.send(logMsg); } catch (e) { /* ignore */ }
                    }
                }
            } catch (e) {
                // Ignore relay errors
            }
        }
    },

    // Called when receiving a relayed log from peer
    handleRelayedLog(level, message, from) {
        if (this.dotNetRef) {
            try {
                const prefix = from ? `[${from}]` : '[REMOTE]';
                this.dotNetRef.invokeMethodAsync('OnJSLog', level, `${prefix} ${message}`);
            } catch (e) {
                // Ignore
            }
        }
    }
};

// Compute letterbox/pillarbox geometry for fitting video into canvas
function computeLetterbox(canvasW, canvasH, videoW, videoH) {
    if (!videoW || !videoH) return { dx: 0, dy: 0, dw: canvasW, dh: canvasH, videoW: 0, videoH: 0 };
    const canvasAspect = canvasW / canvasH;
    const videoAspect = videoW / videoH;
    let dx, dy, dw, dh;
    if (canvasAspect > videoAspect) {
        // Canvas wider than video — pillarbox (bars on sides)
        dh = canvasH;
        dw = canvasH * videoAspect;
        dx = (canvasW - dw) / 2;
        dy = 0;
    } else {
        // Canvas taller than video — letterbox (bars top/bottom)
        dw = canvasW;
        dh = canvasW / videoAspect;
        dx = 0;
        dy = (canvasH - dh) / 2;
    }
    return { dx, dy, dw, dh, videoW, videoH };
}

window.SteamViewerWebRTC = {
    // Session-keyed Map: sessionId → session state object
    sessions: new Map(),

    // Custom TURN server config (shared across all sessions)
    customTurnServer: null,

    // Get session by ID (throws if not found)
    _getSession(sessionId) {
        const s = this.sessions.get(sessionId);
        if (!s) throw new Error(`No session: ${sessionId}`);
        return s;
    },

    // Create a new session state object with all per-session fields
    _createSessionState(dotNetRef) {
        return {
            peerConnection: null,
            dataChannel: null,
            dotNetRef: dotNetRef,
            localStream: null,
            remoteVideo: null,
            remoteCanvas: null,
            remoteCtx: null,
            // Frame capture
            frameCaptureDotNetRef: null,
            frameCaptureEnabled: false,
            frameCaptureAnimationId: null,
            lastFrameTime: 0,
            frameInterval: 33, // ~30fps (match WebRTC source)
            captureCanvas: null,
            captureCtx: null,
            // Direct rendering (bypasses JPEG relay when canvas is in same JS context)
            _directRenderCanvas: null,
            _directRenderCtx: null,
            _letterbox: { dx: 0, dy: 0, dw: 0, dh: 0, videoW: 0, videoH: 0 },
            _resizeObserver: null,
            // Stats overlay
            _statsInterval: null,
            _statsOverlayEl: null,
            _statsVisible: false,
            _statsPrev: null,
            _inputEventsCount: 0,
            _inputThrottledCount: 0,
            _qualityMode: 'HQ',
            _statsRelay: false,
            // Screen sharing recovery
            _sharingStoppedByUser: false,
            _sharingLost: false,           // true when track ended unexpectedly (restart on next user gesture)
            _restartingShare: false,       // true while restart attempt is in progress (prevents concurrent restarts)
            // Dynamic bitrate adaptation — disabled, let WebRTC handle rate control natively.
            // Our adaptation was fighting WebRTC's BWE, capping bitrate at ~3Mbps on LAN.
            _bitrateAdaptEnabled: false,
            _targetBitrate: 50_000_000,
            _minBitrate: 500_000,
            _maxBitrate: 50_000_000,
            _bitrateHistory: [],
            _lastBitrateAdjust: 0,
            // Silent audio track (separate MSID to bypass Chrome A/V sync)
            _silentAudioCtx: null,
            // LAN detection (observation only — no renegotiation)
            _isLan: false,
            // Dual data channels: control (reliable) + mouse (unreliable)
            mouseChannel: null
        };
    },

    // Set custom TURN server configuration (shared, no sessionId needed)
    // Call this before initialize() to use your own TURN server
    setTurnConfig(urls, username, credential) {
        console.log('Setting custom TURN server:', urls);
        this.customTurnServer = { urls, username, credential };
    },

    // Build ICE servers list (shared helper)
    buildIceServers() {
        const servers = [
            // Multiple STUN servers for reliability
            { urls: 'stun:stun.l.google.com:19302' },
            { urls: 'stun:stun1.l.google.com:19302' },
            { urls: 'stun:stun2.l.google.com:19302' },
            { urls: 'stun:stun3.l.google.com:19302' },
            { urls: 'stun:stun4.l.google.com:19302' },
        ];

        // Use custom TURN server if configured
        if (this.customTurnServer && this.customTurnServer.urls && this.customTurnServer.urls.length > 0) {
            console.log('Using custom TURN server');
            for (const url of this.customTurnServer.urls) {
                servers.push({
                    urls: url,
                    username: this.customTurnServer.username,
                    credential: this.customTurnServer.credential
                });
            }
        } else {
            console.log('No custom TURN server configured - P2P only (may fail over internet)');
        }

        return servers;
    },

    // Initialize WebRTC with STUN/TURN servers for a specific session
    async initialize(sessionId, dotNetReference) {
        const session = this._createSessionState(dotNetReference);

        const iceServers = this.buildIceServers();
        console.log(`=== WebRTC INIT [${sessionId}] ===`);
        console.log('ICE servers configured:', JSON.stringify(iceServers, null, 2));

        const config = {
            iceServers,
            iceCandidatePoolSize: 25,
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            iceTransportPolicy: 'all'
        };

        try {
            session.peerConnection = new RTCPeerConnection(config);
            console.log(`[${sessionId}] RTCPeerConnection created`);

            // Track candidate types found
            const candidateTypes = { host: 0, srflx: 0, relay: 0, prflx: 0 };

            // Handle ICE candidates
            session.peerConnection.onicecandidate = async (event) => {
                if (event.candidate) {
                    const candidateType = event.candidate.candidate.match(/typ (\w+)/)?.[1] || 'unknown';
                    candidateTypes[candidateType] = (candidateTypes[candidateType] || 0) + 1;

                    // Full candidate logging for debugging
                    console.log(`=== ICE CANDIDATE [${sessionId}]: ${candidateType.toUpperCase()} ===`);
                    console.log('Full candidate:', event.candidate.candidate);
                    console.log('Candidate counts so far:', candidateTypes);

                    if (candidateType === 'relay') {
                        console.log('*** RELAY CANDIDATE FOUND - TURN SERVER WORKING! ***');
                    }

                    if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnIceCandidateCallback', JSON.stringify(event.candidate));
                } else {
                    console.log(`=== ICE GATHERING COMPLETE [${sessionId}] ===`);
                    console.log('Final candidate counts:', candidateTypes);
                    if (candidateTypes.relay === 0) {
                        console.error('!!! NO RELAY CANDIDATES - TURN SERVER NOT WORKING !!!');
                        console.error('Check: 1) TURN server running 2) Correct port 3) Credentials match');
                    }
                }
            };

            // Handle ICE gathering state
            session.peerConnection.onicegatheringstatechange = () => {
                console.log(`[${sessionId}] ICE gathering state:`, session.peerConnection.iceGatheringState);
            };

            // Handle connection state changes
            session.peerConnection.onconnectionstatechange = async () => {
                const state = session.peerConnection.connectionState;
                console.log(`=== CONNECTION STATE [${sessionId}]:`, state, '===');

                if (state === 'connected') {
                    // Recovery from temporary disconnect — resume capture if it was paused
                    this.resumeFrameCapture(sessionId);
                    // Re-focus canvas on reconnect to ensure keyboard events work
                    if (window.SteamViewerInput && window.SteamViewerInput.canvas) {
                        window.SteamViewerInput.canvas.focus();
                    }
                } else if (state === 'failed') {
                    console.error('CONNECTION FAILED - Possible causes:');
                    console.error('1. No relay candidates (TURN not working)');
                    console.error('2. Firewall blocking');
                    console.error('3. NAT traversal failed');
                }

                // Handle disconnect/failure
                if (state === 'disconnected') {
                    // Temporary disconnect — pause capture but KEEP input locked
                    // ICE will usually reconnect within seconds
                    this.pauseFrameCapture(sessionId);
                    console.log('Temporary disconnect — input lock preserved');
                } else if (state === 'failed' || state === 'closed') {
                    // Permanent failure — full cleanup
                    this.stopFrameCapture(sessionId);
                    if (window.SteamViewerInput && window.SteamViewerInput.isLocked
                        && window.SteamViewerInput._activeSessionId === sessionId) {
                        window.SteamViewerInput.unlock();
                        console.log('Input lock released due to connection state:', state);
                    }
                }

                if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnConnectionStateChangeCallback', state);
            };

            // Handle ICE connection state for more debugging
            session.peerConnection.oniceconnectionstatechange = async () => {
                const state = session.peerConnection?.iceConnectionState;
                console.log(`[${sessionId}] ICE connection state:`, state);
                if (state === 'failed') {
                    console.error('ICE CONNECTION FAILED');
                }
                // LAN detection: observe candidate types for stats (no renegotiation — FEC removed in initial SDP)
                if (state === 'connected' && !session._isLan) {
                    try {
                        const stats = await session.peerConnection.getStats();
                        for (const [, report] of stats) {
                            if (report.type === 'candidate-pair' && report.state === 'succeeded') {
                                let localType = '', remoteType = '';
                                for (const [, r] of stats) {
                                    if (r.id === report.localCandidateId) localType = r.candidateType;
                                    if (r.id === report.remoteCandidateId) remoteType = r.candidateType;
                                }
                                session._isLan = (localType === 'host' && remoteType === 'host');
                                console.log(`[${sessionId}] ICE candidates: local=${localType}, remote=${remoteType} → ${session._isLan ? 'LAN' : 'WAN'}`);
                                break;
                            }
                        }
                    } catch (e) {
                        console.warn(`[${sessionId}] LAN detection failed:`, e);
                    }
                }
            };

            // Handle ICE candidate errors (ignore STUN timeouts on TURN servers - expected)
            session.peerConnection.onicecandidateerror = (event) => {
                // Error 701 = STUN timeout, often happens on TURN servers - not critical
                if (event.errorCode === 701 && event.url?.includes('relay.metered.ca')) {
                    // Silently ignore - Google STUN servers will handle NAT traversal
                    return;
                }
                console.error('ICE candidate error:', event.errorCode, event.errorText, event.url);
            };

            // Visibility change fallback — restart sharing when page becomes visible after lock screen
            const visibilityHandler = async () => {
                if (document.visibilityState !== 'visible') return;
                if (!session._sharingLost || session._sharingStoppedByUser || session._restartingShare) return;
                const pcState = session.peerConnection?.connectionState;
                if (pcState === 'closed' || pcState === 'failed') return;
                console.log('Page visible + sharing lost — attempting restart from visibilitychange');
                session._sharingLost = false;
                session._restartingShare = true;
                try {
                    const ok = await window.SteamViewerWebRTC.startScreenCapture(sessionId);
                    session._restartingShare = false;
                    if (!ok) session._sharingLost = true;
                } catch (e) {
                    session._restartingShare = false;
                    session._sharingLost = true;
                }
            };
            document.addEventListener('visibilitychange', visibilityHandler);
            session._visibilityHandler = visibilityHandler;

            // Handle renegotiation needed (when tracks are added after connection)
            session.peerConnection.onnegotiationneeded = async () => {
                console.log(`=== NEGOTIATION NEEDED [${sessionId}] (track added post-connection) ===`);
                try {
                    // Only renegotiate if we're in a stable state
                    if (session.peerConnection.signalingState === 'stable') {
                        const offer = await session.peerConnection.createOffer();
                        offer.sdp = this.modifySdpForLowLatency(offer.sdp);
                        await session.peerConnection.setLocalDescription(offer);
                        console.log('Renegotiation offer created, sending to peer...');
                        if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnRenegotiationNeededCallback', JSON.stringify(offer));
                    } else {
                        console.log('Skipping renegotiation, signaling state:', session.peerConnection.signalingState);
                    }
                } catch (err) {
                    console.error('Renegotiation failed:', err);
                }
            };

            // Handle incoming data channels (viewer receives host-created channels)
            session.peerConnection.ondatachannel = (event) => {
                if (event.channel.label === 'mouse') {
                    this._setupMouseChannel(sessionId, event.channel);
                } else if (event.channel.label === 'file') {
                    this._setupFileChannel(sessionId, event.channel);
                } else if (event.channel.label === 'file-data') {
                    this._setupFileDataChannel(sessionId, event.channel);
                } else {
                    this._setupDataChannel(sessionId, event.channel);
                }
            };

            // Handle incoming video track
            session.peerConnection.ontrack = (event) => {
                console.log(`=== ONTRACK EVENT [${sessionId}] ===`);
                console.log('Track kind:', event.track.kind);
                console.log('Track id:', event.track.id);
                console.log('Track readyState:', event.track.readyState);
                console.log('Streams:', event.streams.length);

                if (event.track.kind === 'video') {
                    console.log('Setting up video track...');

                    // Configure receiver for ultra-low latency (reduces jitter buffer)
                    const receiver = event.receiver;
                    if (receiver) {
                        if ('playoutDelayHint' in receiver) {
                            receiver.playoutDelayHint = 0;  // Minimum delay (browser enforces floor)
                            console.log('Set playoutDelayHint to 0 (ultra-low latency)');
                        }
                        if ('jitterBufferTarget' in receiver) {
                            receiver.jitterBufferTarget = 0;  // Request minimal jitter buffer
                            console.log('Set jitterBufferTarget to 0');
                        }
                    }

                    // Persistent jitter buffer pressure at 15ms interval (Selkies pattern).
                    // Chrome's adaptive algorithm regrows the buffer — 1s polling was too slow.
                    // Source: .claude/research/external-research (Selkies v1.6.0 uses 15ms)
                    if (!session._jbPressureInterval) {
                        session._jbPressureInterval = setInterval(() => {
                            if (!session.peerConnection) {
                                clearInterval(session._jbPressureInterval);
                                session._jbPressureInterval = null;
                                return;
                            }
                            const receivers = session.peerConnection.getReceivers();
                            for (const r of receivers) {
                                if (r.track?.kind === 'video') {
                                    if ('jitterBufferTarget' in r) r.jitterBufferTarget = 0;
                                    if ('playoutDelayHint' in r) r.playoutDelayHint = 0;
                                }
                            }
                        }, 15);
                    }

                    const video = document.createElement('video');
                    video.srcObject = event.streams[0];
                    video.autoplay = true;
                    video.muted = true;
                    video.playsInline = true;

                    // Low-latency hints to reduce video element buffering
                    video.disableRemotePlayback = true;  // No casting
                    video.preload = 'none';              // Don't pre-buffer
                    video.playbackRate = 1.0;            // Prevent adaptive playback adjustments
                    if ('preservesPitch' in video) {
                        video.preservesPitch = false;    // Skip audio pitch correction
                    }

                    // Store reference for debugging
                    session.remoteVideo = video;

                    // Log video events
                    video.onloadstart = () => console.log(`[${sessionId}] Video: loadstart`);
                    video.onloadeddata = () => console.log(`[${sessionId}] Video: loadeddata`);
                    video.oncanplay = () => console.log(`[${sessionId}] Video: canplay`);
                    video.onplaying = () => console.log(`[${sessionId}] Video: playing`);
                    video.onstalled = () => console.warn(`[${sessionId}] Video: STALLED`);
                    video.onerror = (e) => console.error(`[${sessionId}] Video ERROR:`, video.error);

                    // Track if we got any frames
                    let frameCount = 0;
                    let lastFrameTime = 0;

                    // Create offscreen canvas per session (no DOM dependency)
                    const setupCanvas = () => {
                        const canvas = document.createElement('canvas');
                        console.log(`[${sessionId}] Created offscreen canvas for video rendering`);
                        const ctx = canvas.getContext('2d');
                        session.remoteCanvas = canvas;
                        session.remoteCtx = ctx;

                        video.onloadedmetadata = () => {
                            const width = video.videoWidth;
                            const height = video.videoHeight;
                            console.log(`=== VIDEO METADATA LOADED [${sessionId}] ===`);
                            console.log(`Dimensions: ${width}x${height}`);

                            if (width === 0 || height === 0) {
                                console.error('Video has ZERO dimensions!');
                                return;
                            }

                            canvas.width = width;
                            canvas.height = height;

                            // Recompute letterbox when video resolution changes
                            if (session._directRenderCanvas && session._updateCanvasSize) {
                                session._updateCanvasSize();
                            }

                            // Use requestVideoFrameCallback for lower latency (renders when frame arrives, not on monitor refresh)
                            const renderFrameRVFC = (now, metadata) => {
                                if (video.paused || video.ended) {
                                    console.log('Video paused/ended, stopping render');
                                    return;
                                }

                                // Skip drawing if video not ready yet (avoids errors during startup)
                                if (video.readyState < 2) {
                                    video.requestVideoFrameCallback(renderFrameRVFC);
                                    return;
                                }

                                try {
                                    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);

                                    // Direct render: draw to visible canvas with letterbox (bypasses JPEG relay)
                                    if (session._directRenderCtx) {
                                        const dc = session._directRenderCanvas;
                                        const lb = session._letterbox;
                                        // Recompute letterbox if video resolution changed
                                        if (lb.videoW !== video.videoWidth || lb.videoH !== video.videoHeight) {
                                            if (session._updateCanvasSize) session._updateCanvasSize();
                                        }
                                        session._directRenderCtx.clearRect(0, 0, dc.width, dc.height);
                                        session._directRenderCtx.drawImage(video, lb.dx, lb.dy, lb.dw, lb.dh);

                                        // Notify C# about first direct-render frame (dismisses "Waiting for host screen" overlay)
                                        if (!session._firstDirectFrameNotified && session.dotNetRef) {
                                            session._firstDirectFrameNotified = true;
                                            session.dotNetRef.invokeMethodAsync('OnVideoStartedCallback');
                                        }
                                    }

                                    frameCount++;

                                    // Log frame rate every 2 seconds
                                    if (now - lastFrameTime > 2000) {
                                        const fps = frameCount / ((now - lastFrameTime) / 1000);
                                        const mode = session._directRenderCtx ? 'DIRECT' : 'RVFC';
                                        console.log(`Video rendering (${mode}): ${fps.toFixed(1)} FPS, ${frameCount} frames`);
                                        frameCount = 0;
                                        lastFrameTime = now;
                                    }
                                } catch (e) {
                                    console.error('Error drawing frame:', e);
                                }

                                video.requestVideoFrameCallback(renderFrameRVFC);
                            };

                            // Fallback for browsers without requestVideoFrameCallback
                            const renderFrameRAF = () => {
                                if (video.paused || video.ended) {
                                    console.log('Video paused/ended, stopping render');
                                    return;
                                }

                                if (video.readyState >= 2) {
                                    try {
                                        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);

                                        // Direct render: draw to visible canvas with letterbox
                                        if (session._directRenderCtx) {
                                            const dc = session._directRenderCanvas;
                                            const lb = session._letterbox;
                                            if (lb.videoW !== video.videoWidth || lb.videoH !== video.videoHeight) {
                                                if (session._updateCanvasSize) session._updateCanvasSize();
                                            }
                                            session._directRenderCtx.clearRect(0, 0, dc.width, dc.height);
                                            session._directRenderCtx.drawImage(video, lb.dx, lb.dy, lb.dw, lb.dh);

                                            // Notify C# about first direct-render frame
                                            if (!session._firstDirectFrameNotified && session.dotNetRef) {
                                                session._firstDirectFrameNotified = true;
                                                session.dotNetRef.invokeMethodAsync('OnVideoStartedCallback');
                                            }
                                        }

                                        frameCount++;

                                        // Log frame rate every 2 seconds
                                        const now = performance.now();
                                        if (now - lastFrameTime > 2000) {
                                            const fps = frameCount / ((now - lastFrameTime) / 1000);
                                            console.log(`Video rendering (RAF): ${fps.toFixed(1)} FPS, ${frameCount} frames`);
                                            frameCount = 0;
                                            lastFrameTime = now;
                                        }
                                    } catch (e) {
                                        console.error('Error drawing frame:', e);
                                    }
                                }
                                requestAnimationFrame(renderFrameRAF);
                            };

                            lastFrameTime = performance.now();

                            // Start rendering IMMEDIATELY (don't wait for play() promise)
                            // This eliminates 200-500ms latency from waiting for play() to resolve
                            if ('requestVideoFrameCallback' in HTMLVideoElement.prototype) {
                                console.log('Using requestVideoFrameCallback (low-latency mode)');
                                video.requestVideoFrameCallback(renderFrameRVFC);
                            } else {
                                console.log('Using requestAnimationFrame fallback');
                                renderFrameRAF();
                            }

                            // Play in background (don't block rendering)
                            video.play()
                                .then(() => {
                                    console.log('=== VIDEO PLAYBACK STARTED ===');
                                })
                                .catch(err => {
                                    console.error('Video play() FAILED:', err);
                                    // Try to auto-recover with user gesture workaround
                                    console.log('Attempting autoplay workaround...');
                                    video.muted = true;
                                    video.play().catch(e => console.error('Autoplay workaround failed:', e));
                                });
                        };

                        // If metadata already loaded (rare but possible)
                        if (video.readyState >= 1) {
                            console.log('Video metadata already available');
                            video.onloadedmetadata();
                        }
                    };

                    setupCanvas();
                }
            };

            // Store session in map
            this.sessions.set(sessionId, session);

            console.log(`[${sessionId}] WebRTC initialized successfully`);
            return true;
        } catch (err) {
            console.error(`[${sessionId}] Failed to initialize WebRTC:`, err);
            return false;
        }
    },

    // Create data channel (for host) — legacy single-channel
    createDataChannel(sessionId, name) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            console.error('PeerConnection not initialized');
            return;
        }

        session.dataChannel = session.peerConnection.createDataChannel(name, {
            ordered: true
        });
        this._setupDataChannel(sessionId, session.dataChannel);
        console.log(`[${sessionId}] Data channel '${name}' created`);
    },

    // Create dual data channels (for host): control (reliable) + mouse (unreliable)
    createDataChannels(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            console.error('PeerConnection not initialized');
            return;
        }

        // Control channel: ordered, reliable — keyboard, commands, clipboard text, SD frames
        session.dataChannel = session.peerConnection.createDataChannel('control', {
            ordered: true
        });
        this._setupDataChannel(sessionId, session.dataChannel);

        // Mouse channel: unordered, unreliable — mouse moves only, eliminates head-of-line blocking
        session.mouseChannel = session.peerConnection.createDataChannel('mouse', {
            ordered: false,
            maxRetransmits: 0
        });
        this._setupMouseChannel(sessionId, session.mouseChannel);

        // File channel: ordered, reliable — clipboard file signaling (FormatList, FileContentsRequest)
        session.fileChannel = session.peerConnection.createDataChannel('file', {
            ordered: true
        });
        this._setupFileChannel(sessionId, session.fileChannel);

        // File data channel: ordered, reliable — raw binary file content responses
        session.fileDataChannel = session.peerConnection.createDataChannel('file-data', {
            ordered: true
        });
        this._setupFileDataChannel(sessionId, session.fileDataChannel);

        console.log(`[${sessionId}] Quad data channels created (control + mouse + file + file-data)`);
    },

    _setupDataChannel(sessionId, channel) {
        const session = this._getSession(sessionId);
        session.dataChannel = channel;
        channel.binaryType = 'arraybuffer';

        channel.onopen = async () => {
            console.log(`[${sessionId}] Data channel opened`);
            if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnDataChannelOpenCallback');
        };

        channel.onclose = async () => {
            console.log(`[${sessionId}] Data channel closed`);
            if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnDataChannelCloseCallback');
        };

        channel.onmessage = async (event) => {
            if (typeof event.data === 'string') {
                // Check if this is a relayed log message or mode change from peer
                try {
                    const parsed = JSON.parse(event.data);
                    if (parsed._logRelay) {
                        window.SteamViewerLogger?.handleRelayedLog(parsed.level, parsed.message, parsed.from);
                        return;
                    }
                    // Cursor shape sync — apply CSS cursor directly in JS (no C# round-trip)
                    if (parsed.type === 'cursorShape' && parsed.cursor) {
                        window.SteamViewerInput._remoteCursorShape = parsed.cursor;
                        if (window.SteamViewerInput.isLocked && window.SteamViewerInput.canvas) {
                            window.SteamViewerInput.canvas.style.cursor = parsed.cursor;
                        }
                        return;
                    }
                    // Clipboard data from host — forwarded to C# for native Win32 clipboard write
                    if (parsed.type === 'clipboard_data' && parsed.data) {
                        console.log(`[${sessionId}] Clipboard data from host: ${parsed.data.length} chars (routing to C#)`);
                    }
                } catch (e) {
                    // Not JSON - continue normally
                }

                console.log(`[${sessionId}] Data channel message received:`, event.data?.substring?.(0, 50));
                if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnDataChannelMessageCallback', event.data);
            } else if (event.data instanceof ArrayBuffer) {
                console.log(`[${sessionId}] Data channel binary message received:`, event.data.byteLength, 'bytes');
                const uint8Array = new Uint8Array(event.data);
                if (session.dotNetRef) await session.dotNetRef.invokeMethodAsync('OnDataChannelBinaryMessageCallback', Array.from(uint8Array));
            }
        };

        channel.onerror = (event) => {
            const err = event.error;
            const detail = err ? `errorDetail=${err.errorDetail}, message=${err.message}, sctpCauseCode=${err.sctpCauseCode}, receivedAlert=${err.receivedAlert}, sentAlert=${err.sentAlert}` : 'no error object';
            console.error(`[${sessionId}] Data channel error: ${detail}`, event);
            window.SteamViewerLogger?.log('error', `Data channel error: ${detail}`);
        };
    },

    // Set up unreliable mouse channel (lightweight — no binary, messages route to same C# callback)
    _setupMouseChannel(sessionId, channel) {
        const session = this._getSession(sessionId);
        session.mouseChannel = channel;

        channel.onopen = () => {
            console.log(`[${sessionId}] Mouse channel opened`);
        };

        channel.onclose = () => {
            console.log(`[${sessionId}] Mouse channel closed`);
        };

        // Host receives mouse input through same C# callback as control channel
        channel.onmessage = async (event) => {
            if (typeof event.data === 'string' && session.dotNetRef) {
                await session.dotNetRef.invokeMethodAsync('OnDataChannelMessageCallback', event.data);
            }
        };

        channel.onerror = (event) => {
            const err = event.error;
            const detail = err ? `errorDetail=${err.errorDetail}, message=${err.message}, sctpCauseCode=${err.sctpCauseCode}` : 'no error object';
            console.error(`[${sessionId}] Mouse channel error: ${detail}`, event);
            window.SteamViewerLogger?.log('error', `Mouse channel error: ${detail}`);
        };
    },

    // Set up reliable file channel (clipboard file transfer — virtual file streaming)
    _setupFileChannel(sessionId, channel) {
        const session = this._getSession(sessionId);
        session.fileChannel = channel;
        channel.binaryType = 'arraybuffer';

        channel.onopen = () => {
            console.log(`[${sessionId}] File channel opened`);
        };

        channel.onclose = () => {
            console.log(`[${sessionId}] File channel closed`);
        };

        channel.onmessage = async (event) => {
            if (typeof event.data === 'string' && session.dotNetRef) {
                await session.dotNetRef.invokeMethodAsync('OnFileChannelMessageCallback', event.data);
            } else if (event.data instanceof ArrayBuffer && session.dotNetRef) {
                const uint8Array = new Uint8Array(event.data);
                await session.dotNetRef.invokeMethodAsync('OnFileChannelBinaryCallback', Array.from(uint8Array));
            }
        };

        channel.onerror = (event) => {
            const err = event.error;
            const detail = err ? `errorDetail=${err.errorDetail}, message=${err.message}` : 'no error object';
            console.error(`[${sessionId}] File channel error: ${detail}`, event);
        };
    },

    // Set up reliable binary file-data channel (raw file content responses)
    _setupFileDataChannel(sessionId, channel) {
        const session = this._getSession(sessionId);
        session.fileDataChannel = channel;
        channel.binaryType = 'arraybuffer';

        channel.onopen = () => {
            console.log(`[${sessionId}] File-data channel opened`);
        };

        channel.onclose = () => {
            console.log(`[${sessionId}] File-data channel closed`);
        };

        channel.onmessage = async (event) => {
            if (event.data instanceof ArrayBuffer && session.dotNetRef) {
                // Blazor JSInterop deserializes byte[] from base64 strings, not number arrays
                const uint8Array = new Uint8Array(event.data);
                let binary = '';
                for (let i = 0; i < uint8Array.length; i++) {
                    binary += String.fromCharCode(uint8Array[i]);
                }
                const base64 = btoa(binary);
                await session.dotNetRef.invokeMethodAsync('OnFileDataBinaryCallback', base64);
            }
        };

        channel.onerror = (event) => {
            const err = event.error;
            const detail = err ? `errorDetail=${err.errorDetail}, message=${err.message}` : 'no error object';
            console.error(`[${sessionId}] File-data channel error: ${detail}`, event);
        };
    },

    // Send string data over file channel (clipboard file signaling)
    sendFileChannelData(sessionId, data) {
        const session = this._getSession(sessionId);
        if (session.fileChannel && session.fileChannel.readyState === 'open') {
            session.fileChannel.send(data);
            return true;
        }
        return false;
    },

    // Send raw binary data over file-data channel (file content responses)
    // C# byte[] arrives as base64 string via JSInterop — decode to ArrayBuffer before sending
    sendFileDataBinary(sessionId, base64Data) {
        const session = this._getSession(sessionId);
        if (session.fileDataChannel && session.fileDataChannel.readyState === 'open') {
            const binary = atob(base64Data);
            const uint8Array = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) {
                uint8Array[i] = binary.charCodeAt(i);
            }
            session.fileDataChannel.send(uint8Array.buffer);
            return true;
        }
        return false;
    },

    // Send string data over data channel (control channel)
    sendData(sessionId, data) {
        const session = this._getSession(sessionId);
        if (session.dataChannel && session.dataChannel.readyState === 'open') {
            session.dataChannel.send(data);
            return true;
        }
        return false;
    },

    // Send binary data over data channel
    sendBinaryData(sessionId, data) {
        const session = this._getSession(sessionId);
        if (session.dataChannel && session.dataChannel.readyState === 'open') {
            const uint8Array = new Uint8Array(data);
            session.dataChannel.send(uint8Array.buffer);
            return true;
        }
        console.warn(`[${sessionId}] Data channel not open`);
        return false;
    },

    // Send mouse data over unreliable mouse channel (falls back to control channel)
    sendMouseData(sessionId, data) {
        const session = this._getSession(sessionId);
        // Prefer mouse channel (unordered, unreliable — no head-of-line blocking)
        if (session.mouseChannel && session.mouseChannel.readyState === 'open') {
            session.mouseChannel.send(data);
            return true;
        }
        // Fallback to control channel if mouse channel not yet open
        if (session.dataChannel && session.dataChannel.readyState === 'open') {
            session.dataChannel.send(data);
            return true;
        }
        return false;
    },

    // Modify SDP for lower latency (pure function, no session state)
    // SDP modifications for low-latency remote desktop streaming.
    // Source: .claude/research/webrtc-latency/research.md (Selkies, Hopp, Neko findings)
    modifySdpForLowLatency(sdp, { removeFec = false } = {}) {
        let modified = sdp;

        // Remove any existing b=AS lines (old 3Mbps cap that was killing bitrate)
        modified = modified.replace(/b=AS:\d+\r\n/g, '');

        // Set high bandwidth ceiling for LAN use (50 Mbps)
        modified = modified.replace(/(m=video[^\r\n]*\r\n)/g, '$1b=AS:50000\r\n');

        // Add x-google bitrate params to H264 fmtp lines — fast ramp-up, no slow BWE climb
        // start=10Mbps (instant quality), min=5Mbps (floor), max=50Mbps (ceiling)
        modified = modified.replace(
            /(a=fmtp:\d+ .*profile-level-id=[0-9a-fA-F]+)(?!.*x-google)/g,
            '$1;x-google-start-bitrate=10000;x-google-min-bitrate=5000;x-google-max-bitrate=50000'
        );

        // Add playout-delay RTP header extension — tells Chrome "render ASAP, don't buffer"
        // This is the sender-side hint that Selkies uses to eliminate jitter buffer latency
        if (!modified.includes('playout-delay')) {
            const extmapIds = [...modified.matchAll(/a=extmap:(\d+)/g)].map(m => parseInt(m[1]));
            const nextId = extmapIds.length > 0 ? Math.max(...extmapIds) + 1 : 13;
            modified = modified.replace(
                /(m=video[^\r\n]*\r\n(?:[^\r\n]*\r\n)*?)(a=rtpmap)/,
                `$1a=extmap:${nextId} http://www.webrtc.org/experiments/rtp-hdrext/playout-delay\r\n$2`
            );
        }

        // Prefer H264 Baseline profile (fastest encode/decode, most compatible)
        const lines = modified.split('\r\n');
        let h264PayloadTypes = [];
        lines.forEach(line => {
            if (line.includes('a=rtpmap:') && line.toLowerCase().includes('h264')) {
                const match = line.match(/a=rtpmap:(\d+)/);
                if (match) h264PayloadTypes.push(match[1]);
            }
        });
        if (h264PayloadTypes.length > 0) {
            modified = modified.replace(/(m=video \d+ [^ ]+ )(.+)/, (match, prefix, payloads) => {
                const payloadList = payloads.split(' ');
                const reordered = [...h264PayloadTypes, ...payloadList.filter(p => !h264PayloadTypes.includes(p))];
                return prefix + reordered.join(' ');
            });
        }

        // Remove FEC codecs (saves bandwidth + latency, not needed with 0% loss on LAN)
        if (removeFec) {
            // Collect payload types for FEC codecs before removing them
            const fecPTs = [];
            for (const m of modified.matchAll(/a=rtpmap:(\d+) (?:flexfec|ulpfec|red\/90000)/g)) {
                fecPTs.push(m[1]);
            }
            // Also collect RTX/apt payload types that reference FEC codecs
            for (const m of modified.matchAll(/a=fmtp:(\d+) apt=(\d+)/g)) {
                if (fecPTs.includes(m[2])) fecPTs.push(m[1]);
            }
            if (fecPTs.length > 0) {
                // Remove FEC payload types from m=video line
                modified = modified.replace(/(m=video \d+ [^ ]+) ([^\r\n]+)/, (match, prefix, payloads) => {
                    const filtered = payloads.split(' ').filter(pt => !fecPTs.includes(pt));
                    return prefix + ' ' + filtered.join(' ');
                });
                // Remove rtpmap, fmtp, and rtcp-fb lines for FEC payload types
                for (const pt of fecPTs) {
                    modified = modified.replace(new RegExp(`a=rtpmap:${pt} [^\\r\\n]*\\r\\n`, 'g'), '');
                    modified = modified.replace(new RegExp(`a=fmtp:${pt} [^\\r\\n]*\\r\\n`, 'g'), '');
                    modified = modified.replace(new RegExp(`a=rtcp-fb:${pt} [^\\r\\n]*\\r\\n`, 'g'), '');
                }
            }
        }

        return modified;
    },

    // Create SDP offer (for viewer initiating connection)
    async createOffer(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const offer = await session.peerConnection.createOffer({
            offerToReceiveVideo: true,
            offerToReceiveAudio: true
        });

        // Modify SDP for lower latency — removeFec in initial offer (no renegotiation needed)
        offer.sdp = this.modifySdpForLowLatency(offer.sdp, { removeFec: true });

        await session.peerConnection.setLocalDescription(offer);
        console.log(`=== SDP OFFER CREATED [${sessionId}] ===`);
        console.log('Has video:', offer.sdp.includes('m=video'));
        console.log('Has H264:', offer.sdp.toLowerCase().includes('h264'));
        console.log('Has VP8:', offer.sdp.toLowerCase().includes('vp8'));
        console.log('Has VP9:', offer.sdp.toLowerCase().includes('vp9'));
        return JSON.stringify(offer);
    },

    // Create SDP answer (for host responding to connection)
    async createAnswer(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const answer = await session.peerConnection.createAnswer();

        // Modify SDP for lower latency — removeFec in initial answer (no renegotiation needed)
        answer.sdp = this.modifySdpForLowLatency(answer.sdp, { removeFec: true });

        await session.peerConnection.setLocalDescription(answer);
        console.log(`[${sessionId}] SDP answer created`);
        return JSON.stringify(answer);
    },

    // Set remote SDP description
    async setRemoteDescription(sessionId, sdpJson) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const sdp = JSON.parse(sdpJson);
        console.log(`=== SETTING REMOTE DESCRIPTION [${sessionId}] ===`);
        console.log('Type:', sdp.type);
        console.log('Has video:', sdp.sdp?.includes('m=video'));
        console.log('Has H264:', sdp.sdp?.toLowerCase().includes('h264'));

        await session.peerConnection.setRemoteDescription(new RTCSessionDescription(sdp));
        console.log('Remote description set successfully');
        console.log('Signaling state:', session.peerConnection.signalingState);
    },

    // Add ICE candidate
    async addIceCandidate(sessionId, candidateJson) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const candidate = JSON.parse(candidateJson);
        await session.peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
        console.log(`[${sessionId}] ICE candidate added`);
    },

    // Start screen capture (for host)
    // Always captures primary monitor — no picker (remote desktop tool, not Teams)
    async startScreenCapture(sessionId) {
        const session = this._getSession(sessionId);
        session._sharingStoppedByUser = false;
        console.log(`=== Starting screen capture [${sessionId}] (always fullscreen) ===`);
        console.log('navigator.mediaDevices:', !!navigator.mediaDevices);
        console.log('getDisplayMedia:', !!navigator.mediaDevices?.getDisplayMedia);
        console.log('peerConnection:', !!session.peerConnection);
        console.log('peerConnection state:', session.peerConnection?.connectionState);
        console.log('signalingState:', session.peerConnection?.signalingState);

        if (!session.peerConnection) {
            console.error('Cannot start screen capture - no peer connection!');
            return false;
        }

        try {
            session.localStream = await navigator.mediaDevices.getDisplayMedia({
                video: {
                    displaySurface: 'monitor',
                    cursor: 'motion',
                    width: { ideal: 1920, max: 3840 },
                    height: { ideal: 1080, max: 2160 },
                    frameRate: { ideal: 30, max: 60 }
                },
                audio: false,
                preferCurrentTab: false,
                selfBrowserSurface: 'exclude',
                systemAudio: 'exclude',
                monitorTypeSurfaces: 'include'
            });

            // Add video track to peer connection with encoding parameters
            const videoTracks = session.localStream.getVideoTracks();
            console.log(`=== SCREEN CAPTURE: Found ${videoTracks.length} video track(s) ===`);

            videoTracks.forEach((track, index) => {
                console.log(`Track ${index}:`, {
                    id: track.id,
                    label: track.label,
                    readyState: track.readyState,
                    muted: track.muted,
                    enabled: track.enabled
                });

                // Log track settings
                const settings = track.getSettings();
                console.log(`Track ${index} settings:`, {
                    width: settings.width,
                    height: settings.height,
                    frameRate: settings.frameRate,
                    deviceId: settings.deviceId
                });

                // Set content hint for screen sharing (improves encoding for text/UI)
                if (track.contentHint !== undefined) {
                    track.contentHint = 'motion'; // Prioritize frame rate + low latency over text sharpness // Optimizes for sharp text/UI
                }

                const sender = session.peerConnection.addTrack(track, session.localStream);
                console.log('Track added to peer connection, sender:', !!sender);

                // Configure encoding for lower latency
                const params = sender.getParameters();
                if (!params.encodings) {
                    params.encodings = [{}];
                }
                params.encodings[0].maxBitrate = 50_000_000; // 50 Mbps ceiling — WebRTC BWE controls actual rate
                params.encodings[0].maxFramerate = 30;  // Match getDisplayMedia ideal (24 causes stuttering)
                params.encodings[0].priority = 'high';
                params.encodings[0].networkPriority = 'high';
                // Disable scalability for lower latency
                params.encodings[0].scalabilityMode = 'L1T1';
                // Maintain frame rate even if quality has to drop (better for responsiveness)
                params.encodings[0].degradationPreference = 'maintain-resolution';
                sender.setParameters(params).catch(e => console.warn('Could not set encoding params:', e));

                // Report actual capture dimensions to host C# (source of truth for coordinate mapping)
                if (settings.width && settings.height && session.dotNetRef) {
                    try {
                        session.dotNetRef.invokeMethodAsync('OnCaptureStartedCallback', settings.width, settings.height);
                    } catch (e) {
                        console.warn('Could not report capture dims:', e);
                    }
                }

                // Add silent audio track with SEPARATE MediaStream (different MSID)
                // This bypasses Chrome's RtpStreamsSynchronizer which adds A/V sync delay to video
                try {
                    const audioCtx = new AudioContext();
                    const oscillator = audioCtx.createOscillator();
                    const gain = audioCtx.createGain();
                    gain.gain.value = 0; // silent
                    oscillator.connect(gain);
                    const dest = audioCtx.createMediaStreamDestination();
                    gain.connect(dest);
                    oscillator.start();
                    const audioTrack = dest.stream.getAudioTracks()[0];
                    // Key: new MediaStream([audioTrack]) = different MSID than video stream
                    session.peerConnection.addTrack(audioTrack, new MediaStream([audioTrack]));
                    session._silentAudioCtx = audioCtx;
                    console.log('Silent audio track added (separate MSID for A/V sync bypass)');
                } catch (e) {
                    console.warn('Could not add silent audio track:', e);
                }

                // Handle track ending — retry with fullscreen constraints (keeps connection alive during lock screen)
                const restartHandler = async () => {
                    if (session._sharingStoppedByUser) {
                        console.log('Screen sharing stopped by user');
                        return;
                    }
                    if (session._restartingShare) {
                        console.log('Screen share restart already in progress');
                        return;
                    }
                    session._restartingShare = true;
                    session._sharingLost = false;
                    const delays = [2000, 3000, 5000, 8000, 12000];
                    console.log(`Screen sharing track ended unexpectedly — will retry ${delays.length} times`);

                    for (let attempt = 0; attempt < delays.length; attempt++) {
                        await new Promise(r => setTimeout(r, delays[attempt]));
                        if (session._sharingStoppedByUser) {
                            console.log('User stopped sharing during restart — aborting');
                            session._restartingShare = false;
                            return;
                        }
                        const pcState = session.peerConnection?.connectionState;
                        if (pcState === 'closed' || pcState === 'failed') {
                            console.log(`Peer connection ${pcState} — aborting restart`);
                            session._restartingShare = false;
                            return;
                        }
                        try {
                            console.log(`Auto-restart attempt ${attempt + 1}/${delays.length}...`);
                            const newStream = await navigator.mediaDevices.getDisplayMedia({
                                video: {
                                    displaySurface: 'monitor',
                                    cursor: 'motion',
                                    width: { ideal: 1920, max: 3840 },
                                    height: { ideal: 1080, max: 2160 },
                                    frameRate: { ideal: 30, max: 60 }
                                },
                                audio: false,
                                preferCurrentTab: false,
                                selfBrowserSurface: 'exclude',
                                systemAudio: 'exclude',
                                monitorTypeSurfaces: 'include'
                            });
                            const newTrack = newStream.getVideoTracks()[0];
                            const videoSender = session.peerConnection.getSenders()
                                .find(s => s.track?.kind === 'video' || s.track === null);
                            if (videoSender) {
                                await videoSender.replaceTrack(newTrack);
                                console.log(`Screen sharing auto-restarted via replaceTrack (attempt ${attempt + 1})`);
                            } else {
                                session.peerConnection.addTrack(newTrack, newStream);
                                console.log(`Screen sharing restarted via addTrack (attempt ${attempt + 1})`);
                            }
                            newTrack.onended = restartHandler;
                            session.localStream = newStream;
                            session._sharingLost = false;
                            session._restartingShare = false;

                            // Report updated capture dimensions after restart
                            const restartSettings = newTrack.getSettings();
                            if (restartSettings.width && restartSettings.height && session.dotNetRef) {
                                try {
                                    session.dotNetRef.invokeMethodAsync('OnCaptureStartedCallback',
                                        restartSettings.width, restartSettings.height);
                                } catch (e) { }
                            }
                            return; // Success
                        } catch (err) {
                            console.warn(`Auto-restart attempt ${attempt + 1}/${delays.length} failed: ${err.name}: ${err.message}`);
                        }
                    }
                    // All retries exhausted
                    console.error('Screen sharing auto-restart failed after all retries');
                    session._sharingLost = true;
                    session._restartingShare = false;
                    if (session.dotNetRef) {
                        try { session.dotNetRef.invokeMethodAsync('OnScreenShareLostCallback'); } catch (e) { }
                    }
                };
                track.onended = restartHandler;
            });

            console.log('Screen capture started');
            return true;
        } catch (err) {
            console.error('Screen capture failed:', err.name, err.message);
            if (err.name === 'NotAllowedError') {
                console.error('User denied screen capture permission or cancelled dialog');
            } else if (err.name === 'NotFoundError') {
                console.error('No screen available for capture');
            } else if (err.name === 'NotSupportedError') {
                console.error('Screen capture not supported in this browser/context');
            }
            return false;
        }
    },

    // Stop screen capture
    stopScreenCapture(sessionId) {
        const session = this._getSession(sessionId);
        session._sharingStoppedByUser = true;
        session._sharingLost = false;
        if (session.localStream) {
            session.localStream.getTracks().forEach(track => track.stop());
            session.localStream = null;
            console.log(`[${sessionId}] Screen capture stopped`);
        }
    },

    // --- Native DXGI Capture (canvas bridge) ---
    // Replaces getDisplayMedia() — no screen picker, programmatic monitor selection.
    // C# captures via DXGI Desktop Duplication → JPEG → pushNativeFrame() → hidden canvas
    // → captureStream() → MediaStream → WebRTC (browser handles H264 encoding)

    // Start native capture: use MediaStreamTrackGenerator (push-based, no canvas)
    // Fallback to canvas + captureStream(0) if MediaStreamTrackGenerator unavailable
    startNativeCapture(sessionId, fps) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            console.error(`[${sessionId}] Cannot start native capture - no peer connection`);
            return false;
        }

        fps = fps || 30;
        let track;
        let stream;

        // Prefer MediaStreamTrackGenerator (push-based, lower latency)
        if (typeof MediaStreamTrackGenerator !== 'undefined') {
            console.log(`[${sessionId}] Starting native DXGI capture (MediaStreamTrackGenerator, ${fps} FPS)`);
            const generator = new MediaStreamTrackGenerator({ kind: 'video' });
            session._trackGenerator = generator;
            session._trackWriter = generator.writable.getWriter();
            session._useTrackGenerator = true;
            track = generator;
            stream = new MediaStream([generator]);
        } else {
            // Fallback: canvas + captureStream(0) with manual requestFrame
            console.log(`[${sessionId}] Starting native DXGI capture (canvas fallback, ${fps} FPS)`);
            session._nativeCanvas = document.createElement('canvas');
            session._nativeCtx = session._nativeCanvas.getContext('2d');
            session._nativeStream = session._nativeCanvas.captureStream(0);
            session._useTrackGenerator = false;
            track = session._nativeStream.getVideoTracks()[0];
            stream = session._nativeStream;

            if (!track) {
                console.error(`[${sessionId}] captureStream produced no video track`);
                return false;
            }
        }

        // Optimize for screen content (sharp text/UI)
        track.contentHint = 'motion'; // Prioritize frame rate + low latency over text sharpness

        // Add to peer connection
        const sender = session.peerConnection.addTrack(track, stream);
        console.log(`[${sessionId}] Native capture track added to peer connection`);

        // Configure encoding parameters — high ceiling, let WebRTC self-regulate
        const params = sender.getParameters();
        if (!params.encodings) params.encodings = [{}];
        params.encodings[0].maxBitrate = 50_000_000; // 50 Mbps ceiling — WebRTC BWE controls actual rate
        params.encodings[0].maxFramerate = fps;
        params.encodings[0].priority = 'high';
        params.encodings[0].networkPriority = 'high';
        params.encodings[0].scalabilityMode = 'L1T1';
        params.encodings[0].degradationPreference = 'maintain-resolution';
        sender.setParameters(params).catch(e => console.warn('Could not set native capture encoding params:', e));

        // Add silent audio track with SEPARATE MediaStream (different MSID)
        // This bypasses Chrome's RtpStreamsSynchronizer which adds A/V sync delay to video
        try {
            const audioCtx = new AudioContext();
            const oscillator = audioCtx.createOscillator();
            const gain = audioCtx.createGain();
            gain.gain.value = 0; // silent
            oscillator.connect(gain);
            const dest = audioCtx.createMediaStreamDestination();
            gain.connect(dest);
            oscillator.start();
            const audioTrack = dest.stream.getAudioTracks()[0];
            session.peerConnection.addTrack(audioTrack, new MediaStream([audioTrack]));
            session._silentAudioCtx = audioCtx;
            console.log(`[${sessionId}] Silent audio track added (separate MSID for A/V sync bypass)`);
        } catch (e) {
            console.warn(`[${sessionId}] Could not add silent audio track:`, e);
        }

        session._nativeCaptureActive = true;
        session._nativeFrameCount = 0;
        session._nativeSender = sender;
        session._nativeStreamRef = stream;
        console.log(`[${sessionId}] Native DXGI capture ready (waiting for frames from C#)`);
        return true;
    },

    // Receive a JPEG frame from C# DXGI capture.
    // MediaStreamTrackGenerator path: JPEG → createImageBitmap → VideoFrame → writer (push-based)
    // Canvas fallback path: JPEG → createImageBitmap → drawImage → requestFrame()
    pushNativeFrame(sessionId, base64Jpeg, width, height) {
        const session = this.sessions.get(sessionId);
        if (!session || !session._nativeCaptureActive) return;

        // Decode base64 to binary ArrayBuffer
        const binary = atob(base64Jpeg);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        const blob = new Blob([bytes], { type: 'image/jpeg' });

        // GPU-accelerated JPEG decode → ImageBitmap
        createImageBitmap(blob).then(bitmap => {
            if (!session._nativeCaptureActive) { bitmap.close(); return; }

            if (session._useTrackGenerator && session._trackWriter) {
                // Push-based: ImageBitmap → VideoFrame → MediaStreamTrackGenerator → WebRTC
                const frame = new VideoFrame(bitmap, { timestamp: performance.now() * 1000 });
                bitmap.close();
                session._trackWriter.write(frame).catch(e => {
                    // Writer may be closed during shutdown — not an error
                    if (session._nativeCaptureActive) console.warn('TrackWriter error:', e);
                });
            } else if (session._nativeCanvas) {
                // Canvas fallback: drawImage + manual requestFrame
                const canvas = session._nativeCanvas;
                if (canvas.width !== width || canvas.height !== height) {
                    canvas.width = width;
                    canvas.height = height;
                }
                session._nativeCtx.drawImage(bitmap, 0, 0);
                bitmap.close();
                const track = session._nativeStream?.getVideoTracks()[0];
                if (track?.requestFrame) track.requestFrame();
            }
        }).catch(e => {
            if (session._nativeCaptureActive) console.warn('createImageBitmap error:', e);
        });

        session._nativeFrameCount = (session._nativeFrameCount || 0) + 1;

        // Report capture dimensions to C# on first frame (same as getDisplayMedia path)
        if (session._nativeFrameCount === 1 && session.dotNetRef) {
            try {
                session.dotNetRef.invokeMethodAsync('OnCaptureStartedCallback', width, height);
                console.log(`[${sessionId}] Native capture: reported dims ${width}x${height} to C#`);
            } catch (e) {
                console.warn('Could not report native capture dims:', e);
            }
        }
    },

    // Stop native DXGI capture and clean up
    stopNativeCapture(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;

        const frameCount = session._nativeFrameCount || 0;
        session._nativeCaptureActive = false;

        // Close MediaStreamTrackGenerator resources
        if (session._trackWriter) {
            session._trackWriter.close().catch(() => {});
            session._trackWriter = null;
        }
        if (session._trackGenerator) {
            session._trackGenerator.stop();
            session._trackGenerator = null;
        }

        // Stop stream tracks (canvas fallback or generator stream)
        if (session._nativeStreamRef) {
            session._nativeStreamRef.getTracks().forEach(t => t.stop());
            session._nativeStreamRef = null;
        }
        if (session._nativeStream) {
            session._nativeStream.getTracks().forEach(t => t.stop());
            session._nativeStream = null;
        }

        // Remove sender from peer connection
        if (session._nativeSender && session.peerConnection) {
            try {
                session.peerConnection.removeTrack(session._nativeSender);
            } catch (e) {
                console.warn(`[${sessionId}] Could not remove native capture sender:`, e);
            }
            session._nativeSender = null;
        }

        // Clean up canvas fallback resources
        session._nativeCanvas = null;
        session._nativeCtx = null;
        session._useTrackGenerator = false;

        console.log(`[${sessionId}] Native DXGI capture stopped (${frameCount} frames total)`);
        session._nativeFrameCount = 0;
    },

    // Pause video track sender (frees bandwidth for data channel during Secure Desktop)
    pauseVideoTrack(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) return;
        session.peerConnection.getSenders().forEach(s => {
            if (s.track?.kind === 'video') {
                s.track.enabled = false;
                console.log(`[${sessionId}] Video track paused`);
            }
        });
    },

    // Resume video track sender (Secure Desktop deactivated)
    resumeVideoTrack(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) return;
        session.peerConnection.getSenders().forEach(s => {
            if (s.track?.kind === 'video') {
                s.track.enabled = true;
                console.log(`[${sessionId}] Video track resumed`);
            }
        });
    },

    // Manually check for and setup any video tracks (call after renegotiation)
    checkForVideoTracks(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) return false;

        const receivers = session.peerConnection.getReceivers();
        const videoReceiver = receivers.find(r => r.track?.kind === 'video');

        if (videoReceiver && videoReceiver.track) {
            console.log(`=== MANUAL TRACK CHECK [${sessionId}]: Found video track ===`);
            const track = videoReceiver.track;

            // If we already have this track set up, skip
            if (session.remoteVideo?.srcObject?.getVideoTracks().includes(track)) {
                console.log('Track already set up');
                return true;
            }

            // Configure receiver for ultra-low latency (reduces jitter buffer)
            if ('playoutDelayHint' in videoReceiver) {
                videoReceiver.playoutDelayHint = 0;
                console.log('Manual setup: Set playoutDelayHint to 0');
            }
            if ('jitterBufferTarget' in videoReceiver) {
                videoReceiver.jitterBufferTarget = 0;
                console.log('Manual setup: Set jitterBufferTarget to 0');
            }

            // Start 15ms JB pressure interval if not already running
            if (!session._jbPressureInterval) {
                session._jbPressureInterval = setInterval(() => {
                    if (!session.peerConnection) {
                        clearInterval(session._jbPressureInterval);
                        session._jbPressureInterval = null;
                        return;
                    }
                    const recvs = session.peerConnection.getReceivers();
                    for (const r of recvs) {
                        if (r.track?.kind === 'video') {
                            if ('jitterBufferTarget' in r) r.jitterBufferTarget = 0;
                            if ('playoutDelayHint' in r) r.playoutDelayHint = 0;
                        }
                    }
                }, 15);
            }

            // Set up the video track (same logic as ontrack)
            console.log('Setting up video track manually');
            const stream = new MediaStream([track]);

            const video = document.createElement('video');
            video.srcObject = stream;
            video.autoplay = true;
            video.muted = true;
            video.playsInline = true;

            // Low-latency hints
            video.disableRemotePlayback = true;
            video.preload = 'none';
            video.playbackRate = 1.0;  // Prevent adaptive playback adjustments
            if ('preservesPitch' in video) {
                video.preservesPitch = false;
            }

            session.remoteVideo = video;

            const setupCanvas = () => {
                const canvas = document.createElement('canvas');
                console.log(`[${sessionId}] Created offscreen canvas for manual track setup`);
                const ctx = canvas.getContext('2d');
                session.remoteCanvas = canvas;
                session.remoteCtx = ctx;

                video.onloadedmetadata = () => {
                    const width = video.videoWidth;
                    const height = video.videoHeight;
                    console.log(`Manual setup: Video ${width}x${height}`);

                    if (width === 0 || height === 0) return;

                    canvas.width = width;
                    canvas.height = height;

                    let frameCount = 0;
                    let lastFrameTime = performance.now();

                    // Low-latency render using requestVideoFrameCallback
                    const renderFrameRVFC = (now, metadata) => {
                        if (video.paused || video.ended) return;
                        if (video.readyState < 2) {
                            video.requestVideoFrameCallback(renderFrameRVFC);
                            return;
                        }
                        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                        frameCount++;
                        if (now - lastFrameTime > 2000) {
                            frameCount = 0;
                            lastFrameTime = now;
                        }
                        video.requestVideoFrameCallback(renderFrameRVFC);
                    };

                    // Fallback render using requestAnimationFrame
                    const renderFrameRAF = () => {
                        if (video.paused || video.ended) return;
                        if (video.readyState >= 2) {
                            ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                            frameCount++;
                            const now = performance.now();
                            if (now - lastFrameTime > 2000) {
                                frameCount = 0;
                                lastFrameTime = now;
                            }
                        }
                        requestAnimationFrame(renderFrameRAF);
                    };

                    video.play().then(() => {
                        console.log('Manual setup: Video playback started');
                        if ('requestVideoFrameCallback' in HTMLVideoElement.prototype) {
                            video.requestVideoFrameCallback(renderFrameRVFC);
                        } else {
                            renderFrameRAF();
                        }
                    }).catch(err => console.error('Manual setup play failed:', err));
                };

                if (video.readyState >= 1) {
                    video.onloadedmetadata();
                }
            };

            setupCanvas();
            return true;
        }

        console.log('No video track found');
        return false;
    },

    // Get video pipeline diagnostic info
    getVideoDiagnostics(sessionId) {
        const session = this._getSession(sessionId);
        const info = {
            peerConnection: session.peerConnection?.connectionState || 'none',
            iceConnection: session.peerConnection?.iceConnectionState || 'none',
            signalingState: session.peerConnection?.signalingState || 'none',
            hasRemoteVideo: !!session.remoteVideo,
            videoReadyState: session.remoteVideo?.readyState || -1,
            videoPaused: session.remoteVideo?.paused,
            videoEnded: session.remoteVideo?.ended,
            videoWidth: session.remoteVideo?.videoWidth || 0,
            videoHeight: session.remoteVideo?.videoHeight || 0,
            hasCanvas: !!session.remoteCanvas,
            canvasWidth: session.remoteCanvas?.width || 0,
            canvasHeight: session.remoteCanvas?.height || 0,
            receivers: []
        };

        if (session.peerConnection) {
            const receivers = session.peerConnection.getReceivers();
            info.receivers = receivers.map(r => ({
                kind: r.track?.kind,
                readyState: r.track?.readyState,
                muted: r.track?.muted,
                enabled: r.track?.enabled
            }));
        }

        console.log(`=== VIDEO DIAGNOSTICS [${sessionId}] ===`);
        console.log(JSON.stringify(info, null, 2));
        return info;
    },

    // Get connection stats
    async getStats(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            return null;
        }

        const stats = await session.peerConnection.getStats();
        const result = {};

        stats.forEach(report => {
            if (report.type === 'outbound-rtp' && report.kind === 'video') {
                result.outbound = {
                    bytesSent: report.bytesSent,
                    packetsSent: report.packetsSent,
                    framesEncoded: report.framesEncoded,
                    framesSent: report.framesSent,
                    framesPerSecond: report.framesPerSecond
                };
            } else if (report.type === 'inbound-rtp' && report.kind === 'video') {
                result.inbound = {
                    bytesReceived: report.bytesReceived,
                    packetsReceived: report.packetsReceived,
                    framesDecoded: report.framesDecoded,
                    framesReceived: report.framesReceived,
                    framesPerSecond: report.framesPerSecond
                };
            }
        });

        return result;
    },

    // Log latency stats for debugging - call from browser console: SteamViewerWebRTC.logLatencyStats('sessionId')
    async logLatencyStats(sessionId) {
        const session = this._getSession(sessionId);
        if (!session.peerConnection) {
            console.log('[LATENCY] No peer connection');
            return;
        }
        const stats = await session.peerConnection.getStats();
        stats.forEach(report => {
            if (report.type === 'inbound-rtp' && report.kind === 'video') {
                console.log(`[LATENCY] jitterBufferDelay: ${report.jitterBufferDelay?.toFixed(3) ?? 'N/A'}s, ` +
                            `jitter: ${report.jitter?.toFixed(3) ?? 'N/A'}s, ` +
                            `framesDecoded: ${report.framesDecoded ?? 'N/A'}, ` +
                            `FPS: ${report.framesPerSecond?.toFixed(1) ?? 'N/A'}`);
            }
            if (report.type === 'candidate-pair' && report.state === 'succeeded') {
                console.log(`[LATENCY] RTT: ${((report.currentRoundTripTime ?? 0) * 1000).toFixed(1)}ms`);
            }
        });
    },

    // Check if video is ready for capture
    _isVideoReady(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return false;
        return session.remoteCanvas &&
               session.remoteCanvas.width > 0 &&
               session.remoteCanvas.height > 0 &&
               session.remoteVideo &&
               session.remoteVideo.readyState >= 2;
    },

    // Enable direct rendering to a visible canvas (bypasses JPEG relay)
    // Only works when PeerConnection is in the same JS context as the canvas
    setDirectRenderTarget(sessionId, canvasId) {
        const session = this.sessions.get(sessionId);
        if (!session) {
            console.warn(`[DirectRender] No session: ${sessionId}`);
            return false;
        }

        const canvas = document.getElementById(canvasId);
        if (!canvas) {
            console.warn(`[DirectRender] Canvas '${canvasId}' not found`);
            return false;
        }

        session._directRenderCanvas = canvas;
        session._directRenderCtx = canvas.getContext('2d');

        // Size canvas bitmap to CSS display size × devicePixelRatio
        // We handle letterboxing ourselves in drawImage — no object-fit:contain
        const updateCanvasSize = () => {
            const rect = canvas.getBoundingClientRect();
            const dpr = window.devicePixelRatio || 1;
            const newW = Math.round(rect.width * dpr);
            const newH = Math.round(rect.height * dpr);
            if (canvas.width !== newW || canvas.height !== newH) {
                canvas.width = newW;
                canvas.height = newH;
            }
            // Recompute letterbox with current video dimensions
            const videoW = session.remoteVideo?.videoWidth || 0;
            const videoH = session.remoteVideo?.videoHeight || 0;
            session._letterbox = computeLetterbox(newW, newH, videoW, videoH);
        };

        updateCanvasSize();

        // Watch for container resizes (window resize, tab switch, etc.)
        if (session._resizeObserver) {
            session._resizeObserver.disconnect();
        }
        session._resizeObserver = new ResizeObserver(updateCanvasSize);
        session._resizeObserver.observe(canvas.parentElement || canvas);

        // Store updater so render loop can call it when video resolution changes
        session._updateCanvasSize = updateCanvasSize;

        console.log(`[DirectRender] Enabled for session ${sessionId} → canvas '${canvasId}' (${canvas.width}x${canvas.height} bitmap)`);
        return true;
    },

    // Disable direct rendering (falls back to JPEG relay)
    clearDirectRenderTarget(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session._firstDirectFrameNotified = false;
        if (session._resizeObserver) {
            session._resizeObserver.disconnect();
            session._resizeObserver = null;
        }
        session._updateCanvasSize = null;
        session._directRenderCanvas = null;
        session._directRenderCtx = null;
        console.log(`[DirectRender] Disabled for session ${sessionId}`);
    },

    // Enable frame capture to relay to viewer window
    async startFrameCapture(sessionId, dotNetRef) {
        const session = this._getSession(sessionId);
        console.log(`[${sessionId}] Starting frame capture for viewer window`);
        session.frameCaptureDotNetRef = dotNetRef;
        session.frameCaptureEnabled = true;
        session.lastFrameTime = 0;

        // Wait for video to be ready (up to 5 seconds)
        let attempts = 0;
        while (!this._isVideoReady(sessionId) && attempts < 50) {
            await new Promise(r => setTimeout(r, 100));
            attempts++;
        }

        if (!this._isVideoReady(sessionId)) {
            console.warn('Video not ready after 5s, starting capture anyway');
        } else {
            console.log('Video ready, starting frame capture');
        }

        // Use requestAnimationFrame for better performance
        const captureLoop = (timestamp) => {
            if (!session.frameCaptureEnabled) return;

            // Throttle to target frame rate
            if (timestamp - session.lastFrameTime >= session.frameInterval) {
                this._captureAndSendFrame(sessionId);
                session.lastFrameTime = timestamp;
            }

            session.frameCaptureAnimationId = requestAnimationFrame(captureLoop);
        };

        session.frameCaptureAnimationId = requestAnimationFrame(captureLoop);
        return true;
    },

    stopFrameCapture(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        console.log(`[${sessionId}] Stopping frame capture`);
        session.frameCaptureEnabled = false;
        if (session.frameCaptureAnimationId) {
            cancelAnimationFrame(session.frameCaptureAnimationId);
            session.frameCaptureAnimationId = null;
        }
        session.frameCaptureDotNetRef = null;
        session.captureCanvas = null;
        session.captureCtx = null;
    },

    // Pause frame capture (keeps dotNetRef for resume)
    pauseFrameCapture(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session || !session.frameCaptureEnabled) return;
        console.log(`[${sessionId}] Pausing frame capture`);
        session.frameCaptureEnabled = false;
        if (session.frameCaptureAnimationId) {
            cancelAnimationFrame(session.frameCaptureAnimationId);
            session.frameCaptureAnimationId = null;
        }
    },

    // Resume frame capture (restarts rAF loop if dotNetRef is still set)
    resumeFrameCapture(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session || !session.frameCaptureDotNetRef || session.frameCaptureEnabled) return;
        console.log(`[${sessionId}] Resuming frame capture`);
        session.frameCaptureEnabled = true;
        session.lastFrameTime = 0;

        const captureLoop = (timestamp) => {
            if (!session.frameCaptureEnabled) return;
            if (timestamp - session.lastFrameTime >= session.frameInterval) {
                this._captureAndSendFrame(sessionId);
                session.lastFrameTime = timestamp;
            }
            session.frameCaptureAnimationId = requestAnimationFrame(captureLoop);
        };
        session.frameCaptureAnimationId = requestAnimationFrame(captureLoop);
    },

    _captureAndSendFrame(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session || !session.frameCaptureEnabled || !session.remoteCanvas || !session.frameCaptureDotNetRef) return;

        try {
            const srcWidth = session.remoteCanvas.width;
            const srcHeight = session.remoteCanvas.height;

            if (srcWidth === 0 || srcHeight === 0) return;

            // Downscale very large resolutions for encoding (max 4K)
            const maxWidth = 3840;
            const maxHeight = 2160;
            let destWidth = srcWidth;
            let destHeight = srcHeight;

            if (srcWidth > maxWidth || srcHeight > maxHeight) {
                const scale = Math.min(maxWidth / srcWidth, maxHeight / srcHeight);
                destWidth = Math.round(srcWidth * scale);
                destHeight = Math.round(srcHeight * scale);
            }

            // Create/reuse downscale canvas
            if (!session.captureCanvas || session.captureCanvas.width !== destWidth || session.captureCanvas.height !== destHeight) {
                session.captureCanvas = document.createElement('canvas');
                session.captureCanvas.width = destWidth;
                session.captureCanvas.height = destHeight;
                session.captureCtx = session.captureCanvas.getContext('2d');
            }

            // Draw downscaled frame
            session.captureCtx.drawImage(session.remoteCanvas, 0, 0, destWidth, destHeight);

            // Convert to JPEG (0.65 quality — faster encode/decode, smaller payload for relay path)
            const dataUrl = session.captureCanvas.toDataURL('image/jpeg', 0.65);

            // Send to C# - use original dimensions for coordinate scaling
            const base64Data = dataUrl.replace(/^data:image\/jpeg;base64,/, '');
            if (session.frameCaptureDotNetRef) session.frameCaptureDotNetRef.invokeMethodAsync('OnFrameCaptured', base64Data, srcWidth, srcHeight);
        } catch (e) {
            // Ignore capture errors
        }
    },

    // === Stats Overlay ===

    toggleStatsOverlay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) {
            console.warn(`[Stats] No session found for: ${sessionId}`);
            return;
        }
        session._statsVisible = !session._statsVisible;
        if (session._statsVisible) {
            this._createStatsOverlay(sessionId);
            this._startStatsPolling(sessionId);
        } else {
            if (!session._statsRelay) {
                this._stopStatsPolling(sessionId);
            }
            this._removeStatsOverlay(sessionId);
        }
    },

    enableStatsRelay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session._statsRelay = true;
        if (!session._statsInterval) {
            this._startStatsPolling(sessionId);
        }
    },

    disableStatsRelay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session._statsRelay = false;
        if (!session._statsVisible) {
            this._stopStatsPolling(sessionId);
        }
    },

    _createStatsOverlay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        if (session._statsOverlayEl) return;
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
        session._statsOverlayEl = el;
    },

    _removeStatsOverlay(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        if (session._statsOverlayEl) {
            session._statsOverlayEl.remove();
            session._statsOverlayEl = null;
        }
    },

    _startStatsPolling(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        session._statsPrev = null;
        session._inputEventsCount = 0;
        session._inputThrottledCount = 0;
        session._statsInterval = setInterval(() => this._pollStats(sessionId), 1000);
    },

    _stopStatsPolling(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;
        if (session._statsInterval) {
            clearInterval(session._statsInterval);
            session._statsInterval = null;
        }
        session._statsPrev = null;
    },

    async _adaptBitrate(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session || !session.peerConnection || !session._bitrateAdaptEnabled) return;

        const now = performance.now();
        if (now - session._lastBitrateAdjust < 3000) return; // Adjust every 3s
        session._lastBitrateAdjust = now;

        try {
            const stats = await session.peerConnection.getStats();
            let availableBandwidth = 0;
            let packetsLost = 0, packetsReceived = 0;

            stats.forEach(report => {
                if (report.type === 'candidate-pair' && report.state === 'succeeded') {
                    if (report.availableOutgoingBitrate) {
                        availableBandwidth = report.availableOutgoingBitrate;
                    }
                }
                if (report.type === 'inbound-rtp' && report.kind === 'video') {
                    packetsLost = report.packetsLost || 0;
                    packetsReceived = report.packetsReceived || 0;
                }
            });

            if (availableBandwidth <= 0) return;

            // Rolling average of last 5 samples
            session._bitrateHistory.push(availableBandwidth);
            if (session._bitrateHistory.length > 5) session._bitrateHistory.shift();
            const avgBandwidth = session._bitrateHistory.reduce((a, b) => a + b, 0) / session._bitrateHistory.length;

            // Target 90% of available bandwidth (ramp up aggressively)
            let newTarget = Math.floor(avgBandwidth * 0.9);

            // Only reduce below initial bitrate on actual packet loss — never on BWE alone.
            // BWE starts conservatively and needs high usage to ramp up. Reducing to match
            // BWE's estimate creates a feedback loop where bitrate gets stuck at ~3Mbps.
            let lossPercent = 0;
            if (packetsReceived + packetsLost > 0) {
                lossPercent = (packetsLost / (packetsReceived + packetsLost)) * 100;
            }

            const initialBitrate = 10_000_000; // Must match startNativeCapture / startScreenShare
            if (lossPercent > 2) {
                // Significant loss — reduce aggressively, allow going below initial
                newTarget = Math.floor(newTarget * 0.7);
            } else if (lossPercent > 0.5) {
                // Mild loss — reduce gently, allow going below initial
                newTarget = Math.floor(newTarget * 0.9);
            } else {
                // No loss — never reduce below initial, only ramp up
                newTarget = Math.max(newTarget, initialBitrate);
            }

            // Clamp
            newTarget = Math.max(session._minBitrate, Math.min(session._maxBitrate, newTarget));

            // Only adjust if >10% change
            const changePct = Math.abs(newTarget - session._targetBitrate) / session._targetBitrate;
            if (changePct < 0.1) return;

            // Apply via setParameters
            const senders = session.peerConnection.getSenders();
            for (const sender of senders) {
                if (sender.track?.kind !== 'video') continue;
                const params = sender.getParameters();
                if (params.encodings && params.encodings.length > 0) {
                    params.encodings[0].maxBitrate = newTarget;
                    await sender.setParameters(params);
                }
            }

            const oldMbps = (session._targetBitrate / 1_000_000).toFixed(1);
            const newMbps = (newTarget / 1_000_000).toFixed(1);
            console.log(`[Bitrate] ${oldMbps} → ${newMbps} Mbps (avail: ${(avgBandwidth / 1_000_000).toFixed(1)}, loss: ${lossPercent.toFixed(1)}%)`);

            session._targetBitrate = newTarget;

            if (newTarget >= 20_000_000) session._qualityMode = 'HQ';
            else if (newTarget >= 8_000_000) session._qualityMode = 'MQ';
            else if (newTarget >= 2_000_000) session._qualityMode = 'LQ';
            else session._qualityMode = 'MIN';
        } catch (e) {
            console.warn('[Bitrate] adaptation error:', e);
        }
    },

    async _pollStats(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session || !session.peerConnection) return;
        if (!session._statsOverlayEl && !session._statsRelay) return;

        const fmtBytes = (bytes) => {
            if (bytes < 1024) return `${bytes} B`;
            if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
            if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(1)} MB`;
            return `${(bytes / 1073741824).toFixed(2)} GB`;
        };

        try {
        const stats = await session.peerConnection.getStats();
        const now = performance.now();

        let videoFps = 0, bitrateMbps = 0, resolution = '?';
        let rttMs = 0, lossPercent = 0;
        let currentBytes = 0, currentFrames = 0;
        let packetsLost = 0, packetsReceived = 0;

        stats.forEach(report => {
            // Outbound video (host side)
            if (report.type === 'outbound-rtp' && report.kind === 'video') {
                currentBytes = report.bytesSent || 0;
                currentFrames = report.framesEncoded || 0;
                videoFps = report.framesPerSecond || 0;
                if (report.frameWidth && report.frameHeight) {
                    resolution = `${report.frameWidth}x${report.frameHeight}`;
                }
            }
            // Inbound video (viewer side)
            if (report.type === 'inbound-rtp' && report.kind === 'video') {
                currentBytes = report.bytesReceived || 0;
                currentFrames = report.framesDecoded || 0;
                videoFps = report.framesPerSecond || 0;
                packetsLost = report.packetsLost || 0;
                packetsReceived = report.packetsReceived || 0;
                if (report.frameWidth && report.frameHeight) {
                    resolution = `${report.frameWidth}x${report.frameHeight}`;
                }
                // Jitter buffer metrics — track cumulative values for delta calculation
                if (report.jitterBufferDelay !== undefined && report.jitterBufferEmittedCount) {
                    session._jbDelay = report.jitterBufferDelay;
                    session._jbCount = report.jitterBufferEmittedCount;
                }
            }
            // RTT from candidate pair
            if (report.type === 'candidate-pair' && report.state === 'succeeded') {
                rttMs = (report.currentRoundTripTime || 0) * 1000;
            }
        });

        // Data channel bytes (control/input traffic)
        let dataChannelBytes = 0;
        stats.forEach(report => {
            if (report.type === 'data-channel') {
                dataChannelBytes += (report.bytesSent || 0) + (report.bytesReceived || 0);
            }
        });

        // Calculate deltas from previous poll
        if (session._statsPrev) {
            const elapsed = (now - session._statsPrev.time) / 1000; // seconds
            if (elapsed > 0) {
                const deltaBytes = currentBytes - session._statsPrev.bytes;
                bitrateMbps = (deltaBytes * 8) / (elapsed * 1_000_000);

                if (!videoFps) {
                    const deltaFrames = currentFrames - session._statsPrev.frames;
                    videoFps = deltaFrames / elapsed;
                }
            }
        }

        // Loss percentage
        if (packetsReceived + packetsLost > 0) {
            lossPercent = (packetsLost / (packetsReceived + packetsLost)) * 100;
        }

        // Data channel buffer
        const bufferKB = session.dataChannel ? (session.dataChannel.bufferedAmount / 1024) : 0;

        // Input events/sec (captured since last poll)
        const inputPerSec = session._inputEventsCount;
        const throttledPerSec = session._inputThrottledCount;
        session._inputEventsCount = 0;
        session._inputThrottledCount = 0;

        // Jitter buffer delta — average JB delay since last poll
        // Source: .claude/research/webrtc-latency/research.md (Selkies pattern)
        let jbAvgMs = session._jbAvgMs || 0;
        if (session._jbDelay !== undefined && session._statsPrev?.jbDelay !== undefined) {
            const deltaDelay = session._jbDelay - session._statsPrev.jbDelay;
            const deltaCount = session._jbCount - (session._statsPrev.jbCount || 0);
            if (deltaCount > 0) {
                jbAvgMs = (deltaDelay / deltaCount) * 1000; // seconds → ms
                session._jbAvgMs = jbAvgMs;
            }
        }

        // JB pressure moved to dedicated 15ms interval (_jbPressureInterval)
        // — 1000ms was too slow, Chrome's adaptive algorithm regrows buffer between polls

        // Save for next delta (include jitter buffer cumulative values)
        session._statsPrev = {
            time: now, bytes: currentBytes, frames: currentFrames,
            jbDelay: session._jbDelay, jbCount: session._jbCount
        };

        // If resolution still unknown, try from canvas/video
        if (resolution === '?') {
            if (session.remoteCanvas && session.remoteCanvas.width > 0) {
                resolution = `${session.remoteCanvas.width}x${session.remoteCanvas.height}`;
            } else if (session.remoteVideo && session.remoteVideo.videoWidth > 0) {
                resolution = `${session.remoteVideo.videoWidth}x${session.remoteVideo.videoHeight}`;
            }
        }

        // Format overlay text
        const lines = [
            `Video: ${videoFps.toFixed(0)} FPS | ${bitrateMbps.toFixed(1)} Mbps | ${resolution}`,
            `Lat:   JB ${jbAvgMs.toFixed(0)}ms | RTT ${rttMs.toFixed(0)}ms | Loss ${lossPercent.toFixed(1)}%`,
            `Input: ${inputPerSec} evt/s${throttledPerSec > 0 ? ` (${throttledPerSec} throttled)` : ''} | Buf: ${bufferKB.toFixed(0)} KB`,
            `Data:  ${fmtBytes(currentBytes)} video | ${fmtBytes(dataChannelBytes)} ctrl`,
            `Mode:  [${session._qualityMode}] Target: ${(session._targetBitrate / 1_000_000).toFixed(1)} Mbps`
        ];

        // Update DOM overlay if visible
        if (session._statsOverlayEl) {
            session._statsOverlayEl.textContent = lines.join('\n');
            session._statsOverlayEl.style.whiteSpace = 'pre';
        }

        // Relay stats to C# if enabled (for cross-window overlay)
        if (session._statsRelay && session.dotNetRef) {
            try {
                session.dotNetRef.invokeMethodAsync('OnStatsUpdate', JSON.stringify({
                    videoFps, bitrateMbps, resolution,
                    rttMs, lossPercent, jbAvgMs,
                    inputPerSec, throttledPerSec, bufferKB,
                    currentBytes, dataChannelBytes,
                    qualityMode: session._qualityMode
                }));
            } catch (e) { /* ignore relay errors */ }
        }

        // Dynamic bitrate adaptation
        this._adaptBitrate(sessionId);
        } catch (e) {
            console.error('[Stats] _pollStats error:', e);
        }
    },

    // Close connection
    close(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;

        // Stop JB pressure interval
        if (session._jbPressureInterval) {
            clearInterval(session._jbPressureInterval);
            session._jbPressureInterval = null;
        }

        this._stopStatsPolling(sessionId);
        this._removeStatsOverlay(sessionId);
        this.stopFrameCapture(sessionId);

        if (session.localStream) {
            session.localStream.getTracks().forEach(track => track.stop());
            session.localStream = null;
        }

        if (session.remoteVideo) {
            session.remoteVideo.pause();
            session.remoteVideo.srcObject = null;
            session.remoteVideo = null;
        }

        session.remoteCanvas = null;
        session.remoteCtx = null;

        if (session.mouseChannel) {
            session.mouseChannel.close();
            session.mouseChannel = null;
        }

        if (session.dataChannel) {
            session.dataChannel.close();
            session.dataChannel = null;
        }

        if (session.peerConnection) {
            session.peerConnection.close();
            session.peerConnection = null;
        }

        // Reset input lock state on disconnect (fix: input lock persists after disconnect)
        if (window.SteamViewerInput && window.SteamViewerInput.isLocked) {
            window.SteamViewerInput.unlock();
            console.log('Input lock released due to connection close');
        }

        if (session._visibilityHandler) {
            document.removeEventListener('visibilitychange', session._visibilityHandler);
            session._visibilityHandler = null;
        }

        // Close silent audio context
        if (session._silentAudioCtx) {
            session._silentAudioCtx.close().catch(() => {});
            session._silentAudioCtx = null;
        }

        session.dotNetRef = null;
        this.sessions.delete(sessionId);
        console.log(`[${sessionId}] WebRTC connection closed`);
    },

    // Reset capture state for reconnection (fix: mouse coords stale after reconnect)
    resetForReconnect(sessionId) {
        const session = this.sessions.get(sessionId);
        if (!session) return;

        this.stopFrameCapture(sessionId);

        if (session.localStream) {
            session.localStream.getTracks().forEach(track => track.stop());
            session.localStream = null;
        }

        if (session.remoteVideo) {
            session.remoteVideo.pause();
            session.remoteVideo.srcObject = null;
        }

        session.remoteCanvas = null;
        session.remoteCtx = null;

        // Reset input state
        if (window.SteamViewerInput) {
            window.SteamViewerInput.unlock();
        }

        console.log(`[${sessionId}] WebRTC state reset for reconnection`);
    },

    // Helper to increment input event count for a session (called from SteamViewerInput)
    _incrementInputCount(sessionId) {
        const session = this.sessions.get(sessionId);
        if (session) {
            session._inputEventsCount++;
        }
    }
};

// Input capture for remote canvas
window.SteamViewerInput = {
    canvas: null,
    dotNetRef: null,
    isCapturing: false,
    isLocked: false,  // Capture lock - only send inputs when locked
    // Track which session the input is associated with (for stats counting)
    _activeSessionId: null,
    _inputEventCount: 0,
    _lastMouseDownCoords: null,  // Cache coords from mouse_down for identical mouse_up (prevents micro-drag)
    _focusWatchdogId: null,
    // PID mouse regulation — sends every event at low velocity (precision/hover),
    // suppresses during sweeps, sends immediately on deceleration (arrival detection)
    _pidAlpha: 0.3,           // EMA smoothing factor for velocity (0-1, lower = smoother)
    _pidKp: 1.0,              // Proportional gain (velocity weight)
    _pidKi: 0.5,              // Integral gain (accumulated movement weight)
    _pidKd: 2.0,              // Derivative gain (acceleration/deceleration weight)
    _pidSendThreshold: 0.8,   // Score above this = suppress (buffer for interval)
    _pidIDecay: 0.95,         // Integral decay per event (anti-windup via EMA)
    _pidIMax: 5.0,            // Integral clamp (anti-windup hard limit)
    _pidIdleThresholdMs: 100, // ms idle before triggering cold start burst
    _pidColdStartBurst: 5,    // Number of events to force-send after idle
    _pidColdStartRemaining: 0, // Counter for remaining burst events
    // Dynamic cooldown — timer send rate scales with velocity
    _pidMinCooldown: 16,      // ms — fastest timer rate during sweeps (~60 FPS)
    _pidMaxCooldown: 100,     // ms — slowest timer rate for fast sweeps (~10 FPS)
    _pidVelocityCap: 2.0,     // px/ms — velocity at which cooldown maxes out
    // PID internal state
    _pidVelocity: 0,          // EMA-smoothed velocity (px/ms)
    _pidIntegral: 0,          // Accumulated velocity integral (with decay)
    _pidLastVelocity: 0,      // Previous smoothed velocity (for D term)
    _pidLastEventTime: 0,     // Timestamp of previous mousemove
    // Regulation plumbing
    _lastTimerSendTime: 0,    // Timestamp of last timer-initiated send
    _lastSentX: 0,
    _lastSentY: 0,
    _bufferedMouseCoords: null,     // Latest coords waiting for interval send
    _regulationTimer: null,         // setInterval for sweep mode sends


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
        // Mouse events
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

        // Focus canvas immediately so keyboard events are captured from the start
        this.canvas.focus();
        // Re-focus after a delay — MAUI WebView or Blazor re-render can steal focus
        setTimeout(() => { this.canvas?.focus(); }, 200);
        setTimeout(() => { this.canvas?.focus(); }, 500);

        // Start periodic focus watchdog (restores focus if lost while locked)
        this._startFocusWatchdog();

        console.log('Input capture initialized (use toolbar button to lock/unlock)');
        return true;
    },

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

    // Set which session input events count toward (for stats overlay)
    setActiveSession(sessionId) {
        this._activeSessionId = sessionId;
    },

    // Called from C# after Blazor re-renders (e.g., elevation state change) to fix canvas reference
    _reattachCount: 0,
    reattachIfNeeded() {
        this._reattachCount++;
        const current = document.getElementById('viewerCanvas');
        const same = current === this.canvas;
        if (this._reattachCount <= 5 || !same) {
            console.log(`[Input] reattachIfNeeded #${this._reattachCount}: canvasSame=${same}, capturing=${this.isCapturing}, locked=${this.isLocked}, dotNetRef=${!!this.dotNetRef}`);
        }
        this.ensureCanvas();
        if (this.canvas) {
            this.canvas.focus();
        }
        // Restart watchdog in case interval was lost during re-render
        if (!this._focusWatchdogId && this.isCapturing) {
            this._startFocusWatchdog();
        }
    },

    // Verify canvas DOM reference is still valid; re-attach listeners if Blazor recreated it
    ensureCanvas() {
        const current = document.getElementById('viewerCanvas');
        if (current && current !== this.canvas) {
            console.warn('[Input] Canvas DOM node changed — re-attaching listeners');
            // Remove listeners from old canvas (if still in DOM)
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
        }
    },

    createLockIndicator() {
        // Add a visual indicator for lock state
        this.lockIndicator = document.createElement('div');
        this.lockIndicator.id = 'inputLockIndicator';
        this.lockIndicator.style.cssText = `
            position: fixed;
            top: 10px;
            right: 10px;
            padding: 8px 16px;
            border-radius: 4px;
            font-size: 12px;
            font-family: sans-serif;
            z-index: 9999;
            pointer-events: none;
            transition: all 0.2s;
        `;
        this.updateLockIndicator();
        document.body.appendChild(this.lockIndicator);
    },

    updateLockIndicator() {
        if (!this.lockIndicator || !this.showLockIndicator) return;
        if (this.isLocked) {
            this.lockIndicator.textContent = '🔒 Input Locked';
            this.lockIndicator.style.background = '#a6e3a1';
            this.lockIndicator.style.color = '#1e1e2e';
        } else {
            this.lockIndicator.textContent = '🔓 Input Unlocked';
            this.lockIndicator.style.background = '#45475a';
            this.lockIndicator.style.color = '#cdd6f4';
        }
    },

    // Remote cursor shape from host (set via cursorShape data channel message)
    _remoteCursorShape: 'default',

    lock() {
        this.ensureCanvas();
        this.isLocked = true;
        this.updateLockIndicator();
        this.notifyLockChange();
        this.canvas.focus();
        // Apply remote cursor shape — shows the host's current cursor type locally
        this.canvas.style.cursor = this._remoteCursorShape || 'default';
        // Gesture-backed restart: user click to lock = user gesture for getDisplayMedia
        this._restartSharingIfLost();
        // Start mouse regulation interval timer (sweep mode sends)
        this._startRegulationTimer();
        console.log('Input LOCKED - sending inputs to host');
    },

    // Check all sessions for lost sharing and attempt restart (called from lock() which has user gesture)
    _restartSharingIfLost() {
        if (!window.SteamViewerWebRTC) return;
        for (const [sessionId, session] of window.SteamViewerWebRTC.sessions) {
            if (session._sharingLost && !session._sharingStoppedByUser && !session._restartingShare) {
                console.log(`[${sessionId}] Sharing lost — restarting with user gesture (fullscreen)`);
                session._sharingLost = false;
                session._restartingShare = true;
                window.SteamViewerWebRTC.startScreenCapture(sessionId).then(ok => {
                    session._restartingShare = false;
                    if (!ok) session._sharingLost = true;
                }).catch(() => { session._restartingShare = false; session._sharingLost = true; });
                break; // One at a time
            }
        }
    },

    unlock() {
        this.isLocked = false;
        this.updateLockIndicator();
        this.notifyLockChange();
        this._stopRegulationTimer();
        // Restore default cursor (no longer mirroring host cursor shape)
        if (this.canvas) this.canvas.style.cursor = '';
        console.log('Input UNLOCKED');
    },

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

            if (elapsed < cooldown) return; // not enough time since last send

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

    notifyLockChange() {
        // Notify C# of lock state change (for toolbar sync)
        if (this.dotNetRef) {
            try {
                this.dotNetRef.invokeMethodAsync('OnInputLockChanged', this.isLocked);
            } catch (e) {
                // Ignore if C# callback not available
            }
        }
    },

    _coordLogCount: 0,
    _frameLetterbox: null, // Set by SteamViewerViewer.renderJpegFrame (JPEG relay path)
    getScaledCoords(e) {
        // Map CSS mouse position → video pixel coordinates.
        // Source 1: session._letterbox (direct rendering — same JS context)
        // Source 2: _frameLetterbox (JPEG relay — cross-window, set by SteamViewerViewer)
        const session = window.SteamViewerWebRTC.sessions.get(this._activeSessionId);
        const lb = (session?._letterbox?.videoW > 0 ? session._letterbox : null)
                || (this._frameLetterbox?.videoW > 0 ? this._frameLetterbox : null);

        if (!lb) {
            if (this._coordLogCount < 5) {
                this._coordLogCount++;
                console.warn(`[Input] getScaledCoords: no letterbox source, using canvas fallback`);
            }
            return this.getScaledCoordsForCanvas(e, this.canvas);
        }

        const rect = this.canvas.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        // Convert CSS mouse position to canvas bitmap coords (matching _letterbox space)
        const bitmapX = (e.clientX - rect.left) * dpr;
        const bitmapY = (e.clientY - rect.top) * dpr;
        // Subtract letterbox offset, scale to video pixel coords
        const relX = bitmapX - lb.dx;
        const relY = bitmapY - lb.dy;

        if (this._coordLogCount < 5) {
            this._coordLogCount++;
            console.log(`[Input] getScaledCoords: lb=${JSON.stringify(lb)}, dpr=${dpr}, bitmap=(${bitmapX.toFixed(0)},${bitmapY.toFixed(0)}), rel=(${relX.toFixed(0)},${relY.toFixed(0)})`);
        }

        return {
            x: Math.max(0, Math.min(lb.videoW, relX * lb.videoW / lb.dw)),
            y: Math.max(0, Math.min(lb.videoH, relY * lb.videoH / lb.dh))
        };
    },

    // Like getScaledCoords but for any canvas element (used for SD overlay)
    // SD overlay still uses object-fit:contain style, so compute letterbox from CSS
    getScaledCoordsForCanvas(e, canvas) {
        const rect = canvas.getBoundingClientRect();
        const canvasAspect = canvas.width / canvas.height;
        const rectAspect = rect.width / rect.height;

        let renderWidth, renderHeight, offsetX, offsetY;

        if (rectAspect > canvasAspect) {
            renderHeight = rect.height;
            renderWidth = rect.height * canvasAspect;
            offsetX = (rect.width - renderWidth) / 2;
            offsetY = 0;
        } else {
            renderWidth = rect.width;
            renderHeight = rect.width / canvasAspect;
            offsetX = 0;
            offsetY = (rect.height - renderHeight) / 2;
        }

        const relX = e.clientX - rect.left - offsetX;
        const relY = e.clientY - rect.top - offsetY;
        const scaleX = canvas.width / renderWidth;
        const scaleY = canvas.height / renderHeight;

        return {
            x: Math.max(0, Math.min(canvas.width, relX * scaleX)),
            y: Math.max(0, Math.min(canvas.height, relY * scaleY))
        };
    },

    getModifiers(e) {
        return {
            ctrl: e.ctrlKey,
            shift: e.shiftKey,
            alt: e.altKey,
            meta: e.metaKey
        };
    },

    _rawEventCount: 0,
    async handleMouseMove(e) {
        this._rawEventCount++;
        if (this._rawEventCount <= 5 || this._rawEventCount % 500 === 0) {
            console.log(`[Input] raw #${this._rawEventCount}: capturing=${this.isCapturing}, locked=${this.isLocked}, dotNetRef=${!!this.dotNetRef}`);
        }
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;

        this._inputEventCount++;
        if (this._inputEventCount <= 3) {
            console.log(`[Input] event #${this._inputEventCount}: capturing=${this.isCapturing}, locked=${this.isLocked}, dotNetRef=${!!this.dotNetRef}, session=${this._activeSessionId}`);
        }
        if (this._activeSessionId) {
            window.SteamViewerWebRTC._incrementInputCount(this._activeSessionId);
        }
        this.ensureCanvas();
        // Clear mouse-down cache — mouse moved, so this is a drag, not a click
        this._lastMouseDownCoords = null;

        const sd = window.SteamViewerSecureDesktop;
        const sdActive = sd?.isActive && sd._width && sd._height && sd.canvas;
        let x, y, captureW, captureH;

        if (sdActive) {
            const sdCoords = this.getScaledCoordsForCanvas(e, sd.canvas);
            x = sdCoords.x;
            y = sdCoords.y;
            captureW = sd._width;
            captureH = sd._height;
            sd._cursorX = sdCoords.x;
            sd._cursorY = sdCoords.y;
            sd._drawCursor();
        } else {
            const coords = this.getScaledCoords(e);
            x = coords.x;
            y = coords.y;
            // Use letterbox video dims for captureW/captureH (matches getScaledCoords space)
            const session = window.SteamViewerWebRTC.sessions.get(this._activeSessionId);
            const lb = (session?._letterbox?.videoW > 0 ? session._letterbox : null)
                    || (this._frameLetterbox?.videoW > 0 ? this._frameLetterbox : null);
            captureW = lb ? lb.videoW : this.canvas.width;
            captureH = lb ? lb.videoH : this.canvas.height;
        }

        // PID mouse regulation:
        // Low score (slow/decelerating/arriving) → send immediately
        // High score (fast/sustained/accelerating) → buffer, interval timer flushes
        const now = performance.now();
        let dt = now - this._pidLastEventTime;
        if (dt <= 0) dt = 1; // guard against same-timestamp events

        // Cold start burst — detect idle→movement transition, force-send first N events
        if (dt > this._pidIdleThresholdMs) {
            this._pidColdStartRemaining = this._pidColdStartBurst;
        }

        const rawVelocity = Math.hypot(e.movementX, e.movementY) / dt; // px/ms
        const velocity = this._pidAlpha * rawVelocity + (1 - this._pidAlpha) * this._pidVelocity;

        // PID terms
        const P = this._pidKp * velocity;
        this._pidIntegral = Math.min(this._pidIntegral * this._pidIDecay + velocity * dt, this._pidIMax);
        const I = this._pidKi * this._pidIntegral;
        const D = this._pidKd * (velocity - this._pidLastVelocity) / dt;

        const score = P + I + D;

        // Update state before send (so first event after lock works)
        this._pidLastVelocity = velocity;
        this._pidVelocity = velocity;
        this._pidLastEventTime = now;

        // Cold start burst bypasses PID — always send immediately after idle
        const coldStart = this._pidColdStartRemaining > 0;
        if (coldStart) this._pidColdStartRemaining--;

        if (coldStart || score < this._pidSendThreshold) {
            // Precision / arrival — send immediately
            this._lastSentX = x;
            this._lastSentY = y;
            this._bufferedMouseCoords = null;
            try {
                await this.dotNetRef.invokeMethodAsync('OnMouseMove', x, y, captureW, captureH);
            } catch (err) { console.error('[Input] OnMouseMove failed:', err); }
        } else {
            // Sweep or decelerating (not yet arrived) — buffer, interval timer will flush
            this._bufferedMouseCoords = { x, y, captureW, captureH };
        }
    },

    _getMouseCoords(e) {
        const sd = window.SteamViewerSecureDesktop;
        const sdActive = sd?.isActive && sd._width && sd._height && sd.canvas;
        if (sdActive) {
            const sdCoords = this.getScaledCoordsForCanvas(e, sd.canvas);
            return { x: sdCoords.x, y: sdCoords.y, captureW: sd._width, captureH: sd._height };
        }
        const coords = this.getScaledCoords(e);
        const session = window.SteamViewerWebRTC.sessions.get(this._activeSessionId);
        const lb = (session?._letterbox?.videoW > 0 ? session._letterbox : null)
                || (this._frameLetterbox?.videoW > 0 ? this._frameLetterbox : null);
        const captureW = lb ? lb.videoW : this.canvas.width;
        const captureH = lb ? lb.videoH : this.canvas.height;
        return { x: coords.x, y: coords.y, captureW, captureH };
    },

    async handleMouseDown(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        if (this._activeSessionId) {
            window.SteamViewerWebRTC._incrementInputCount(this._activeSessionId);
        }
        e.preventDefault();
        const { x, y, captureW, captureH } = this._getMouseCoords(e);
        const button = ['left', 'middle', 'right', 'XButton1', 'XButton2'][e.button] || 'left';
        this._lastMouseDownCoords = { x, y, captureW, captureH, button };
        try {
            await this.dotNetRef.invokeMethodAsync('OnMouseDown', button, x, y, captureW, captureH);
        } catch (err) { console.error('[Input] OnMouseDown failed:', err); }
    },

    async handleMouseUp(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        if (this._activeSessionId) {
            window.SteamViewerWebRTC._incrementInputCount(this._activeSessionId);
        }
        e.preventDefault();
        const button = ['left', 'middle', 'right', 'XButton1', 'XButton2'][e.button] || 'left';
        let x, y, captureW, captureH;
        const cached = this._lastMouseDownCoords;
        if (cached && cached.button === button) {
            // Reuse mouse_down coords for identical click (prevents micro-drag)
            x = cached.x; y = cached.y;
            captureW = cached.captureW; captureH = cached.captureH;
        } else {
            ({ x, y, captureW, captureH } = this._getMouseCoords(e));
        }
        this._lastMouseDownCoords = null;
        try {
            await this.dotNetRef.invokeMethodAsync('OnMouseUp', button, x, y, captureW, captureH);
        } catch (err) { console.error('[Input] OnMouseUp failed:', err); }
    },

    async handleWheel(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        if (this._activeSessionId) {
            window.SteamViewerWebRTC._incrementInputCount(this._activeSessionId);
        }
        e.preventDefault();
        // Normalize delta to pixels (mode 0=pixels, 1=lines, 2=pages)
        let dx = e.deltaX, dy = e.deltaY;
        if (e.deltaMode === 1) { dx *= 40; dy *= 40; }
        else if (e.deltaMode === 2) { dx *= 800; dy *= 800; }
        try {
            await this.dotNetRef.invokeMethodAsync('OnMouseWheel', dx, dy);
        } catch (e) { /* disposed */ }
    },

    async handleKeyDown(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();

        // Clipboard interception — Ctrl+V: route through C# which has clipboard permission
        // (WebView2 blocks navigator.clipboard.readText() in JS keydown context)
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'v' || e.key === 'V')) {
            try {
                console.log('[Input] Ctrl+V detected — routing clipboard paste through C#');
                await this.dotNetRef.invokeMethodAsync('OnClipboardPaste');
            } catch (err) {
                console.warn('[Input] OnClipboardPaste failed, forwarding Ctrl+V keystroke:', err);
                try {
                    await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
                } catch (e2) { console.error('[Input] OnKeyDown failed:', e2); }
            }
            return;
        }

        // Clipboard interception — Ctrl+C/X: forward keystroke, then request host clipboard
        if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'c' || e.key === 'x')) {
            try {
                // Send the keystroke to host first (host app copies/cuts)
                await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
                // After a delay, request the host's clipboard
                if (this._activeSessionId) {
                    const sid = this._activeSessionId;
                    setTimeout(() => {
                        const msg = JSON.stringify({ type: 'clipboard_request' });
                        window.SteamViewerWebRTC.sendData(sid, msg);
                        console.log(`[Input] Clipboard request sent after Ctrl+${e.key.toUpperCase()}`);
                    }, 150);
                }
            } catch (err) { console.error('[Input] OnKeyDown failed:', err); }
            return;
        }

        try {
            await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
        } catch (err) { console.error('[Input] OnKeyDown failed:', err); }
    },

    async handleKeyUp(e) {
        if (!this.isCapturing || !this.isLocked || !this.dotNetRef) return;
        e.preventDefault();
        try {
            await this.dotNetRef.invokeMethodAsync('OnKeyUp', e.key, this.getModifiers(e));
        } catch (err) { console.error('[Input] OnKeyUp failed:', err); }
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
        console.log('Input capture stopped');
    }
};

// JPEG frame rendering for remote viewer window
window.SteamViewerViewer = {
    canvas: null,
    ctx: null,
    img: null,
    _lastFrameW: 0,
    _lastFrameH: 0,

    // Render a JPEG frame to the viewer canvas with proper letterboxing
    renderJpegFrame(canvasId, base64Data, width, height) {
        // Get or create canvas context
        if (!this.canvas || this.canvas.id !== canvasId) {
            this.canvas = document.getElementById(canvasId);
            if (!this.canvas) {
                console.error(`Canvas '${canvasId}' not found`);
                return;
            }
            this.ctx = this.canvas.getContext('2d');
        }

        // Size canvas bitmap to CSS display size × DPR (not video resolution)
        const rect = this.canvas.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        const canvasW = Math.round(rect.width * dpr);
        const canvasH = Math.round(rect.height * dpr);
        if (this.canvas.width !== canvasW || this.canvas.height !== canvasH) {
            this.canvas.width = canvasW;
            this.canvas.height = canvasH;
        }

        // Compute letterbox and publish to SteamViewerInput for coordinate mapping
        const lb = computeLetterbox(canvasW, canvasH, width, height);
        this._lastFrameW = width;
        this._lastFrameH = height;

        // Publish letterbox to input system (cross-namespace, same JS context)
        if (window.SteamViewerInput) {
            window.SteamViewerInput._frameLetterbox = lb;
        }

        // Create image and draw to canvas with letterboxing
        if (!this.img) {
            this.img = new Image();
            this.img.onload = () => {
                if (this.ctx && this.canvas) {
                    // Clear black bars
                    this.ctx.fillStyle = '#000';
                    this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);
                    // Draw frame in letterbox area
                    const lb = window.SteamViewerInput?._frameLetterbox;
                    if (lb) {
                        this.ctx.drawImage(this.img, lb.dx, lb.dy, lb.dw, lb.dh);
                    } else {
                        this.ctx.drawImage(this.img, 0, 0, this.canvas.width, this.canvas.height);
                    }
                }
            };
        }

        this.img.src = 'data:image/jpeg;base64,' + base64Data;
    }
};

// Video frame processing for H.264 encoded data (alternative to browser's built-in codec)
window.SteamViewerVideoDecoder = {
    canvas: null,
    ctx: null,
    decoder: null,

    initialize(canvasId) {
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) {
            console.error(`Canvas '${canvasId}' not found`);
            return false;
        }
        this.ctx = this.canvas.getContext('2d');

        // Check if VideoDecoder API is available (WebCodecs)
        if (typeof VideoDecoder !== 'undefined') {
            this.initWebCodecsDecoder();
            return true;
        }

        console.warn('WebCodecs API not available, using fallback');
        return true;
    },

    initWebCodecsDecoder() {
        this.decoder = new VideoDecoder({
            output: (frame) => {
                // Draw decoded frame to canvas
                this.ctx.drawImage(frame, 0, 0, this.canvas.width, this.canvas.height);
                frame.close();
            },
            error: (e) => {
                console.error('VideoDecoder error:', e);
            }
        });

        this.decoder.configure({
            codec: 'avc1.42E01E' // H.264 Baseline Profile — dimensions come from stream SPS
        });

        console.log('WebCodecs VideoDecoder initialized');
    },

    // Decode H.264 encoded chunk
    decodeFrame(data, timestamp, isKeyFrame) {
        if (!this.decoder) {
            console.warn('Decoder not initialized');
            return;
        }

        const chunk = new EncodedVideoChunk({
            type: isKeyFrame ? 'key' : 'delta',
            timestamp: timestamp,
            data: new Uint8Array(data)
        });

        this.decoder.decode(chunk);
    },

    // Render raw RGBA frame directly
    renderRGBAFrame(data, width, height) {
        if (this.canvas.width !== width || this.canvas.height !== height) {
            this.canvas.width = width;
            this.canvas.height = height;
        }

        const imageData = new ImageData(new Uint8ClampedArray(data), width, height);
        this.ctx.putImageData(imageData, 0, 0);
    },

    close() {
        if (this.decoder) {
            this.decoder.close();
            this.decoder = null;
        }
    }
};

// Secure Desktop overlay for UAC prompt capture (Phase 2)
window.SteamViewerSecureDesktop = {
    canvas: null,
    ctx: null,
    img: null,
    _frameCount: 0,
    isActive: false,
    _width: 0,
    _height: 0,
    _cursorX: -1,
    _cursorY: -1,
    _lastFrameData: null,

    show(canvasId) {
        this._frameCount = 0;
        this.isActive = true;
        this.canvas = document.getElementById(canvasId);
        if (!this.canvas) {
            console.error(`Secure Desktop canvas '${canvasId}' not found`);
            return;
        }
        this.ctx = this.canvas.getContext('2d');
        this.canvas.style.display = 'block';
        console.log('Secure Desktop overlay shown');
    },

    hide(canvasId) {
        const canvas = document.getElementById(canvasId);
        if (canvas) {
            canvas.style.display = 'none';
        }
        this.isActive = false;
        this._width = 0;
        this._height = 0;
        this.canvas = null;
        this.ctx = null;
        this.img = null;
        console.log('Secure Desktop overlay hidden');
    },

    renderFrame(canvasId, base64Jpeg, width, height) {
        this._frameCount++;
        this._width = width;
        this._height = height;
        if (this._frameCount <= 3 || this._frameCount % 100 === 0) {
            console.log(`[SecureDesktop] renderFrame #${this._frameCount}: ${width}x${height}, dataLen=${base64Jpeg?.length}`);
        }
        // Lazy-init canvas if show() wasn't called yet or canvas changed
        if (!this.canvas || this.canvas.id !== canvasId) {
            this.canvas = document.getElementById(canvasId);
            if (!this.canvas) return;
            this.ctx = this.canvas.getContext('2d');
        }

        // Update canvas size to match capture resolution
        if (this.canvas.width !== width || this.canvas.height !== height) {
            this.canvas.width = width;
            this.canvas.height = height;
        }

        // Decode and draw JPEG frame
        if (!this.img) {
            this.img = new Image();
            this.img.onload = () => {
                if (this.ctx && this.canvas) {
                    this.ctx.drawImage(this.img, 0, 0, this.canvas.width, this.canvas.height);
                    this._drawCursor();
                }
            };
        }

        this.img.src = 'data:image/jpeg;base64,' + base64Jpeg;
    },

    _drawCursor() {
        if (!this.ctx || !this.canvas || this._cursorX < 0 || this._cursorY < 0) return;
        const x = this._cursorX;
        const y = this._cursorY;
        const ctx = this.ctx;
        const size = 12;

        // Crosshair
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(x - size, y); ctx.lineTo(x + size, y);
        ctx.moveTo(x, y - size); ctx.lineTo(x, y + size);
        ctx.stroke();

        // Dark outline for visibility
        ctx.strokeStyle = '#000';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x - size, y); ctx.lineTo(x + size, y);
        ctx.moveTo(x, y - size); ctx.lineTo(x, y + size);
        ctx.stroke();

        // Center dot
        ctx.fillStyle = '#ff3333';
        ctx.beginPath();
        ctx.arc(x, y, 3, 0, Math.PI * 2);
        ctx.fill();
    }
};

// --- WebView2 SharedBuffer receiver (zero-copy frame transfer from C# DXGI capture) ---
// Supports two modes:
//   raw BGRA: VideoFrame directly from pixel data (no JPEG encode/decode — fastest)
//   JPEG fallback: createImageBitmap → VideoFrame (when raw not available)
// Source: .claude/research/binary-frame-transfer/research.md
if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('sharedbufferreceived', (e) => {
        try {
            const meta = e.additionalData; // Already parsed by WebView2 — NOT a string
            const buf = e.getBuffer();

            const session = window.SteamViewerWebRTC.sessions.get(meta.sid);
            if (!session?._nativeCaptureActive) {
                chrome.webview.releaseBuffer(buf);
                return;
            }

            if (meta.raw) {
                // Raw BGRA path — VideoFrame directly from pixel data
                // No JPEG encode, no JPEG decode, no createImageBitmap. Synchronous.
                // Must copy bytes out of SharedBuffer before releasing (SharedBuffer
                // ArrayBuffer may not be accepted by VideoFrame constructor directly)
                const bgraCopy = new Uint8Array(buf, 0, meta.len).slice();
                chrome.webview.releaseBuffer(buf);
                const frame = new VideoFrame(bgraCopy, {
                    format: 'BGRA',
                    codedWidth: meta.w,
                    codedHeight: meta.h,
                    timestamp: performance.now() * 1000,
                });

                if (session._useTrackGenerator && session._trackWriter) {
                    session._trackWriter.write(frame).catch(() => {});
                } else {
                    frame.close();
                }
            } else {
                // JPEG fallback path (async — needs createImageBitmap)
                const jpegBytes = new Uint8Array(buf, 0, meta.len).slice();
                chrome.webview.releaseBuffer(buf);

                const blob = new Blob([jpegBytes], { type: 'image/jpeg' });
                createImageBitmap(blob).then(bitmap => {
                    if (!session._nativeCaptureActive) { bitmap.close(); return; }

                    if (session._useTrackGenerator && session._trackWriter) {
                        const frame = new VideoFrame(bitmap, { timestamp: performance.now() * 1000 });
                        bitmap.close();
                        session._trackWriter.write(frame).catch(() => {});
                    } else if (session._nativeCanvas) {
                        const canvas = session._nativeCanvas;
                        if (canvas.width !== meta.w || canvas.height !== meta.h) {
                            canvas.width = meta.w;
                            canvas.height = meta.h;
                        }
                        session._nativeCtx.drawImage(bitmap, 0, 0);
                        bitmap.close();
                        const track = session._nativeStream?.getVideoTracks()[0];
                        if (track?.requestFrame) track.requestFrame();
                    }
                }).catch(() => {});
            }

            session._nativeFrameCount = (session._nativeFrameCount || 0) + 1;

            // Report dimensions on first frame
            if (session._nativeFrameCount === 1 && session.dotNetRef) {
                try {
                    session.dotNetRef.invokeMethodAsync('OnCaptureStartedCallback', meta.w, meta.h);
                    console.log(`[${meta.sid}] SharedBuffer: reported dims ${meta.w}x${meta.h} to C#`);
                } catch (err) {
                    console.warn('Could not report SharedBuffer dims:', err);
                }
            }
        } catch (err) {
            console.warn('SharedBuffer frame error:', err);
        }
    });
    console.log('[SharedBuffer] WebView2 SharedBuffer receiver registered');
} else {
    console.log('[SharedBuffer] Not in WebView2 — using JSInterop fallback');
}
