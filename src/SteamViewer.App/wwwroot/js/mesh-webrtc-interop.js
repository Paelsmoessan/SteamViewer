// Mesh WebRTC Interop for SteamViewer Collaboration Mode
// Manages multiple peer connections for N-way mesh topology

window.SteamViewerMeshWebRTC = {
    // Map of peer connections: peerId -> { pc, dataChannel, remoteVideo, remoteCanvas, remoteCtx }
    peers: new Map(),
    dotNetRef: null,
    localStream: null,  // Our screen share stream (shared to all peers)
    customTurnServer: null,

    // Set custom TURN server configuration
    setTurnConfig(urls, username, credential) {
        console.log('[Mesh] Setting custom TURN server:', urls);
        this.customTurnServer = { urls, username, credential };
    },

    // Build ICE servers list
    buildIceServers() {
        const servers = [
            { urls: 'stun:stun.l.google.com:19302' },
            { urls: 'stun:stun1.l.google.com:19302' },
        ];

        if (this.customTurnServer?.urls?.length > 0) {
            for (const url of this.customTurnServer.urls) {
                servers.push({
                    urls: url,
                    username: this.customTurnServer.username,
                    credential: this.customTurnServer.credential
                });
            }
        }
        return servers;
    },

    // Initialize the mesh manager
    initialize(dotNetReference) {
        this.dotNetRef = dotNetReference;
        console.log('[Mesh] Initialized');
        return true;
    },

    // Create a new peer connection for a specific peer
    async createPeerConnection(peerId) {
        console.log(`[Mesh] Creating peer connection for: ${peerId}`);

        if (this.peers.has(peerId)) {
            console.warn(`[Mesh] Peer ${peerId} already exists, closing old connection`);
            await this.closePeer(peerId);
        }

        const config = {
            iceServers: this.buildIceServers(),
            iceCandidatePoolSize: 10,
            bundlePolicy: 'max-bundle',
            rtcpMuxPolicy: 'require'
        };

        const pc = new RTCPeerConnection(config);
        const peerInfo = {
            pc,
            dataChannel: null,
            remoteVideo: null,
            remoteCanvas: null,
            remoteCtx: null,
            isDataChannelOpen: false
        };

        this.peers.set(peerId, peerInfo);

        // ICE candidate handler
        pc.onicecandidate = async (event) => {
            if (event.candidate) {
                console.log(`[Mesh] ICE candidate for ${peerId}:`, event.candidate.candidate.substring(0, 50));
                await this.dotNetRef.invokeMethodAsync('OnMeshIceCandidateCallback', peerId, JSON.stringify(event.candidate));
            }
        };

        // Connection state handler
        pc.onconnectionstatechange = async () => {
            console.log(`[Mesh] Connection state for ${peerId}:`, pc.connectionState);
            await this.dotNetRef.invokeMethodAsync('OnMeshConnectionStateChangeCallback', peerId, pc.connectionState);
        };

        // Incoming data channel handler
        pc.ondatachannel = (event) => {
            console.log(`[Mesh] Incoming data channel from ${peerId}`);
            this.setupDataChannel(peerId, event.channel);
        };

        // Incoming video track handler
        pc.ontrack = (event) => {
            console.log(`[Mesh] Incoming track from ${peerId}:`, event.track.kind);
            if (event.track.kind === 'video') {
                this.setupRemoteVideo(peerId, event.streams[0]);
            }
        };

        // If we're already sharing screen, add track to this new peer
        if (this.localStream) {
            const videoTrack = this.localStream.getVideoTracks()[0];
            if (videoTrack) {
                pc.addTrack(videoTrack, this.localStream);
                console.log(`[Mesh] Added existing screen share to ${peerId}`);
            }
        }

        return true;
    },

    // Create data channel (caller side)
    createDataChannel(peerId, name = 'data') {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) {
            console.error(`[Mesh] Peer ${peerId} not found`);
            return false;
        }

        const channel = peerInfo.pc.createDataChannel(name, { ordered: true });
        this.setupDataChannel(peerId, channel);
        console.log(`[Mesh] Data channel created for ${peerId}`);
        return true;
    },

    setupDataChannel(peerId, channel) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) return;

        peerInfo.dataChannel = channel;
        channel.binaryType = 'arraybuffer';

        channel.onopen = async () => {
            console.log(`[Mesh] Data channel opened for ${peerId}`);
            peerInfo.isDataChannelOpen = true;
            await this.dotNetRef.invokeMethodAsync('OnMeshDataChannelOpenCallback', peerId);
        };

        channel.onclose = async () => {
            console.log(`[Mesh] Data channel closed for ${peerId}`);
            peerInfo.isDataChannelOpen = false;
            await this.dotNetRef.invokeMethodAsync('OnMeshDataChannelCloseCallback', peerId);
        };

        channel.onmessage = async (event) => {
            if (typeof event.data === 'string') {
                await this.dotNetRef.invokeMethodAsync('OnMeshDataChannelMessageCallback', peerId, event.data);
            } else if (event.data instanceof ArrayBuffer) {
                const uint8Array = new Uint8Array(event.data);
                await this.dotNetRef.invokeMethodAsync('OnMeshDataChannelBinaryMessageCallback', peerId, Array.from(uint8Array));
            }
        };
    },

    setupRemoteVideo(peerId, stream) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) return;

        console.log(`[Mesh] Setting up remote video for ${peerId}`);

        const video = document.createElement('video');
        video.srcObject = stream;
        video.autoplay = true;
        video.muted = true;
        video.playsInline = true;
        peerInfo.remoteVideo = video;

        video.onloadedmetadata = () => {
            const width = video.videoWidth;
            const height = video.videoHeight;
            console.log(`[Mesh] Video metadata for ${peerId}: ${width}x${height}`);

            // Notify C# that video is ready
            this.dotNetRef.invokeMethodAsync('OnMeshVideoReadyCallback', peerId, width, height);
        };

        video.play().catch(err => {
            console.warn(`[Mesh] Video play error for ${peerId}:`, err);
            video.muted = true;
            video.play();
        });
    },

    // Render a peer's video to a specific canvas (called by Blazor render loop)
    renderPeerToCanvas(peerId, canvasId) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo?.remoteVideo) return false;

        let canvas = document.getElementById(canvasId);
        if (!canvas) return false;

        const video = peerInfo.remoteVideo;
        if (video.readyState < 2) return false;

        // Update canvas size if needed
        if (canvas.width !== video.videoWidth || canvas.height !== video.videoHeight) {
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
        }

        const ctx = canvas.getContext('2d');
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
        return true;
    },

    // Create SDP offer for a peer
    async createOffer(peerId) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) {
            console.error(`[Mesh] Peer ${peerId} not found`);
            return null;
        }

        const offer = await peerInfo.pc.createOffer({
            offerToReceiveVideo: true,
            offerToReceiveAudio: false
        });

        offer.sdp = this.modifySdpForLowLatency(offer.sdp);
        await peerInfo.pc.setLocalDescription(offer);
        console.log(`[Mesh] Offer created for ${peerId}`);
        return JSON.stringify(offer);
    },

    // Create SDP answer for a peer
    async createAnswer(peerId) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) {
            console.error(`[Mesh] Peer ${peerId} not found`);
            return null;
        }

        const answer = await peerInfo.pc.createAnswer();
        answer.sdp = this.modifySdpForLowLatency(answer.sdp);
        await peerInfo.pc.setLocalDescription(answer);
        console.log(`[Mesh] Answer created for ${peerId}`);
        return JSON.stringify(answer);
    },

    // Set remote description for a peer
    async setRemoteDescription(peerId, sdpJson) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) {
            console.error(`[Mesh] Peer ${peerId} not found`);
            return false;
        }

        const sdp = JSON.parse(sdpJson);
        await peerInfo.pc.setRemoteDescription(new RTCSessionDescription(sdp));
        console.log(`[Mesh] Remote description set for ${peerId}`);
        return true;
    },

    // Add ICE candidate for a peer
    async addIceCandidate(peerId, candidateJson) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) {
            console.error(`[Mesh] Peer ${peerId} not found`);
            return false;
        }

        const candidate = JSON.parse(candidateJson);
        await peerInfo.pc.addIceCandidate(new RTCIceCandidate(candidate));
        console.log(`[Mesh] ICE candidate added for ${peerId}`);
        return true;
    },

    // Start screen capture and add to all existing peers
    async startScreenCapture() {
        console.log('[Mesh] Starting screen capture...');

        try {
            this.localStream = await navigator.mediaDevices.getDisplayMedia({
                video: {
                    cursor: 'always',
                    width: { ideal: 1280, max: 1920 },
                    height: { ideal: 720, max: 1080 },
                    frameRate: { ideal: 24, max: 30 }
                },
                audio: false,
                preferCurrentTab: false,
                selfBrowserSurface: 'exclude'
            });

            const videoTrack = this.localStream.getVideoTracks()[0];
            if (!videoTrack) {
                console.error('[Mesh] No video track in screen capture');
                return false;
            }

            videoTrack.contentHint = 'detail';

            // Handle user stopping share
            videoTrack.onended = async () => {
                console.log('[Mesh] Screen share stopped by user');
                await this.dotNetRef.invokeMethodAsync('OnScreenShareEndedCallback');
            };

            // Add track to all existing peer connections
            for (const [peerId, peerInfo] of this.peers) {
                const sender = peerInfo.pc.addTrack(videoTrack, this.localStream);

                // Configure encoding
                const params = sender.getParameters();
                if (!params.encodings) params.encodings = [{}];
                params.encodings[0].maxBitrate = 3000000;
                params.encodings[0].maxFramerate = 24;
                sender.setParameters(params).catch(e => console.warn(`[Mesh] Encoding params error for ${peerId}:`, e));

                console.log(`[Mesh] Screen share track added to ${peerId}`);
            }

            console.log(`[Mesh] Screen capture started, sharing to ${this.peers.size} peers`);
            return true;
        } catch (err) {
            console.error('[Mesh] Screen capture failed:', err);
            return false;
        }
    },

    // Stop screen capture
    stopScreenCapture() {
        if (this.localStream) {
            this.localStream.getTracks().forEach(track => track.stop());
            this.localStream = null;
            console.log('[Mesh] Screen capture stopped');
        }
    },

    // Send data to a specific peer
    sendData(peerId, data) {
        const peerInfo = this.peers.get(peerId);
        if (peerInfo?.dataChannel?.readyState === 'open') {
            peerInfo.dataChannel.send(data);
            return true;
        }
        return false;
    },

    // Broadcast data to all connected peers
    broadcastData(data) {
        let sent = 0;
        for (const [peerId, peerInfo] of this.peers) {
            if (peerInfo.dataChannel?.readyState === 'open') {
                peerInfo.dataChannel.send(data);
                sent++;
            }
        }
        return sent;
    },

    // Send binary data to a specific peer
    sendBinaryData(peerId, data) {
        const peerInfo = this.peers.get(peerId);
        if (peerInfo?.dataChannel?.readyState === 'open') {
            const uint8Array = new Uint8Array(data);
            peerInfo.dataChannel.send(uint8Array.buffer);
            return true;
        }
        return false;
    },

    // Close a specific peer connection
    async closePeer(peerId) {
        const peerInfo = this.peers.get(peerId);
        if (!peerInfo) return;

        if (peerInfo.dataChannel) {
            peerInfo.dataChannel.close();
        }
        if (peerInfo.remoteVideo) {
            peerInfo.remoteVideo.pause();
            peerInfo.remoteVideo.srcObject = null;
        }
        peerInfo.pc.close();

        this.peers.delete(peerId);
        console.log(`[Mesh] Peer ${peerId} closed`);
    },

    // Close all connections
    closeAll() {
        this.stopScreenCapture();
        for (const peerId of this.peers.keys()) {
            this.closePeer(peerId);
        }
        this.dotNetRef = null;
        console.log('[Mesh] All connections closed');
    },

    // Get list of connected peer IDs
    getConnectedPeerIds() {
        const connected = [];
        for (const [peerId, peerInfo] of this.peers) {
            if (peerInfo.pc.connectionState === 'connected') {
                connected.push(peerId);
            }
        }
        return connected;
    },

    // Modify SDP for lower latency
    modifySdpForLowLatency(sdp) {
        let modified = sdp;

        // Add bandwidth limit
        if (!modified.includes('b=AS:')) {
            modified = modified.replace(/m=video.*\r\n/g, '$&b=AS:3000\r\n');
        }

        // Prefer H264
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

        return modified;
    }
};
