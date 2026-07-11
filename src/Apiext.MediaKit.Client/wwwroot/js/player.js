// Lightweight stream helpers for Blazor Interactive Server / WASM.
// Prefer native HLS when available; otherwise open FLV / HLS in a new tab or video element.

window.MediaKitPlayer = {
    playInVideo: function (videoElement, url) {
        if (!videoElement || !url) return false;
        try {
            videoElement.pause();
            videoElement.removeAttribute('src');
            videoElement.load();
            videoElement.src = url;
            videoElement.muted = true;
            const p = videoElement.play();
            if (p && typeof p.catch === 'function') p.catch(() => { });
            return true;
        } catch (e) {
            console.warn('MediaKitPlayer.playInVideo failed', e);
            return false;
        }
    },
    stopVideo: function (videoElement) {
        if (!videoElement) return;
        try {
            videoElement.pause();
            videoElement.removeAttribute('src');
            videoElement.load();
        } catch (e) { }
    },
    openExternal: function (url) {
        if (!url) return;
        window.open(url, '_blank', 'noopener,noreferrer');
    }
};
