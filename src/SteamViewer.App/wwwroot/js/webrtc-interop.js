// WebRTC Interop for SteamViewer
// Provides browser WebRTC API access to Blazor via JS interop

window.SteamViewerWebRTC = {
    peerConnection: null,
    dataChannel: null,
    dotNetRef: null,
    localStream: null,

    // Initialize WebRTC with STUN servers
    async initialize(dotNetReference) {
        this.dotNetRef = dotNetReference;

        const config = {
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' },
                { urls: 'stun:stun1.l.google.com:19302' },
                { urls: 'stun:stun2.l.google.com:19302' }
            ]
        };

        try {
            this.peerConnection = new RTCPeerConnection(config);

            // Handle ICE candidates
            this.peerConnection.onicecandidate = async (event) => {
                if (event.candidate) {
                    await this.dotNetRef.invokeMethodAsync('OnIceCandidateCallback', JSON.stringify(event.candidate));
                }
            };

            // Handle connection state changes
            this.peerConnection.onconnectionstatechange = async () => {
                await this.dotNetRef.invokeMethodAsync('OnConnectionStateChangeCallback', this.peerConnection.connectionState);
            };

            // Handle incoming data channels
            this.peerConnection.ondatachannel = (event) => {
                this.setupDataChannel(event.channel);
            };

            // Handle incoming video track
            this.peerConnection.ontrack = (event) => {
                console.log('Received remote track:', event.track.kind);

                if (event.track.kind === 'video') {
                    const video = document.createElement('video');
                    video.srcObject = event.streams[0];
                    video.autoplay = true;
                    video.muted = true;
                    video.playsInline = true;

                    // Render to canvas when frames are available
                    const canvas = document.getElementById('remoteCanvas');
                    if (canvas) {
                        const ctx = canvas.getContext('2d');
                        video.onloadedmetadata = () => {
                            canvas.width = video.videoWidth;
                            canvas.height = video.videoHeight;
                            console.log(`Video dimensions: ${video.videoWidth}x${video.videoHeight}`);

                            const renderFrame = () => {
                                if (!video.paused && !video.ended && video.readyState >= 2) {
                                    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                                }
                                requestAnimationFrame(renderFrame);
                            };
                            video.play().then(() => renderFrame());
                        };
                    }
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
                await this.dotNetRef.invokeMethodAsync('OnDataChannelMessageCallback', event.data);
            } else if (event.data instanceof ArrayBuffer) {
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
        console.warn('Data channel not open');
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

    // Create SDP offer (for viewer initiating connection)
    async createOffer() {
        if (!this.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const offer = await this.peerConnection.createOffer();
        await this.peerConnection.setLocalDescription(offer);
        console.log('SDP offer created');
        return JSON.stringify(offer);
    },

    // Create SDP answer (for host responding to connection)
    async createAnswer() {
        if (!this.peerConnection) {
            throw new Error('PeerConnection not initialized');
        }

        const answer = await this.peerConnection.createAnswer();
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
        await this.peerConnection.setRemoteDescription(new RTCSessionDescription(sdp));
        console.log('Remote description set');
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
        try {
            this.localStream = await navigator.mediaDevices.getDisplayMedia({
                video: {
                    cursor: 'always',
                    width: { ideal: 1920, max: 2560 },
                    height: { ideal: 1080, max: 1440 },
                    frameRate: { ideal: 30, max: 60 }
                },
                audio: false
            });

            // Add video track to peer connection
            this.localStream.getVideoTracks().forEach(track => {
                console.log('Adding video track to peer connection');
                this.peerConnection.addTrack(track, this.localStream);

                // Handle track ending (user stops sharing)
                track.onended = () => {
                    console.log('Screen sharing stopped by user');
                };
            });

            console.log('Screen capture started');
            return true;
        } catch (err) {
            console.error('Screen capture failed:', err);
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

    // Close connection
    close() {
        this.stopScreenCapture();

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

    initialize(canvasId, dotNetReference) {
        this.canvas = document.getElementById(canvasId);
        this.dotNetRef = dotNetReference;

        if (!this.canvas) {
            console.error(`Canvas '${canvasId}' not found`);
            return false;
        }

        // Mouse events
        this.canvas.addEventListener('mousemove', (e) => this.handleMouseMove(e));
        this.canvas.addEventListener('mousedown', (e) => this.handleMouseDown(e));
        this.canvas.addEventListener('mouseup', (e) => this.handleMouseUp(e));
        this.canvas.addEventListener('wheel', (e) => this.handleWheel(e), { passive: false });
        this.canvas.addEventListener('contextmenu', (e) => e.preventDefault());

        // Make canvas focusable for keyboard events
        this.canvas.tabIndex = 0;
        this.canvas.style.outline = 'none';
        this.canvas.addEventListener('keydown', (e) => this.handleKeyDown(e));
        this.canvas.addEventListener('keyup', (e) => this.handleKeyUp(e));

        // Focus canvas on click
        this.canvas.addEventListener('click', () => this.canvas.focus());

        this.isCapturing = true;
        console.log('Input capture initialized');
        return true;
    },

    getScaledCoords(e) {
        const rect = this.canvas.getBoundingClientRect();
        const scaleX = this.canvas.width / rect.width;
        const scaleY = this.canvas.height / rect.height;
        return {
            x: (e.clientX - rect.left) * scaleX,
            y: (e.clientY - rect.top) * scaleY
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
        if (!this.isCapturing) return;
        const coords = this.getScaledCoords(e);
        await this.dotNetRef.invokeMethodAsync('OnMouseMove', coords.x, coords.y);
    },

    async handleMouseDown(e) {
        if (!this.isCapturing) return;
        e.preventDefault();
        const coords = this.getScaledCoords(e);
        const button = ['left', 'middle', 'right'][e.button] || 'left';
        await this.dotNetRef.invokeMethodAsync('OnMouseDown', button, coords.x, coords.y);
    },

    async handleMouseUp(e) {
        if (!this.isCapturing) return;
        e.preventDefault();
        const coords = this.getScaledCoords(e);
        const button = ['left', 'middle', 'right'][e.button] || 'left';
        await this.dotNetRef.invokeMethodAsync('OnMouseUp', button, coords.x, coords.y);
    },

    async handleWheel(e) {
        if (!this.isCapturing) return;
        e.preventDefault();
        await this.dotNetRef.invokeMethodAsync('OnMouseWheel', e.deltaX, e.deltaY);
    },

    async handleKeyDown(e) {
        if (!this.isCapturing) return;
        e.preventDefault();
        await this.dotNetRef.invokeMethodAsync('OnKeyDown', e.key, this.getModifiers(e));
    },

    async handleKeyUp(e) {
        if (!this.isCapturing) return;
        e.preventDefault();
        await this.dotNetRef.invokeMethodAsync('OnKeyUp', e.key, this.getModifiers(e));
    },

    stop() {
        this.isCapturing = false;
        console.log('Input capture stopped');
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
