// 分屏播放器封装：管理多个 jessibuca 实例（一个实例对应一个分屏格子）
window.apiextPlayer = {
    _players: {},
    _errorAt: {},

    // 创建实例（containerId 为容器 div 的 id；dotNetRef 可选，用于把播放失败回传给 Blazor）
    // codec：线上流的实际视频编码（H264 / H265 / 空=未知），决定走硬解还是 WASM 软解
    create: async function (id, containerId, dotNetRef, codec) {
        await this.destroy(id);
        var container = document.getElementById(containerId);
        if (!container) {
            console.warn('[apiextPlayer] 容器不存在：' + containerId);
            return false;
        }

        var self = this;
        var notify = function (msg) {
            var key = String(id);
            var now = Date.now();
            // 同一格 10 秒内只提示一次，避免重连抖动刷屏
            if (self._errorAt[key] && now - self._errorAt[key] < 10000) return;
            self._errorAt[key] = now;
            if (dotNetRef) {
                try {
                    dotNetRef.invokeMethodAsync('OnPlayerError', key, String(msg || '播放失败'));
                    return;
                } catch (e) { /* 忽略互操作异常，降级到 console */ }
            }
            console.warn('[apiextPlayer] ' + containerId + ' 播放失败：' + msg);
        };

        var opt = {
            container: container,
            decoder: '/player/decoder.js',
            videoBuffer: 0.2,
            isResize: true,
            useMSE: true,
            useWCS: true,
            hasAudio: true,
            debug: false,
            loadingText: '加载中…',
            showBandwidth: true,
            // MSE/WebCodecs 播不了 H.265 时自动切 WASM 软解（兜底，正常路径下面已按编码预选）
            autoWasm: true,
            wasmDecodeErrorReplay: true,
            operateBtns: {
                fullscreen: true,
                screenshot: false,
                play: false,
                audio: true,
                recorder: false
            },
            forceNoOffscreen: true,
            isNotMute: false,
            onError: function (e) {
                notify((e && (e.message || e.msg)) || e || '播放失败');
            },
            onTimeout: function () {
                notify('播放超时（流不存在、未推流或地址已失效）');
            },
            onLoadingTimeout: function () {
                notify('加载超时（流不存在、未推流或地址已失效）');
            }
        };

        var c = String(codec || '').toUpperCase();
        if (c === 'H265') {
            // HEVC：浏览器 MSE/WebCodecs 基本不支持，直接走 WASM 软解；软解缓冲给大一点更稳
            opt.useMSE = false;
            opt.useWCS = false;
            opt.videoBuffer = 0.5;
        } else if (c === 'H264') {
            // AVC：优先硬解（多分屏时 CPU 占用远低于软解）
            opt.useMSE = true;
            opt.useWCS = true;
        }

        var player = new Jessibuca(opt);
        this._players[id] = player;
        return true;
    },

    play: function (id, url) {
        var player = this._players[id];
        if (!player) {
            console.warn('[apiextPlayer] 播放器未创建：' + id);
            return false;
        }
        player.play(url);
        return true;
    },

    destroy: async function (id) {
        var player = this._players[id];
        if (player) {
            delete this._players[id];
            try { await player.destroy(); } catch (e) { }
        }
    },

    destroyAll: async function () {
        var ids = Object.keys(this._players);
        for (var i = 0; i < ids.length; i++) {
            await this.destroy(ids[i]);
        }
    }
};
