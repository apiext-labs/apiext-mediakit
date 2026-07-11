// Browser WHIP publish helper for Apiext.MediaKit.
window.MediaKitWhip = {
    _pc: null,
    _stream: null,
    _location: null,

    async listDevices() {
        try {
            // labels only available after permission
            const tmp = await navigator.mediaDevices.getUserMedia({ audio: true, video: true });
            tmp.getTracks().forEach(t => t.stop());
        } catch (_) { /* ignore; user may deny */ }

        const devices = await navigator.mediaDevices.enumerateDevices();
        return devices.map(d => ({
            deviceId: d.deviceId,
            kind: d.kind,
            label: d.label || (d.kind === 'videoinput' ? '摄像头' : d.kind === 'audioinput' ? '麦克风' : d.kind)
        }));
    },

    async start(videoEl, whipUrl, videoDeviceId, audioDeviceId) {
        await this.stop();
        if (!whipUrl) throw new Error('WHIP URL 为空');

        const constraints = {
            video: videoDeviceId ? { deviceId: { exact: videoDeviceId } } : true,
            audio: audioDeviceId ? { deviceId: { exact: audioDeviceId } } : true
        };
        this._stream = await navigator.mediaDevices.getUserMedia(constraints);
        if (videoEl) {
            videoEl.srcObject = this._stream;
            videoEl.muted = true;
            try { await videoEl.play(); } catch (_) { }
        }

        this._pc = new RTCPeerConnection({
            iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
        });
        this._stream.getTracks().forEach(track => this._pc.addTrack(track, this._stream));

        const offer = await this._pc.createOffer();
        await this._pc.setLocalDescription(offer);

        // Wait briefly for ICE gathering when non-trickle WHIP is expected.
        if (this._pc.iceGatheringState !== 'complete') {
            await new Promise(resolve => {
                const check = () => {
                    if (this._pc.iceGatheringState === 'complete') {
                        this._pc.removeEventListener('icegatheringstatechange', check);
                        resolve();
                    }
                };
                this._pc.addEventListener('icegatheringstatechange', check);
                setTimeout(resolve, 2000);
            });
        }

        const sdp = this._pc.localDescription?.sdp || offer.sdp;
        const response = await fetch(whipUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/sdp' },
            body: sdp
        });
        if (!response.ok) {
            const text = await response.text();
            throw new Error(`WHIP 失败 HTTP ${response.status}: ${text || response.statusText}`);
        }
        const answer = await response.text();
        this._location = response.headers.get('Location');
        await this._pc.setRemoteDescription({ type: 'answer', sdp: answer });
        return { location: this._location || '' };
    },

    async stop() {
        try {
            if (this._location) {
                await fetch(this._location, { method: 'DELETE' }).catch(() => { });
            }
        } finally {
            this._location = null;
            if (this._pc) {
                try { this._pc.close(); } catch (_) { }
                this._pc = null;
            }
            if (this._stream) {
                this._stream.getTracks().forEach(t => t.stop());
                this._stream = null;
            }
        }
    },

    bindPreview(videoEl) {
        if (videoEl && this._stream) {
            videoEl.srcObject = this._stream;
        }
    }
};
