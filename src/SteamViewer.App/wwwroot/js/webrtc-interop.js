// WebRTC Interop for SteamViewer
// Provides browser WebRTC API access to Blazor via JS interop

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

        // Relay to peer (bidirectional - both host and viewer send)
        if (this.relayEnabled && window.SteamViewerWebRTC?.dataChannel?.readyState === 'open') {
            try {
                const logMsg = JSON.stringify({
                    _logRelay: true,
                    level,
                    message,
                    from: this.peerName,
                    timestamp: Date.now()
                });
                window.SteamViewerWebRTC.dataChannel.send(logMsg);
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

window.SteamViewerWebRTC = {
    peerConnection: null,
    dataChannel: null,
    dotNetRef: null,
    localStream: null,
    remoteVideo: null,      // Video element for incoming stream
    remoteCanvas: null,     // Canvas for rendering
    remoteCtx: null,        // Canvas 2D context

    // Custom TURN server config (set from C# via setTurnConfig)
    customTurnServer: null,

    // Set custom TURN server configuration
    // Call this before initialize() to use your own TURN server
    setTurnConfig(urls, username, credential) {
        console.log('Setting custom TURN server:', urls);
        this.customTurnServer = { urls, username, credential };
    },

    // Build ICE servers list
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

    // Initialize WebRTC with STUN/TURN servers
    async initialize(dotNetReference) {
        this.dotNetRef = dotNetReference;

        const iceServers = this.buildIceServers();
        console.log('=== WebRTC INIT ===');
        console.log('ICE servers configured:', JSON.stringify(iceServers, null, 2));

        const config = {
            iceServers,
            iceCandidatePoolSize: 25,
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require',
            iceTransportPolicy: 'all'
        };

        try {
            this.peerConnection = new RTCPeerConnection(config);
            console.log('RTCPeerConnection created');

            // Track candidate types found
            const candidateTypes = { host: 0, srflx: 0, relay: 0, prflx: 0 };

            // Handle ICE candidates
            this.peerConnection.onicecandidate = async (event) => {
                if (event.candidate) {
                    const candidateType = event.candidate.candidate.match(/typ (\w+)/)?.[1] || 'unknown';
                    candidateTypes[candidateType] = (candidateTypes[candidateType] || 0) + 1;

                    // Full candidate logging for debugging
                    console.log(`=== ICE CANDIDATE: ${candidateType.toUpperCase()} ===`);
                    console.log('Full candidate:', event.candidate.candidate);
                    console.log('Candidate counts so far:', candidateTypes);

                    if (candidateType === 'relay') {
                        console.log('*** RELAY CANDIDATE FOUND - TURN SERVER WORKING! ***');
                    }

                    await this.dotNetRef.invokeMethodAsync('OnIceCandidateCallback', JSON.stringify(event.candidate));
                } else {
                    console.log('=== ICE GATHERING COMPLETE ===');
                    console.log('Final candidate counts:', candidateTypes);
                    if (candidateTypes.relay === 0) {
                        console.error('!!! NO RELAY CANDIDATES - TURN SERVER NOT WORKING !!!');
                        console.error('Check: 1) TURN server running 2) Correct port 3) Credentials match');
                    }
                }
            };

            // Handle ICE gathering state
            this.peerConnection.onicegatheringstatechange = () => {
                console.log('ICE gathering state:', this.peerConnection.iceGatheringState);
            };

            // Handle connection state changes
            this.peerConnection.onconnectionstatechange = async () => {
                console.log('=== CONNECTION STATE:', this.peerConnection.connectionState, '===');
                if (this.peerConnection.connectionState === 'failed') {
                    console.error('CONNECTION FAILED - Possible causes:');
                    console.error('1. No relay candidates (TURN not working)');
                    console.error('2. Firewall blocking');
                    console.error('3. NAT traversal failed');
                }
                await this.dotNetRef.invokeMethodAsync('OnConnectionStateChangeCallback', this.peerConnection.connectionState);
            };

            // Handle ICE connection state for more debugging
            this.peerConnection.oniceconnectionstatechange = () => {
                console.log('ICE connection state:', this.peerConnection.iceConnectionState);
                if (this.peerConnection.iceConnectionState === 'failed') {
                    console.error('ICE CONNECTION FAILED');
                }
            };

            // Handle ICE candidate errors (ignore STUN timeouts on TURN servers - expected)
            this.peerConnection.onicecandidateerror = (event) => {
                // Error 701 = STUN timeout, often happens on TURN servers - not critical
                if (event.errorCode === 701 && event.url?.includes('relay.metered.ca')) {
                    // Silently ignore - Google STUN servers will handle NAT traversal
                    return;
                }
                console.error('ICE candidate error:', event.errorCode, event.errorText, event.url);
            };

            // Handle renegotiation needed (when tracks are added after connection)
            this.peerConnection.onnegotiationneeded = async () => {
                console.log('=== NEGOTIATION NEEDED (track added post-connection) ===');
                try {
                    // Only renegotiate if we're in a stable state
                    if (this.peerConnection.signalingState === 'stable') {
                        const offer = await this.peerConnection.createOffer();
                        offer.sdp = this.modifySdpForLowLatency(offer.sdp);
                        await this.peerConnection.setLocalDescription(offer);
                        console.log('Renegotiation offer created, sending to peer...');
                        await this.dotNetRef.invokeMethodAsync('OnRenegotiationNeededCallback', JSON.stringify(offer));
                    } else {
                        console.log('Skipping renegotiation, signaling state:', this.peerConnection.signalingState);
                    }
                } catch (err) {
                    console.error('Renegotiation failed:', err);
                }
            };

            // Handle incoming data channels
            this.peerConnection.ondatachannel = (event) => {
                this.setupDataChannel(event.channel);
            };

            // Handle incoming video track
            this.peerConnection.ontrack = (event) => {
                console.log('=== ONTRACK EVENT ===');
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
                    this.remoteVideo = video;

                    // Log video events
                    video.onloadstart = () => console.log('Video: loadstart');
                    video.onloadeddata = () => console.log('Video: loadeddata');
                    video.oncanplay = () => console.log('Video: canplay');
                    video.onplaying = () => console.log('Video: playing');
                    video.onstalled = () => console.warn('Video: STALLED');
                    video.onerror = (e) => console.error('Video ERROR:', video.error);

                    // Track if we got any frames
                    let frameCount = 0;
                    let lastFrameTime = 0;

                    // Wait for canvas with retry logic (Blazor may not have rendered yet)
                    const setupCanvas = (retryCount = 0) => {
                        const canvas = document.getElementById('remoteCanvas');
                        if (!canvas) {
                            if (retryCount < 10) { // Retry for up to 1 second (reduced from 5s)
                                console.log(`Canvas not found, retry ${retryCount + 1}/10...`);
                                setTimeout(() => setupCanvas(retryCount + 1), 100);
                                return;
                            }
                            console.error('Canvas not found after 1 second!');
                            return;
                        }

                        console.log('Canvas found, setting up renderer');
                        const ctx = canvas.getContext('2d');
                        this.remoteCanvas = canvas;
                        this.remoteCtx = ctx;

                        video.onloadedmetadata = () => {
                            const width = video.videoWidth;
                            const height = video.videoHeight;
                            console.log(`=== VIDEO METADATA LOADED ===`);
                            console.log(`Dimensions: ${width}x${height}`);

                            if (width === 0 || height === 0) {
                                console.error('Video has ZERO dimensions!');
                                return;
                            }

                            canvas.width = width;
                            canvas.height = height;

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
                                    frameCount++;

                                    // Log frame rate every 2 seconds
                                    if (now - lastFrameTime > 2000) {
                                        const fps = frameCount / ((now - lastFrameTime) / 1000);
                                        console.log(`Video rendering (RVFC): ${fps.toFixed(1)} FPS, ${frameCount} frames`);
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

            console.log('WebRTC initialized successfully');
            return true;
        } catch (err) {
            console.error('Failed to initialize WebRTC:', err);
            return false;
        }
    },

    // Create data channel (for host)
    createDataChannel(name) {
        if (!this.peerConnection) {
            console.error('PeerConnection not initialized');
            return;
        }

        this.dataChannel = this.peerConnection.createDataChannel(name, {
            ordered: true
        });
        this.setupDataChannel(this.dataChannel);
        console.log(`Data channel '${name}' created`);
    },

    setupDataChannel(channel) {
        this.dataChannel = channel;
        channel.binaryType = 'arraybuffer';

        channel.onopen = async () => {
            console.log('Data channel opened');
            await this.dotNetRef.invokeMethodAsync('OnDataChannelOpenCallback');
        };

        channel.onclose = async () => {
            console.log('Data channel closed');
            await this.dotNetRef.invokeMethodAsync('OnDataChannelCloseCallback');
        };

        channel.onmessage = async (event) => {
            if (typeof event.data === 'string') {
                // Check if this is a relayed log message from peer
                try {
                    const parsed = JSON.parse(event.data);
                    if (parsed._logRelay) {
                        // This is a log relay message - handle it specially
                        window.SteamViewerLogger?.handleRelayedLog(parsed.level, parsed.message, parsed.from);
                        return; // Don't forward to C# as regular message
                    }
                } catch (e) {
                    // Not JSON or not a log relay - continue normally
                }

                console.log('Data channel message received:', event.data?.substring?.(0, 50));
                await this.dotNetRef.invokeMethodAsync('OnDataChannelMessageCallback', event.data);
            } else if (event.data instanceof ArrayBuffer) {
                console.log('Data channel binary message received:', event.data.byteLength, 'bytes');
                const uint8Array = new Uint8Array(event.data);
                await this.dotNetRef.invokeMethodAsync('OnDataChannelBinaryMessageCallback', Array.from(uint8Array));
            }
        };

        channel.onerror = (error) => {
            console.error('Data channel error:', error);
        };
    },

    // Send string data over data channel
    sendData(data) {
        if (this.dataChannel && this.dataChannel.readyState === 'open') {
            this.dataChannel.send(data);
            return true;
        }
        return false;
    },

    // Send binary data over data channel
    sendBinaryData(data) {
        if (this.dataChannel && this.dataChannel.readyState === 'open') {
            const uint8Array = new Uint8Array(data);
            this.dataChannel.send(uint8Array.buffer);
            return true;
        }
        console.warn('Data channel not open');
        return false;
    },

    // Modify SDP for lower latency
    modifySdpForLowLatency(sdp) {
        let modified = sdp;

        // Lower bandwidth for faster encoding
        if (!modified.includes('b=AS:')) {
            modified = modified.replace(/m=video.*\r\n/g, '$&b=AS:3000\r\n');
        }

        // Prefer H264 Baseline profile (fastest encoding, most compatible)
        // Move H264 to top of codec list if present
        const lines = modified.split('\r\n');
        let h264PayloadTypes = [];

        // Find H264 payload types
        lines.forEach(line => {
            if (line.includes('a=rtpmap:') && line.toLowerCase().includes('h264')) {
                const match = line.match(/a=rtpmap:(\d+)/);
                if (match) h264PayloadTypes.push(match[1]);
            }
        });

        // Reorder m=video line to prefer H264
        if (h264PayloadTypes.length > 0) {
            modified = modified.replace(/(m=video \d+ [^ ]+ )(.+)/, (match, prefix, payloads) => {
                const payloadList = payloads.split(' ');
                const reordered = [...h264PayloadTypes, ...payloadList.filter(p => !h264PayloadTypes.includes(p))];
                return prefix + reordered.join(' ');
            });
        }

        return modified;
    },

    // Create SDP offer (for viewer initiating connection)
    async createOffer() {
        if (!this.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const offer = await this.peerConnection.createOffer({
            offerToReceiveVideo: true,
            offerToReceiveAudio: false
        });

        // Modify SDP for lower latency
        offer.sdp = this.modifySdpForLowLatency(offer.sdp);

        await this.peerConnection.setLocalDescription(offer);
        console.log('=== SDP OFFER CREATED ===');
        console.log('Has video:', offer.sdp.includes('m=video'));
        console.log('Has H264:', offer.sdp.toLowerCase().includes('h264'));
        console.log('Has VP8:', offer.sdp.toLowerCase().includes('vp8'));
        console.log('Has VP9:', offer.sdp.toLowerCase().includes('vp9'));
        return JSON.stringify(offer);
    },

    // Create SDP answer (for host responding to connection)
    async createAnswer() {
        if (!this.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const answer = await this.peerConnection.createAnswer();

        // Modify SDP for lower latency
        answer.sdp = this.modifySdpForLowLatency(answer.sdp);

        await this.peerConnection.setLocalDescription(answer);
        console.log('SDP answer created');
        return JSON.stringify(answer);
    },

    // Set remote SDP description
    async setRemoteDescription(sdpJson) {
        if (!this.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const sdp = JSON.parse(sdpJson);
        console.log('=== SETTING REMOTE DESCRIPTION ===');
        console.log('Type:', sdp.type);
        console.log('Has video:', sdp.sdp?.includes('m=video'));
        console.log('Has H264:', sdp.sdp?.toLowerCase().includes('h264'));

        await this.peerConnection.setRemoteDescription(new RTCSessionDescription(sdp));
        console.log('Remote description set successfully');
        console.log('Signaing state:', this.peerConnection.signalingState);
    },

    // Add ICE candidate
    async addIceCandidate(candidateJson) {
        if (!this.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const candidate = JSON.parse(candidateJson);
        await this.peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
        console.log('ICE candidate added');
    },

    // Start screen capture (for host)
    async startScreenCapture() {
        console.log('=== Starting screen capture ===');
        console.log('navigator.mediaDevices:', !!navigator.mediaDevices);
        console.log('getDisplayMedia:', !!navigator.mediaDevices?.getDisplayMedia);
        console.log('peerConnection:', !!this.peerConnection);
        console.log('peerConnection state:', this.peerConnection?.connectionState);
        console.log('signalingState:', this.peerConnection?.signalingState);

        if (!this.peerConnection) {
            console.error('Cannot start screen capture - no peer connection!');
            return false;
        }

        try {
            this.localStream = await navigator.mediaDevices.getDisplayMedia({
                video: {
                    cursor: 'motion',  // Only redraw cursor when moving (reduces capture overhead)
                    width: { ideal: 1920, max: 3840 },
                    height: { ideal: 1080, max: 2160 },
                    frameRate: { ideal: 30, max: 60 }
                },
                audio: false,
                preferCurrentTab: false,
                selfBrowserSurface: 'exclude',
                systemAudio: 'exclude'
            });

            // Add video track to peer connection with encoding parameters
            const videoTracks = this.localStream.getVideoTracks();
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
                    track.contentHint = 'detail'; // Optimizes for sharp text/UI
                }

                const sender = this.peerConnection.addTrack(track, this.localStream);
                console.log('Track added to peer connection, sender:', !!sender);

                // Configure encoding for lower latency
                const params = sender.getParameters();
                if (!params.encodings) {
                    params.encodings = [{}];
                }
                params.encodings[0].maxBitrate = 3000000; // 3 Mbps (lower = less latency)
                params.encodings[0].maxFramerate = 30;  // Match getDisplayMedia ideal (24 causes stuttering)
                params.encodings[0].priority = 'high';
                params.encodings[0].networkPriority = 'high';
                // Disable scalability for lower latency
                params.encodings[0].scalabilityMode = 'L1T1';
                // Maintain frame rate even if quality has to drop (better for responsiveness)
                params.encodings[0].degradationPreference = 'maintain-framerate';
                sender.setParameters(params).catch(e => console.warn('Could not set encoding params:', e));

                // Handle track ending (user stops sharing)
                track.onended = () => {
                    console.log('Screen sharing stopped by user');
                };
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
    stopScreenCapture() {
        if (this.localStream) {
            this.localStream.getTracks().forEach(track => track.stop());
            this.localStream = null;
            console.log('Screen capture stopped');
        }
    },

    // Manually check for and setup any video tracks (call after renegotiation)
    checkForVideoTracks() {
        if (!this.peerConnection) return false;

        const receivers = this.peerConnection.getReceivers();
        const videoReceiver = receivers.find(r => r.track?.kind === 'video');

        if (videoReceiver && videoReceiver.track) {
            console.log('=== MANUAL TRACK CHECK: Found video track ===');
            const track = videoReceiver.track;

            // If we already have this track set up, skip
            if (this.remoteVideo?.srcObject?.getVideoTracks().includes(track)) {
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

            this.remoteVideo = video;

            const setupCanvas = (retryCount = 0) => {
                const canvas = document.getElementById('remoteCanvas');
                if (!canvas) {
                    if (retryCount < 10) { // Reduced from 50 (5s) to 10 (1s)
                        setTimeout(() => setupCanvas(retryCount + 1), 100);
                        return;
                    }
                    console.error('Canvas not found for manual track setup');
                    return;
                }

                const ctx = canvas.getContext('2d');
                this.remoteCanvas = canvas;
                this.remoteCtx = ctx;

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
    getVideoDiagnostics() {
        const info = {
            peerConnection: this.peerConnection?.connectionState || 'none',
            iceConnection: this.peerConnection?.iceConnectionState || 'none',
            signalingState: this.peerConnection?.signalingState || 'none',
            hasRemoteVideo: !!this.remoteVideo,
            videoReadyState: this.remoteVideo?.readyState || -1,
            videoPaused: this.remoteVideo?.paused,
            videoEnded: this.remoteVideo?.ended,
            videoWidth: this.remoteVideo?.videoWidth || 0,
            videoHeight: this.remoteVideo?.videoHeight || 0,
            hasCanvas: !!this.remoteCanvas,
            canvasWidth: this.remoteCanvas?.width || 0,
            canvasHeight: this.remoteCanvas?.height || 0,
            receivers: []
        };

        if (this.peerConnection) {
            const receivers = this.peerConnection.getReceivers();
            info.receivers = receivers.map(r => ({
                kind: r.track?.kind,
                readyState: r.track?.readyState,
                muted: r.track?.muted,
                enabled: r.track?.enabled
            }));
        }

        console.log('=== VIDEO DIAGNOSTICS ===');
        console.log(JSON.stringify(info, null, 2));
        return info;
    },

    // Get connection stats
    async getStats() {
        if (!this.peerConnection) {
            return null;
        }

        const stats = await this.peerConnection.getStats();
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

    // Log latency stats for debugging - call from browser console: SteamViewerWebRTC.logLatencyStats()
    async logLatencyStats() {
        if (!this.peerConnection) {
            console.log('[LATENCY] No peer connection');
            return;
        }
        const stats = await this.peerConnection.getStats();
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

    // Frame capture for external viewer window
    frameCaptureDotNetRef: null,
    frameCaptureEnabled: false,
    frameCaptureAnimationId: null,
    lastFrameTime: 0,
    frameInterval: 50, // ~20fps (less CPU than 30fps)
    captureCanvas: null, // Downscaled canvas for encoding
    captureCtx: null,

    // Check if video is ready for capture
    isVideoReady() {
        return this.remoteCanvas &&
               this.remoteCanvas.width > 0 &&
               this.remoteCanvas.height > 0 &&
               this.remoteVideo &&
               this.remoteVideo.readyState >= 2;
    },

    // Enable frame capture to relay to viewer window
    async startFrameCapture(dotNetRef) {
        console.log('Starting frame capture for viewer window');
        this.frameCaptureDotNetRef = dotNetRef;
        this.frameCaptureEnabled = true;
        this.lastFrameTime = 0;

        // Wait for video to be ready (up to 5 seconds)
        let attempts = 0;
        while (!this.isVideoReady() && attempts < 50) {
            await new Promise(r => setTimeout(r, 100));
            attempts++;
        }

        if (!this.isVideoReady()) {
            console.warn('Video not ready after 5s, starting capture anyway');
        } else {
            console.log('Video ready, starting frame capture');
        }

        // Use requestAnimationFrame for better performance
        const captureLoop = (timestamp) => {
            if (!this.frameCaptureEnabled) return;

            // Throttle to target frame rate
            if (timestamp - this.lastFrameTime >= this.frameInterval) {
                this.captureAndSendFrame();
                this.lastFrameTime = timestamp;
            }

            this.frameCaptureAnimationId = requestAnimationFrame(captureLoop);
        };

        this.frameCaptureAnimationId = requestAnimationFrame(captureLoop);
        return true;
    },

    stopFrameCapture() {
        console.log('Stopping frame capture');
        this.frameCaptureEnabled = false;
        if (this.frameCaptureAnimationId) {
            cancelAnimationFrame(this.frameCaptureAnimationId);
            this.frameCaptureAnimationId = null;
        }
        this.frameCaptureDotNetRef = null;
        this.captureCanvas = null;
        this.captureCtx = null;
    },

    captureAndSendFrame() {
        if (!this.frameCaptureEnabled || !this.remoteCanvas || !this.frameCaptureDotNetRef) return;

        try {
            const srcWidth = this.remoteCanvas.width;
            const srcHeight = this.remoteCanvas.height;

            if (srcWidth === 0 || srcHeight === 0) return;

            // Downscale large resolutions for encoding (max 1920x1080 for capture)
            const maxWidth = 1920;
            const maxHeight = 1080;
            let destWidth = srcWidth;
            let destHeight = srcHeight;

            if (srcWidth > maxWidth || srcHeight > maxHeight) {
                const scale = Math.min(maxWidth / srcWidth, maxHeight / srcHeight);
                destWidth = Math.round(srcWidth * scale);
                destHeight = Math.round(srcHeight * scale);
            }

            // Create/reuse downscale canvas
            if (!this.captureCanvas || this.captureCanvas.width !== destWidth || this.captureCanvas.height !== destHeight) {
                this.captureCanvas = document.createElement('canvas');
                this.captureCanvas.width = destWidth;
                this.captureCanvas.height = destHeight;
                this.captureCtx = this.captureCanvas.getContext('2d');
            }

            // Draw downscaled frame
            this.captureCtx.drawImage(this.remoteCanvas, 0, 0, destWidth, destHeight);

            // Convert to JPEG (0.85 quality balances size/quality/CPU)
            const dataUrl = this.captureCanvas.toDataURL('image/jpeg', 0.85);

            // Send to C# - use original dimensions for coordinate scaling
            const base64Data = dataUrl.replace(/^data:image\/jpeg;base64,/, '');
            this.frameCaptureDotNetRef.invokeMethodAsync('OnFrameCaptured', base64Data, srcWidth, srcHeight);
        } catch (e) {
            // Ignore capture errors
        }
    },

    // Close connection
    close() {
        this.stopFrameCapture();
        this.stopScreenCapture();

        if (this.remoteVideo) {
            this.remoteVideo.pause();
            this.remoteVideo.srcObject = null;
            this.remoteVideo = null;
        }

        this.remoteCanvas = null;
        this.remoteCtx = null;

        if (this.dataChannel) {
            this.dataChannel.close();
            this.dataChannel = null;
        }

        if (this.peerConnection) {
            this.peerConnection.close();
            this.peerConnection = null;
        }

        this.dotNetRef = null;
        console.log('WebRTC connection closed');
    }
};

// Input capture for remote canvas
window.SteamViewerInput = {
    canvas: null,
    dotNetRef: null,
    isCapturing: false,
    isLocked: false,  // Capture lock - only send inputs when locked

    initialize(canvasId, dotNetReference) {
        // Clean up any previous instance
        if (this.canvas) {
            this.stop();
        }

        this.canvas = document.getElementById(canvasId);
        this.dotNetRef = dotNetReference;

        if (!this.canvas) {
            console.error(`Canvas '${canvasId}' not found`);
            return false;
        }

        // Create lock indicator overlay
        this.createLockIndicator();

        // Bind event handlers (store references for removal)
        this._boundMouseMove = (e) => this.handleMouseMove(e);
        this._boundMouseDown = (e) => this.handleMouseDown(e);
        this._boundMouseUp = (e) => this.handleMouseUp(e);
        this._boundWheel = (e) => this.handleWheel(e);
        this._boundKeyDown = (e) => this.handleKeyDown(e);
        this._boundKeyUp = (e) => this.handleKeyUp(e);
        this._boundDblClick = (e) => {
            e.preventDefault();
            this.toggleLock();
        };

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

        // Double-click to lock/capture
        this.canvas.addEventListener('dblclick', this._boundDblClick);

        this.isCapturing = true;
        this.isLocked = false;
        console.log('Input capture initialized (double-click to lock, Escape to release)');
        return true;
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
        if (!this.lockIndicator) return;
        if (this.isLocked) {
            this.lockIndicator.textContent = '🔒 Input Locked (Esc to release)';
            this.lockIndicator.style.background = '#a6e3a1';
            this.lockIndicator.style.color = '#1e1e2e';
        } else {
            this.lockIndicator.textContent = '🔓 Double-click to capture';
            this.lockIndicator.style.background = '#45475a';
            this.lockIndicator.style.color = '#cdd6f4';
        }
    },

    toggleLock() {
        this.isLocked = !this.isLocked;
        this.updateLockIndicator();
        if (this.isLocked) {
            this.canvas.focus();
            console.log('Input LOCKED - sending inputs to host');
        } else {
            console.log('Input UNLOCKED - inputs disabled');
        }
    },

    unlock() {
        this.isLocked = false;
        this.updateLockIndicator();
        console.log('Input UNLOCKED');
    },

    getScaledCoords(e) {
        const rect = this.canvas.getBoundingClientRect();

        // Account for object-fit: contain letterboxing
        // Calculate the actual rendered size of the canvas content
        const canvasAspect = this.canvas.width / this.canvas.height;
        const rectAspect = rect.width / rect.height;

        let renderWidth, renderHeight, offsetX, offsetY;

        if (rectAspect > canvasAspect) {
            // Container is wider than canvas - letterbox on sides
            renderHeight = rect.height;
            renderWidth = rect.height * canvasAspect;
            offsetX = (rect.width - renderWidth) / 2;
            offsetY = 0;
        } else {
            // Container is taller than canvas - letterbox on top/bottom
            renderWidth = rect.width;
            renderHeight = rect.width / canvasAspect;
            offsetX = 0;
            offsetY = (rect.height - renderHeight) / 2;
        }

        // Calculate position relative to actual content area
        const relX = e.clientX - rect.left - offsetX;
        const relY = e.clientY - rect.top - offsetY;

        // Scale to canvas internal coordinates
        const scaleX = this.canvas.width / renderWidth;
        const scaleY = this.canvas.height / renderHeight;

        return {
            x: Math.max(0, Math.min(this.canvas.width, relX * scaleX)),
            y: Math.max(0, Math.min(this.canvas.height, relY * scaleY))
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

    async handleMouseMove(e) {
        if (!this.isCapturing || !this.isLocked) return;
        const coords = this.getScaledCoords(e);
        // Pass canvas dimensions (capture size) for accurate coordinate mapping on host
        await this.dotNetRef.invokeMethodAsync('OnMouseMove', coords.x, coords.y,
            this.canvas.width, this.canvas.height);
    },

    async handleMouseDown(e) {
        if (!this.isCapturing || !this.isLocked) return;
        e.preventDefault();
        const coords = this.getScaledCoords(e);
        const button = ['left', 'middle', 'right'][e.button] || 'left';
        // Pass canvas dimensions (capture size) for accurate coordinate mapping on host
        await this.dotNetRef.invokeMethodAsync('OnMouseDown', button, coords.x, coords.y,
            this.canvas.width, this.canvas.height);
    },

    async handleMouseUp(e) {
        if (!this.isCapturing || !this.isLocked) return;
        e.preventDefault();
        const coords = this.getScaledCoords(e);
        const button = ['left', 'middle', 'right'][e.button] || 'left';
        // Pass canvas dimensions (capture size) for accurate coordinate mapping on host
        await this.dotNetRef.invokeMethodAsync('OnMouseUp', button, coords.x, coords.y,
            this.canvas.width, this.canvas.height);
    },

    async handleWheel(e) {
        if (!this.isCapturing || !this.isLocked) return;
        e.preventDefault();
        await this.dotNetRef.invokeMethodAsync('OnMouseWheel', e.deltaX, e.deltaY);
    },

    async handleKeyDown(e) {
        if (!this.isCapturing) return;

        // Escape always releases the lock, even when locked
        if (e.key === 'Escape') {
            e.preventDefault();
            this.unlock();
            return;
        }

        if (!this.isLocked) return;
        e.preventDefault();
        await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
    },

    async handleKeyUp(e) {
        if (!this.isCapturing || !this.isLocked) return;
        // Don't send Escape keyup to host
        if (e.key === 'Escape') return;
        e.preventDefault();
        await this.dotNetRef.invokeMethodAsync('OnKeyUp', e.key, this.getModifiers(e));
    },

    stop() {
        this.isCapturing = false;
        this.isLocked = false;

        // Remove lock indicator
        if (this.lockIndicator) {
            this.lockIndicator.remove();
            this.lockIndicator = null;
        }

        // Remove event listeners from canvas
        if (this.canvas) {
            this.canvas.removeEventListener('mousemove', this._boundMouseMove);
            this.canvas.removeEventListener('mousedown', this._boundMouseDown);
            this.canvas.removeEventListener('mouseup', this._boundMouseUp);
            this.canvas.removeEventListener('wheel', this._boundWheel);
            this.canvas.removeEventListener('keydown', this._boundKeyDown);
            this.canvas.removeEventListener('keyup', this._boundKeyUp);
            this.canvas.removeEventListener('dblclick', this._boundDblClick);
        }

        this.canvas = null;
        this.dotNetRef = null;
        console.log('Input capture stopped');
    }
};

// JPEG frame rendering for remote viewer window
window.SteamViewerViewer = {
    canvas: null,
    ctx: null,
    img: null,

    // Render a JPEG frame to the viewer canvas
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

        // Update canvas size if needed
        if (this.canvas.width !== width || this.canvas.height !== height) {
            this.canvas.width = width;
            this.canvas.height = height;
        }

        // Create image and draw to canvas
        if (!this.img) {
            this.img = new Image();
            this.img.onload = () => {
                if (this.ctx) {
                    this.ctx.drawImage(this.img, 0, 0, this.canvas.width, this.canvas.height);
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
            codec: 'avc1.42E01E', // H.264 Baseline Profile
            width: 1920,
            height: 1080
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
