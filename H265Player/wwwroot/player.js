const sdkBaseUrl = "/h265web/";
const playbackWatchdogDefaults = {
    direct: {
        enabled: true,
        checkIntervalMs: 3000,
        stallAfterMs: 10000
    },
    hls: {
        enabled: true,
        checkIntervalMs: 5000,
        stallAfterMs: 15000
    }
};

function clampWatchdogSeconds(value, fallbackSeconds, minSeconds, maxSeconds) {
    const parsed = Number.parseInt(value, 10);
    if (Number.isNaN(parsed)) {
        return fallbackSeconds;
    }

    return Math.min(maxSeconds, Math.max(minSeconds, parsed));
}

function normalizeWatchdogOptions(kind, options) {
    const defaults = playbackWatchdogDefaults[kind] || playbackWatchdogDefaults.hls;
    const checkIntervalSeconds = clampWatchdogSeconds(options?.checkIntervalSeconds, defaults.checkIntervalMs / 1000, 1, 30);
    const stallSeconds = Math.max(
        clampWatchdogSeconds(options?.stallSeconds, defaults.stallAfterMs / 1000, 2, 120),
        checkIntervalSeconds + 1
    );

    return {
        enabled: typeof options?.enabled === "boolean" ? options.enabled : defaults.enabled,
        checkIntervalMs: checkIntervalSeconds * 1000,
        stallAfterMs: stallSeconds * 1000
    };
}

function buildManagedPlayer(hostId, onStatus, onLog, url, autoPlay, ignoreAudio) {
    const player = H265webjsPlayer();
    player.on_ready_show_done_callback = function () {
        onStatus?.("First frame rendered");
        onLog?.("First frame rendered");
    };
    player.video_probe_callback = function (mediaInfo) {
        onLog?.(`Probe codec=${mediaInfo.codec} fmt=${mediaInfo.fmt || "-"} fps=${mediaInfo.fps}`);
    };
    player.on_load_caching_callback = function () {
        onStatus?.("Buffering");
        onLog?.("Buffering");
    };
    player.on_play_finished = function () {
        onStatus?.("Playback finished");
        onLog?.("Playback finished");
    };
    player.on_error = function (error) {
        onStatus?.("Decoder error");
        onLog?.(`Decoder error ${JSON.stringify(error)}`);
    };

    player.build({
        player_id: hostId,
        base_url: sdkBaseUrl,
        wasm_js_uri: "h265web_wasm.js",
        wasm_wasm_uri: "h265web_wasm.wasm",
        ext_src_js_uri: "extjs.js",
        ext_wasm_js_uri: "extwasm.js",
        width: "100%",
        height: 520,
        color: "#000000",
        auto_play: autoPlay,
        ignore_audio: ignoreAudio,
        readframe_multi_times: -1
    });

    player.load_media(url);
    return player;
}

function unmuteManagedHost(hostId) {
    const host = document.getElementById(hostId);
    if (!host) {
        return;
    }

    const video = host.querySelector("video");
    if (!video) {
        return;
    }

    video.muted = false;
    video.defaultMuted = false;
    video.volume = 1;
    video.removeAttribute("muted");
}

function clampBufferingLevel(level) {
    const parsed = Number.parseInt(level, 10);
    if (Number.isNaN(parsed)) {
        return 5;
    }

    return Math.min(5, Math.max(1, parsed));
}

function getBufferingProfile(level) {
    const value = clampBufferingLevel(level);
    const labels = ["Min", "Low", "Med", "High", "Max"];
    const mpegtsProfiles = [
        { enableWorker: false, enableStashBuffer: true, liveBufferLatencyChasing: true, liveBufferLatencyMaxLatency: 1.5, liveBufferLatencyMinRemain: 0.3, stashInitialSize: 128 },
        { enableWorker: false, enableStashBuffer: true, liveBufferLatencyChasing: true, liveBufferLatencyMaxLatency: 2.2, liveBufferLatencyMinRemain: 0.55, stashInitialSize: 256 },
        { enableWorker: false, enableStashBuffer: true, liveBufferLatencyChasing: true, liveBufferLatencyMaxLatency: 3.1, liveBufferLatencyMinRemain: 0.9, stashInitialSize: 384 },
        { enableWorker: false, enableStashBuffer: true, liveBufferLatencyChasing: true, liveBufferLatencyMaxLatency: 4.2, liveBufferLatencyMinRemain: 1.25, stashInitialSize: 768 },
        { enableWorker: false, enableStashBuffer: true, liveBufferLatencyChasing: false, liveBufferLatencyMaxLatency: 5.4, liveBufferLatencyMinRemain: 1.8, stashInitialSize: 1024 }
    ];
    const hlsProfiles = [
        { lowLatencyMode: true, backBufferLength: 15, maxBufferLength: 10, maxMaxBufferLength: 20, initialLiveManifestSize: 1, liveSyncDurationCount: 1, liveMaxLatencyDurationCount: 2 },
        { lowLatencyMode: true, backBufferLength: 20, maxBufferLength: 15, maxMaxBufferLength: 30, initialLiveManifestSize: 1, liveSyncDurationCount: 2, liveMaxLatencyDurationCount: 3 },
        { lowLatencyMode: true, backBufferLength: 25, maxBufferLength: 20, maxMaxBufferLength: 40, initialLiveManifestSize: 2, liveSyncDurationCount: 3, liveMaxLatencyDurationCount: 4 },
        { lowLatencyMode: false, backBufferLength: 30, maxBufferLength: 28, maxMaxBufferLength: 50, initialLiveManifestSize: 3, liveSyncDurationCount: 4, liveMaxLatencyDurationCount: 5 },
        { lowLatencyMode: false, backBufferLength: 40, maxBufferLength: 36, maxMaxBufferLength: 60, initialLiveManifestSize: 4, liveSyncDurationCount: 5, liveMaxLatencyDurationCount: 7 }
    ];

    return {
        value,
        label: labels[value - 1],
        mpegts: mpegtsProfiles[value - 1],
        hls: hlsProfiles[value - 1]
    };
}

window.h265App = {
    _directPlayers: {},
    _ffmpegPlayer: null,
    _rtspPlayer: null,
    _presetManagedPlayer: null,
    _hls: null,
    _hlsPlayers: {},
    _altManagedPlayer: null,
    _altTsPlayer: null,
    _altHls: null,
    _altVideoJs: null,
    _streamWatchdogs: {},
    _hlsState: {
        framework: "Idle",
        status: "Idle",
        lastError: ""
    },
    _recentKey(prefix) {
        return `h265player:${prefix}:recent`;
    },

    _miniRemoteKey(panelId) {
        return `h265player:${panelId}:position`;
    },

    getRecentStreams(prefix) {
        try {
            const raw = localStorage.getItem(this._recentKey(prefix));
            const parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed : [];
        } catch {
            return [];
        }
    },

    pushRecentStream(prefix, streamUrl) {
        const value = (streamUrl || "").trim();
        if (!value) {
            return this.getRecentStreams(prefix);
        }

        const existing = this.getRecentStreams(prefix).filter((item) => item !== value);
        const updated = [value, ...existing].slice(0, 10);
        localStorage.setItem(this._recentKey(prefix), JSON.stringify(updated));
        return updated;
    },

    getProxyUrl(streamUrl) {
        return `/proxy?url=${encodeURIComponent(streamUrl)}`;
    },

    getHlsProxyUrl(streamUrl) {
        return `/hls-proxy/playlist?url=${encodeURIComponent(streamUrl)}`;
    },

    _clearWatchdog(key) {
        const timer = this._streamWatchdogs[key];
        if (timer) {
            window.clearInterval(timer);
            delete this._streamWatchdogs[key];
        }
    },

    _getBufferedEnd(video) {
        try {
            return video.buffered && video.buffered.length > 0
                ? video.buffered.end(video.buffered.length - 1)
                : null;
        } catch {
            return null;
        }
    },

    _armVideoWatchdog(key, getVideo, onStall, options) {
        this._clearWatchdog(key);

        if (!options?.enabled) {
            return;
        }

        let lastTime = null;
        let lastBufferedEnd = null;
        let lastProgressAt = Date.now();

        this._streamWatchdogs[key] = window.setInterval(() => {
            const video = getVideo();
            if (!video) {
                return;
            }

            const now = Date.now();
            const currentTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
            const bufferedEnd = this._getBufferedEnd(video);

            if (video.paused || video.ended || video.seeking) {
                lastTime = currentTime;
                lastBufferedEnd = bufferedEnd;
                lastProgressAt = now;
                return;
            }

            const timeProgressed = lastTime === null ? false : currentTime > lastTime + 0.1;
            const bufferProgressed = lastBufferedEnd === null || bufferedEnd === null
                ? false
                : bufferedEnd > lastBufferedEnd + 0.25;

            if (lastTime === null || timeProgressed || bufferProgressed) {
                lastProgressAt = now;
            }

            lastTime = currentTime;
            lastBufferedEnd = bufferedEnd;

            const stalledForMs = now - lastProgressAt;
            if (stalledForMs < options.stallAfterMs) {
                return;
            }

            lastProgressAt = now;
            onStall(stalledForMs);
        }, options.checkIntervalMs);
    },

    directLoad(streamUrl, dotNetRef, bufferingLevel = 5, watchdogOptions = null) {
        return this.directLoadToElement("direct-player-host", streamUrl, dotNetRef, bufferingLevel, watchdogOptions);
    },

    directLoadToElement(elementId, streamUrl, dotNetRef, bufferingLevel = 5, watchdogOptions = null) {
        this.directReleaseElement(elementId);
        const proxiedUrl = this.getProxyUrl(streamUrl);
        const absoluteUrl = `${window.location.origin}${proxiedUrl}`;
        const video = document.getElementById(elementId);
        const callbacks = dotNetRef || null;
        const buffering = getBufferingProfile(bufferingLevel);
        const watchdog = normalizeWatchdogOptions("direct", watchdogOptions);
        if (!video) {
            return { proxyUrl: proxiedUrl, status: "Video element missing" };
        }

        if (!(window.mpegts && window.mpegts.getFeatureList()?.mseLivePlayback)) {
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "mpegts.js unsupported in this browser");
            callbacks?.invokeMethodAsync("OnDirectLog", "mpegts.js MSE live playback not available");
            return { proxyUrl: proxiedUrl, status: "Unsupported" };
        }

        callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Loading");
        callbacks?.invokeMethodAsync("OnDirectLog", `Opening ${absoluteUrl} with ${buffering.label} buffering`);

        const player = window.mpegts.createPlayer({
            type: "mpegts",
            isLive: true,
            url: absoluteUrl
        }, buffering.mpegts);

        player.attachMediaElement(video);
        player.load();
        player.play().catch((error) => {
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Playback failed");
            callbacks?.invokeMethodAsync("OnDirectLog", error?.message || "Unable to play stream");
        });

        player.on(window.mpegts.Events.MEDIA_INFO, (info) => {
            callbacks?.invokeMethodAsync("OnDirectLog", `Media info codec=${info.videoCodec || "-"} size=${info.width || 0}x${info.height || 0}`);
        });

        player.on(window.mpegts.Events.ERROR, (errorType, errorDetail, errorInfo) => {
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Decoder error");
            callbacks?.invokeMethodAsync("OnDirectLog", `mpegts.js error ${errorType} ${errorDetail} ${JSON.stringify(errorInfo || {})}`);
        });

        video.addEventListener("loadedmetadata", () => {
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Metadata loaded");
        }, { once: true });

        video.addEventListener("playing", () => {
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Playing");
        }, { once: true });

        this._directPlayers[elementId] = { player, callbacks };
        this._armVideoWatchdog(`direct:${elementId}`, () => document.getElementById(elementId), (stalledForMs) => {
            const entry = this._directPlayers[elementId];
            if (!entry || entry.restarting) {
                return;
            }

            entry.restarting = true;
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Watchdog restart");
            callbacks?.invokeMethodAsync("OnDirectLog", `No media progress for ${Math.round(stalledForMs / 1000)}s. Reopening proxied stream.`);
            this.directLoadToElement(elementId, streamUrl, dotNetRef, bufferingLevel, watchdogOptions);
        }, watchdog);
        return { proxyUrl: proxiedUrl, status: "Loading" };
    },

    directPlay() {
        this._directPlayer?.play();
    },

    directPause() {
        this._directPlayer?.pause();
    },

    directRelease() {
        this.directReleaseElement("direct-player-host");
    },

    directReleaseElement(elementId) {
        const entry = this._directPlayers[elementId];
        const video = document.getElementById(elementId);
        this._clearWatchdog(`direct:${elementId}`);
        entry?.player.pause?.();
        entry?.player.unload?.();
        entry?.player.detachMediaElement?.();
        entry?.player.destroy?.();
        delete this._directPlayers[elementId];
        if (video) {
            video.pause();
            video.removeAttribute("src");
            video.load();
        }
    },

    ffmpegLoad(manifestUrl, autoPlay, ignoreAudio) {
        this.ffmpegRelease();
        this._ffmpegPlayer = buildManagedPlayer("ffmpeg-player-host", null, null, manifestUrl, autoPlay, ignoreAudio);
        return { status: "Loading" };
    },

    managedLoadToHost(hostId, manifestUrl, autoPlay, ignoreAudio) {
        this.managedReleaseHost(hostId);
        this._presetManagedPlayer = buildManagedPlayer(hostId, null, null, manifestUrl, autoPlay, ignoreAudio);
        return { status: "Loading" };
    },

    ffmpegPlay() {
        this._ffmpegPlayer?.play();
    },

    ffmpegPause() {
        this._ffmpegPlayer?.pause();
    },

    ffmpegRelease() {
        if (!this._ffmpegPlayer) {
            return;
        }

        this._ffmpegPlayer.release();
        this._ffmpegPlayer = null;
        const host = document.getElementById("ffmpeg-player-host");
        if (host) {
            host.innerHTML = "";
        }
    },

    managedReleaseHost(hostId) {
        if (!this._presetManagedPlayer) {
            const host = document.getElementById(hostId);
            if (host) {
                host.innerHTML = "";
            }
            return;
        }

        this._presetManagedPlayer.release();
        this._presetManagedPlayer = null;
        const host = document.getElementById(hostId);
        if (host) {
            host.innerHTML = "";
        }
    },

    rtspLoad(manifestUrl, autoPlay, ignoreAudio) {
        this.rtspRelease();
        this._rtspPlayer = buildManagedPlayer("rtsp-player-host", null, null, manifestUrl, autoPlay, ignoreAudio);
        unmuteManagedHost("rtsp-player-host");
        window.setTimeout(() => unmuteManagedHost("rtsp-player-host"), 150);
        return { status: "Loading" };
    },

    rtspRelease() {
        if (!this._rtspPlayer) {
            return;
        }

        this._rtspPlayer.release();
        this._rtspPlayer = null;
        const host = document.getElementById("rtsp-player-host");
        if (host) {
            host.innerHTML = "";
        }
    },

    _showElement(id, visible) {
        const element = document.getElementById(id);
        if (element) {
            element.style.display = visible ? "" : "none";
        }
    },

    altRelease() {
        if (this._altManagedPlayer) {
            this._altManagedPlayer.release();
            this._altManagedPlayer = null;
        }

        if (this._altTsPlayer) {
            const video = document.getElementById("alt-ts-video");
            this._altTsPlayer.pause?.();
            this._altTsPlayer.unload?.();
            this._altTsPlayer.detachMediaElement?.();
            this._altTsPlayer.destroy?.();
            this._altTsPlayer = null;
            if (video) {
                video.pause();
                video.removeAttribute("src");
                video.load();
            }
        }

        if (this._altHls) {
            this._altHls.destroy();
            this._altHls = null;
        }

        if (this._altVideoJs) {
            this._altVideoJs.dispose();
            this._altVideoJs = null;
        }

        const hlsVideo = document.getElementById("alt-hls-video");
        if (hlsVideo) {
            hlsVideo.pause();
            hlsVideo.removeAttribute("src");
            hlsVideo.load();
        }

        const managedHost = document.getElementById("alt-managed-host");
        if (managedHost) {
            managedHost.innerHTML = "";
        }

        this._showElement("alt-managed-host", false);
        this._showElement("alt-ts-video", false);
        this._showElement("alt-hls-video", false);
    },

    altTsLoad(streamUrl, engine) {
        this.altRelease();
        const proxiedUrl = this.getProxyUrl(streamUrl);
        const absoluteUrl = `${window.location.origin}${proxiedUrl}`;

        if (engine === "h265web") {
            this._showElement("alt-managed-host", true);
            this._altManagedPlayer = buildManagedPlayer("alt-managed-host", null, null, proxiedUrl, true, false);
            return {
                resolvedUrl: proxiedUrl,
                engine: "h265web.js",
                status: "Loading",
                log: `Opening ${proxiedUrl}`
            };
        }

        const video = document.getElementById("alt-ts-video");
        if (!video) {
            return {
                resolvedUrl: proxiedUrl,
                engine: "mpegts.js",
                status: "Video element missing",
                log: "TS video host not found."
            };
        }

        if (!(window.mpegts && window.mpegts.getFeatureList()?.mseLivePlayback)) {
            return {
                resolvedUrl: proxiedUrl,
                engine: "mpegts.js",
                status: "Unsupported",
                log: "mpegts.js MSE live playback not available in this browser."
            };
        }

        this._showElement("alt-ts-video", true);
        const player = window.mpegts.createPlayer({
            type: "mpegts",
            isLive: true,
            url: absoluteUrl
        }, {
            enableWorker: false,
            liveBufferLatencyChasing: true,
            liveBufferLatencyMaxLatency: 2,
            liveBufferLatencyMinRemain: 0.5,
            stashInitialSize: 128
        });

        player.attachMediaElement(video);
        player.load();
        player.play().catch(() => null);
        this._altTsPlayer = player;

        return {
            resolvedUrl: proxiedUrl,
            engine: "mpegts.js",
            status: "Loading",
            log: `Opening ${absoluteUrl}`
        };
    },

    altHlsLoad(streamUrl, engine, useProxy) {
        this.altRelease();
        const resolvedUrl = useProxy ? this.getHlsProxyUrl(streamUrl) : streamUrl;
        const video = document.getElementById("alt-hls-video");
        if (!video) {
            return {
                resolvedUrl,
                engine,
                status: "Video element missing",
                log: "HLS video host not found."
            };
        }

        this._showElement("alt-hls-video", true);

        if (engine === "videojs") {
            if (!window.videojs) {
                return {
                    resolvedUrl,
                    engine: "video.js",
                    status: "Unavailable",
                    log: "video.js not loaded."
                };
            }

            const player = window.videojs(video, {
                autoplay: false,
                controls: true,
                preload: "auto",
                liveui: true,
                html5: {
                    vhs: {
                        overrideNative: true
                    }
                }
            });
            player.src({ src: resolvedUrl, type: "application/x-mpegURL" });
            player.ready(() => {
                player.play().catch(() => null);
            });
            this._altVideoJs = player;

            return {
                resolvedUrl,
                engine: "video.js",
                status: "Loading",
                log: `Opening ${resolvedUrl}`
            };
        }

        if (video.canPlayType("application/vnd.apple.mpegurl")) {
            video.src = resolvedUrl;
            video.play().catch(() => null);
            return {
                resolvedUrl,
                engine: "hls.js/native",
                status: "Loaded",
                log: `Opening ${resolvedUrl}`
            };
        }

        if (!(window.Hls && window.Hls.isSupported())) {
            return {
                resolvedUrl,
                engine: "hls.js",
                status: "Unsupported",
                log: "hls.js is not supported in this browser."
            };
        }

        const hls = new window.Hls({
            lowLatencyMode: true,
            backBufferLength: 30
        });
        hls.loadSource(resolvedUrl);
        hls.attachMedia(video);
        hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
            video.play().catch(() => null);
        });
        this._altHls = hls;

        return {
            resolvedUrl,
            engine: "hls.js",
            status: "Loading",
            log: `Opening ${resolvedUrl}`
        };
    },

    hlsLoad(streamUrl, useProxy, bufferingLevel = 5, watchdogOptions = null) {
        return this.hlsLoadToElement("hls-video", streamUrl, useProxy, bufferingLevel, watchdogOptions);
    },

    hlsLoadToElement(elementId, streamUrl, useProxy, bufferingLevel = 5, watchdogOptions = null) {
        this.hlsRelease();
        const resolvedUrl = useProxy ? this.getHlsProxyUrl(streamUrl) : streamUrl;
        const video = document.getElementById(elementId);
        const buffering = getBufferingProfile(bufferingLevel);
        const watchdog = normalizeWatchdogOptions("hls", watchdogOptions);
        if (!video) {
            return { resolvedUrl, framework: "Unavailable", status: "Video element missing" };
        }

        if (video.canPlayType("application/vnd.apple.mpegurl")) {
            video.src = resolvedUrl;
            video.play().catch(() => null);
            this._hlsState = { framework: "Native HLS", status: "Loaded", lastError: "" };
            this._hlsPlayers[elementId] = { kind: "native" };
            this._armVideoWatchdog(`hls:${elementId}`, () => document.getElementById(elementId), (stalledForMs) => {
                const entry = this._hlsPlayers[elementId];
                if (!entry || entry.restarting) {
                    return;
                }

                entry.restarting = true;
                this._hlsState = {
                    framework: "Native HLS",
                    status: "Watchdog restart",
                    lastError: `No media progress for ${Math.round(stalledForMs / 1000)}s. Reloading stream.`
                };
                this.hlsLoadToElement(elementId, streamUrl, useProxy, bufferingLevel, watchdogOptions);
            }, watchdog);
            return { resolvedUrl, framework: "Native HLS", status: "Loaded" };
        }

        if (window.Hls && window.Hls.isSupported()) {
            const hls = new window.Hls(buffering.hls);
            hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                this._hlsState.status = "Manifest parsed";
                this._hlsState.lastError = "";
            });
            hls.on(window.Hls.Events.ERROR, (_, data) => {
                this._hlsState.status = `hls.js error (${data.type})`;
                this._hlsState.lastError = data?.details || data?.type || "Unknown HLS error";
            });
            hls.loadSource(resolvedUrl);
            hls.attachMedia(video);
            hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                video.play().catch(() => null);
            });
            this._hls = hls;
            this._hlsPlayers[elementId] = { kind: "hls", player: hls };
            this._hlsState = { framework: "hls.js", status: "Loading", lastError: "" };
            this._armVideoWatchdog(`hls:${elementId}`, () => document.getElementById(elementId), (stalledForMs) => {
                const entry = this._hlsPlayers[elementId];
                if (!entry || entry.restarting) {
                    return;
                }

                entry.restarting = true;
                this._hlsState = {
                    framework: "hls.js",
                    status: "Watchdog restart",
                    lastError: `No media progress for ${Math.round(stalledForMs / 1000)}s. Reloading stream.`
                };
                this.hlsLoadToElement(elementId, streamUrl, useProxy, bufferingLevel, watchdogOptions);
            }, watchdog);
            return { resolvedUrl, framework: "hls.js", status: "Loading" };
        }

        this._hlsState = { framework: "Unavailable", status: "Browser cannot play HLS here", lastError: "Unsupported HLS runtime" };
        return { resolvedUrl, framework: "Unavailable", status: "Browser cannot play HLS here" };
    },

    async hlsPlay() {
        const video = document.getElementById("hls-video");
        if (video) {
            try {
                await video.play();
                this._hlsState.status = "Playing";
                this._hlsState.lastError = "";
                return { ok: true, status: "Playing", error: "" };
            } catch (error) {
                const message = error?.message || "The element has no supported sources.";
                this._hlsState.status = "Playback failed";
                this._hlsState.lastError = message;
                return { ok: false, status: "Playback failed", error: message };
            }
        }

        return { ok: false, status: "Playback failed", error: "Video element missing" };
    },

    hlsPause() {
        const video = document.getElementById("hls-video");
        video?.pause();
        this._hlsState.status = "Paused";
        return { status: "Paused" };
    },

    hlsRelease() {
        this._clearWatchdog("hls:hls-video");
        if (this._hls) {
            this._hls.destroy();
            this._hls = null;
        }

        const video = document.getElementById("hls-video");
        if (video) {
            video.pause();
            video.removeAttribute("src");
            video.load();
        }

        this._hlsState = { framework: "Idle", status: "Released", lastError: "" };
        return { framework: "Idle", status: "Released", error: "" };
    },

    hlsReleaseElement(elementId) {
        const entry = this._hlsPlayers[elementId];
        this._clearWatchdog(`hls:${elementId}`);
        if (entry?.player) {
            entry.player.destroy();
        }

        delete this._hlsPlayers[elementId];
        const video = document.getElementById(elementId);
        if (video) {
            video.pause();
            video.removeAttribute("src");
            video.load();
        }
    },

    hlsGetState() {
        return this._hlsState;
    },

    async authLogin(payload) {
        const response = await fetch("/api/auth/login", {
            method: "POST",
            credentials: "include",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify(payload)
        });

        return {
            ok: response.ok,
            status: response.status,
            text: await response.text()
        };
    },

    async authLogout() {
        await fetch("/api/auth/logout", {
            method: "POST",
            credentials: "include"
        });
    },

    enableMiniRemote(panelId, handleId) {
        const panel = document.getElementById(panelId);
        const handle = document.getElementById(handleId);
        if (!panel || !handle) {
            return;
        }

        const clampPosition = (left, top) => {
            const margin = 16;
            const maxLeft = Math.max(margin, window.innerWidth - panel.offsetWidth - margin);
            const maxTop = Math.max(margin, window.innerHeight - panel.offsetHeight - margin);

            return {
                left: Math.min(Math.max(left, margin), maxLeft),
                top: Math.min(Math.max(top, margin), maxTop)
            };
        };

        const savePosition = (left, top) => {
            const clamped = clampPosition(left, top);
            localStorage.setItem(this._miniRemoteKey(panelId), JSON.stringify(clamped));
            panel.style.left = `${clamped.left}px`;
            panel.style.top = `${clamped.top}px`;
        };

        const loadPosition = () => {
            try {
                const raw = localStorage.getItem(this._miniRemoteKey(panelId));
                if (!raw) {
                    return { left: 24, top: 24 };
                }

                const parsed = JSON.parse(raw);
                if (typeof parsed?.left !== "number" || typeof parsed?.top !== "number") {
                    return { left: 24, top: 24 };
                }

                return clampPosition(parsed.left, parsed.top);
            } catch {
                return { left: 24, top: 24 };
            }
        };

        if (panel.dataset.dragBound === "1") {
            const saved = loadPosition();
            panel.style.left = `${saved.left}px`;
            panel.style.top = `${saved.top}px`;
            panel.focus();
            return;
        }

        panel.dataset.dragBound = "1";
        const saved = loadPosition();
        panel.style.left = `${saved.left}px`;
        panel.style.top = `${saved.top}px`;
        panel.focus();

        let startX = 0;
        let startY = 0;
        let originLeft = 0;
        let originTop = 0;
        let dragging = false;
        let lastX = 0;
        let lastY = 0;

        const move = (event) => {
            if (!dragging) {
                return;
            }

            lastX = event.clientX;
            lastY = event.clientY;
            const dx = event.clientX - startX;
            const dy = event.clientY - startY;
            panel.style.left = `${originLeft + dx}px`;
            panel.style.top = `${originTop + dy}px`;
        };

        const up = () => {
            if (!dragging) {
                return;
            }

            dragging = false;
            savePosition(originLeft + (lastX - startX), originTop + (lastY - startY));
            document.removeEventListener("pointermove", move);
            document.removeEventListener("pointerup", up);
        };

        handle.addEventListener("pointerdown", (event) => {
            dragging = true;
            startX = event.clientX;
            startY = event.clientY;
            lastX = event.clientX;
            lastY = event.clientY;
            originLeft = parseInt(panel.style.left || "24", 10);
            originTop = parseInt(panel.style.top || "24", 10);
            document.addEventListener("pointermove", move);
            document.addEventListener("pointerup", up);
        });

        window.addEventListener("resize", () => {
            const currentLeft = parseInt(panel.style.left || "24", 10);
            const currentTop = parseInt(panel.style.top || "24", 10);
            savePosition(currentLeft, currentTop);
        });
    }
};

window.h265Auth = {
    async fetchJson(url, options) {
        const response = await fetch(url, {
            credentials: "include",
            ...options,
            headers: {
                "Content-Type": "application/json",
                ...(options?.headers || {})
            }
        });

        const text = await response.text();
        let json = null;
        if (text) {
            try {
                json = JSON.parse(text);
            } catch {
            }
        }

        return {
            ok: response.ok,
            status: response.status,
            text,
            json
        };
    },

    async login(payload) {
        return this.fetchJson("/api/auth/login", {
            method: "POST",
            body: JSON.stringify(payload)
        });
    },

    async resetSession() {
        return this.fetchJson("/api/auth/reset", {
            method: "POST"
        });
    },

    async resetSessionAndGetStatus() {
        await this.resetSession();
        return this.fetchJson("/api/auth/status");
    },

    async logout() {
        await fetch("/api/auth/logout", {
            method: "POST",
            credentials: "include"
        });
    },

    async copyQrImage(dataUrl) {
        try {
            if (!navigator.clipboard || typeof ClipboardItem === "undefined") {
                return false;
            }

            const response = await fetch(dataUrl);
            const blob = await response.blob();
            await navigator.clipboard.write([
                new ClipboardItem({
                    [blob.type || "image/png"]: blob
                })
            ]);
            return true;
        } catch {
            return false;
        }
    }
};
