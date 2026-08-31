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

    const hostSize = getManagedHostSize(hostId);
    player.build({
        player_id: hostId,
        base_url: sdkBaseUrl,
        wasm_js_uri: "h265web_wasm.js",
        wasm_wasm_uri: "h265web_wasm.wasm",
        ext_src_js_uri: "extjs.js",
        ext_wasm_js_uri: "extwasm.js",
        width: "100%",
        height: hostSize?.height || 280,
        color: "#000000",
        auto_play: autoPlay,
        ignore_audio: ignoreAudio,
        readframe_multi_times: -1
    });

    player.load_media(url);
    attachManagedControls(hostId, player);
    observeManagedHostSize(hostId, player);
    return player;
}

function getManagedHostSize(hostId) {
    const host = document.getElementById(hostId);
    if (!host) {
        return null;
    }

    const rect = host.getBoundingClientRect();
    const width = Math.max(1, Math.round(rect.width || host.clientWidth || 0));
    const height = Math.max(1, Math.round(rect.height || host.clientHeight || 520));
    return { host, width, height };
}

function resizeManagedPlayer(hostId, player) {
    if (!player || typeof player.resize !== "function") {
        return;
    }

    const size = getManagedHostSize(hostId);
    if (!size) {
        return;
    }

    player.resize(size.width, size.height);
}

function observeManagedHostSize(hostId, player) {
    const size = getManagedHostSize(hostId);
    if (!size || typeof ResizeObserver === "undefined") {
        window.requestAnimationFrame(() => resizeManagedPlayer(hostId, player));
        return;
    }

    player.__hostResizeObserver?.disconnect?.();
    player.__hostResizeObserver = new ResizeObserver(() => {
        window.requestAnimationFrame(() => resizeManagedPlayer(hostId, player));
    });
    player.__hostResizeObserver.observe(size.host);
    window.requestAnimationFrame(() => resizeManagedPlayer(hostId, player));
}

function attachManagedControls(hostId, player) {
    const host = document.getElementById(hostId);
    if (!host || host.querySelector(".managed-player-controls")) {
        return;
    }

    host.classList.add("managed-player-host");

    const controls = document.createElement("div");
    controls.className = "managed-player-controls";

    const makeButton = (label, title, action) => {
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = label;
        button.title = title;
        button.addEventListener("click", (event) => {
            event.stopPropagation();
            action(button);
        });
        return button;
    };

    let muted = false;
    controls.append(
        makeButton("Play", "Play", () => player.play?.()),
        makeButton("Pause", "Pause", () => player.pause?.()),
        makeButton("Mute", "Mute", (button) => {
            muted = !muted;
            player.set_voice?.(muted ? 0 : 1);
            button.textContent = muted ? "Sound" : "Mute";
            button.title = muted ? "Restore sound" : "Mute";
        }),
        makeButton("Full", "Fullscreen", () => player.fullScreen?.())
    );

    host.appendChild(controls);
}

function releaseManagedPlayer(player, hostId) {
    player?.__hostResizeObserver?.disconnect?.();
    player?.release?.();

    const host = document.getElementById(hostId);
    if (host) {
        host.innerHTML = "";
        host.classList.remove("managed-player-host");
    }
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
    const mpegtsLive = {
        enableWorker: false,
        enableStashBuffer: true,
        lazyLoad: false,
        deferLoadAfterSourceOpen: false,
        autoCleanupSourceBuffer: true,
        fixAudioTimestampGap: true,
        liveBufferLatencyChasing: true
    };
    const mpegtsProfiles = [
        { ...mpegtsLive, liveBufferLatencyMaxLatency: 1.5, liveBufferLatencyMinRemain: 0.3, stashInitialSize: 128 },
        { ...mpegtsLive, liveBufferLatencyMaxLatency: 2.2, liveBufferLatencyMinRemain: 0.55, stashInitialSize: 256 },
        { ...mpegtsLive, liveBufferLatencyMaxLatency: 3.1, liveBufferLatencyMinRemain: 0.9, stashInitialSize: 384 },
        { ...mpegtsLive, liveBufferLatencyMaxLatency: 4.2, liveBufferLatencyMinRemain: 1.25, stashInitialSize: 768 },
        { ...mpegtsLive, liveBufferLatencyChasing: false, liveBufferLatencyMaxLatency: 5.4, liveBufferLatencyMinRemain: 1.8, stashInitialSize: 1024 }
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

function createLivePlaybackController(video, options) {
    const onStatus = options?.onStatus;
    const onLog = options?.onLog;
    const playMedia = typeof options?.play === "function"
        ? options.play
        : () => video.play();
    const retryDelays = options?.retryDelaysMs || [0, 120, 350, 800, 1600, 2800, 4500];
    let stopped = false;
    let started = false;
    const timers = [];
    let unmuteHook = null;

    const onCanPlay = () => tryPlay("canplay");
    const onLoadedData = () => tryPlay("loadeddata");
    const onPlaying = () => markStarted();

    function prepare() {
        video.playsInline = true;
        video.autoplay = true;
        video.setAttribute("playsinline", "");
        video.setAttribute("webkit-playsinline", "");
        video.muted = true;
        video.defaultMuted = true;
        video.setAttribute("muted", "");
    }

    function clearStartRetries() {
        timers.splice(0).forEach((timer) => window.clearTimeout(timer));
        video.removeEventListener("canplay", onCanPlay);
        video.removeEventListener("loadeddata", onLoadedData);
    }

    function dismissNativeSpinner() {
        if (stopped || !video.controls) {
            return;
        }

        video.controls = false;
        window.requestAnimationFrame(() => {
            if (!stopped) {
                video.controls = true;
            }
        });
    }

    function markStarted() {
        if (stopped || started) {
            return;
        }

        started = true;
        onStatus?.("Playing");
        clearStartRetries();
        dismissNativeSpinner();
        armUnmuteOnGesture();
    }

    function armUnmuteOnGesture() {
        if (stopped || unmuteHook || !video.muted) {
            return;
        }

        onLog?.("Playing muted; click once for sound.");
        unmuteHook = () => {
            if (stopped) {
                return;
            }

            video.muted = false;
            video.defaultMuted = false;
            video.removeAttribute("muted");
            video.play().catch(() => null);
        };

        window.addEventListener("pointerdown", unmuteHook, { once: true, capture: true });
        window.addEventListener("keydown", unmuteHook, { once: true, capture: true });
    }

    async function tryPlay(reason) {
        if (stopped || !video || video.ended) {
            return false;
        }

        if (!video.paused) {
            markStarted();
            return true;
        }

        if (reason === "media-info" || reason === "manifest-parsed" || reason === "videojs-ready") {
            onLog?.(`Starting playback (${reason})`);
        }

        try {
            await playMedia();
            if (!video.paused) {
                markStarted();
                return true;
            }
        } catch (error) {
            onLog?.(error?.message || "Unable to play stream");
        }

        return !video.paused;
    }

    prepare();
    video.addEventListener("canplay", onCanPlay);
    video.addEventListener("loadeddata", onLoadedData);
    video.addEventListener("playing", onPlaying);
    retryDelays.forEach((delayMs) => {
        timers.push(window.setTimeout(() => tryPlay(`retry ${delayMs}ms`), delayMs));
    });

    return {
        tryPlay,
        stop() {
            stopped = true;
            clearStartRetries();
            video.removeEventListener("playing", onPlaying);
            if (unmuteHook) {
                window.removeEventListener("pointerdown", unmuteHook, true);
                window.removeEventListener("keydown", unmuteHook, true);
                unmuteHook = null;
            }
        }
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
    _altTsPlayback: null,
    _altHls: null,
    _altHlsPlayback: null,
    _altVideoJs: null,
    _streamWatchdogs: {},
    _watchVideo: {
        scale: 100,
        observer: null,
        resizeHandler: null
    },
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

    _armVideoWatchdog(key, getVideo, onStall, options, getPlayback) {
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

            if (video.ended || video.seeking) {
                lastTime = currentTime;
                lastBufferedEnd = bufferedEnd;
                lastProgressAt = now;
                return;
            }

            if (video.paused) {
                getPlayback?.()?.tryPlay?.("watchdog-paused");
                lastTime = currentTime;
                lastBufferedEnd = bufferedEnd;
                const stalledForMs = now - lastProgressAt;
                if (stalledForMs >= options.stallAfterMs) {
                    lastProgressAt = now;
                    onStall(stalledForMs);
                }
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
        const playback = createLivePlaybackController(video, {
            play: () => player.play(),
            onStatus: (status) => callbacks?.invokeMethodAsync("OnDirectStatusChanged", status),
            onLog: (message) => callbacks?.invokeMethodAsync("OnDirectLog", message)
        });
        player.on(window.mpegts.Events.MEDIA_INFO, (info) => {
            callbacks?.invokeMethodAsync("OnDirectLog", `Media info codec=${info.videoCodec || "-"} size=${info.width || 0}x${info.height || 0}`);
            playback.tryPlay("media-info");
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

        this._directPlayers[elementId] = { player, callbacks, playback };
        this._armVideoWatchdog(`direct:${elementId}`, () => document.getElementById(elementId), (stalledForMs) => {
            const entry = this._directPlayers[elementId];
            if (!entry || entry.restarting) {
                return;
            }

            entry.restarting = true;
            callbacks?.invokeMethodAsync("OnDirectStatusChanged", "Watchdog restart");
            callbacks?.invokeMethodAsync("OnDirectLog", `No media progress for ${Math.round(stalledForMs / 1000)}s. Reopening proxied stream.`);
            this.directLoadToElement(elementId, streamUrl, dotNetRef, bufferingLevel, watchdogOptions);
        }, watchdog, () => this._directPlayers[elementId]?.playback);
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
        entry?.playback?.stop();
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

        releaseManagedPlayer(this._ffmpegPlayer, "ffmpeg-player-host");
        this._ffmpegPlayer = null;
    },

    managedReleaseHost(hostId) {
        if (!this._presetManagedPlayer) {
            const host = document.getElementById(hostId);
            if (host) {
                host.innerHTML = "";
            }
            return;
        }

        releaseManagedPlayer(this._presetManagedPlayer, hostId);
        this._presetManagedPlayer = null;
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

        releaseManagedPlayer(this._rtspPlayer, "rtsp-player-host");
        this._rtspPlayer = null;
    },

    resizeManagedHost(hostId) {
        const player = hostId === "ffmpeg-player-host"
            ? this._ffmpegPlayer
            : hostId === "rtsp-player-host"
                ? this._rtspPlayer
                : this._presetManagedPlayer;

        resizeManagedPlayer(hostId, player);
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
            this._altTsPlayback?.stop();
            this._altTsPlayback = null;
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
            this._altHlsPlayback?.stop();
            this._altHlsPlayback = null;
            this._altHls.destroy();
            this._altHls = null;
        }

        if (this._altVideoJs) {
            this._altHlsPlayback?.stop();
            this._altHlsPlayback = null;
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
            enableStashBuffer: true,
            lazyLoad: false,
            deferLoadAfterSourceOpen: false,
            autoCleanupSourceBuffer: true,
            liveBufferLatencyChasing: true,
            liveBufferLatencyMaxLatency: 2,
            liveBufferLatencyMinRemain: 0.5,
            stashInitialSize: 128
        });

        player.attachMediaElement(video);
        player.load();
        this._altTsPlayback = createLivePlaybackController(video, {
            play: () => player.play()
        });
        player.on(window.mpegts.Events.MEDIA_INFO, () => this._altTsPlayback?.tryPlay("media-info"));
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
                autoplay: "muted",
                muted: true,
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
            const playback = createLivePlaybackController(video, {
                play: () => player.play()
            });
            player.ready(() => playback.tryPlay("videojs-ready"));
            this._altVideoJs = player;
            this._altHlsPlayback = playback;

            return {
                resolvedUrl,
                engine: "video.js",
                status: "Loading",
                log: `Opening ${resolvedUrl}`
            };
        }

        if (video.canPlayType("application/vnd.apple.mpegurl")) {
            video.src = resolvedUrl;
            this._altHlsPlayback = createLivePlaybackController(video);
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
        this._altHlsPlayback = createLivePlaybackController(video);
        hls.loadSource(resolvedUrl);
        hls.attachMedia(video);
        hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
            this._altHlsPlayback?.tryPlay("manifest-parsed");
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
        this._hlsPlayers[elementId]?.playback?.stop();
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
            const playback = createLivePlaybackController(video, {
                onStatus: (status) => { this._hlsState.status = status; }
            });
            this._hlsState = { framework: "Native HLS", status: "Loaded", lastError: "" };
            this._hlsPlayers[elementId] = { kind: "native", playback };
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
            }, watchdog, () => this._hlsPlayers[elementId]?.playback);
            return { resolvedUrl, framework: "Native HLS", status: "Loaded" };
        }

        if (window.Hls && window.Hls.isSupported()) {
            const hls = new window.Hls(buffering.hls);
            const playback = createLivePlaybackController(video, {
                onStatus: (status) => { this._hlsState.status = status; }
            });
            hls.on(window.Hls.Events.MANIFEST_PARSED, () => {
                this._hlsState.status = "Manifest parsed";
                this._hlsState.lastError = "";
                playback.tryPlay("manifest-parsed");
            });
            hls.on(window.Hls.Events.ERROR, (_, data) => {
                this._hlsState.status = `hls.js error (${data.type})`;
                this._hlsState.lastError = data?.details || data?.type || "Unknown HLS error";
            });
            hls.loadSource(resolvedUrl);
            hls.attachMedia(video);
            this._hls = hls;
            this._hlsPlayers[elementId] = { kind: "hls", player: hls, playback };
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
            }, watchdog, () => this._hlsPlayers[elementId]?.playback);
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
        this._hlsPlayers["hls-video"]?.playback?.stop();
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
        entry?.playback?.stop();
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

    getWatchVideoScale() {
        try {
            const raw = window.localStorage.getItem("h265player:watch:video-scale");
            const parsed = Number.parseInt(raw, 10);
            if (!Number.isNaN(parsed)) {
                return Math.min(100, Math.max(20, parsed));
            }
        } catch {
        }

        return 100;
    },

    setWatchVideoScale(scale) {
        const parsed = Number.parseInt(scale, 10);
        this._watchVideo.scale = Number.isNaN(parsed) ? 100 : Math.min(100, Math.max(20, parsed));
        try {
            window.localStorage.setItem("h265player:watch:video-scale", String(this._watchVideo.scale));
        } catch {
        }

        this.applyWatchVideoSize();
        this.bindWatchVideoSize();
        return this._watchVideo.scale;
    },

    applyWatchVideoSize() {
        window.requestAnimationFrame(() => this._applyWatchVideoSizeNow());
    },

    _applyWatchVideoSizeNow() {
        const shell = document.getElementById("preset-video-shell");
        if (!shell) {
            return;
        }

        const panel = shell.closest(".preset-player-panel");
        const meta = panel?.querySelector(".preset-meta");
        const panelStyle = panel ? window.getComputedStyle(panel) : null;
        const padX = panelStyle
            ? (Number.parseFloat(panelStyle.paddingLeft) || 0) + (Number.parseFloat(panelStyle.paddingRight) || 0)
            : 0;
        const contentW = Math.max(240, (panel?.clientWidth || window.innerWidth) - padX);
        const shellTop = shell.getBoundingClientRect().top;
        const metaStyle = meta ? window.getComputedStyle(meta) : null;
        const metaH = meta
            ? meta.getBoundingClientRect().height + (Number.parseFloat(metaStyle?.marginTop) || 0)
            : 0;
        const availH = Math.max(160, window.innerHeight - shellTop - metaH - 8);
        const scale = (this._watchVideo.scale || 100) / 100;
        const width = Math.max(240, Math.round(contentW * scale));
        const height = Math.max(135, Math.round(availH * scale));
        shell.style.width = `${width}px`;
        shell.style.height = `${height}px`;
        shell.style.maxWidth = "100%";

        for (const element of shell.querySelectorAll(".player-host, .video-host")) {
            const frame = element.closest(".preset-video-frame");
            if (frame && frame.style.display === "none") {
                continue;
            }

            element.style.width = "100%";
            element.style.height = "100%";
            element.style.minHeight = "0";
        }

        this.resizeManagedHost("preset-managed-host");
    },

    bindWatchVideoSize() {
        if (this._watchVideo.resizeHandler) {
            return;
        }

        this._watchVideo.resizeHandler = () => this.applyWatchVideoSize();
        window.addEventListener("resize", this._watchVideo.resizeHandler);

        if (typeof ResizeObserver === "undefined") {
            return;
        }

        const content = document.querySelector(".content-shell");
        this._watchVideo.observer = new ResizeObserver(() => this.applyWatchVideoSize());
        if (content) {
            this._watchVideo.observer.observe(content);
        }
    },

    releaseWatchVideoSize() {
        if (this._watchVideo.resizeHandler) {
            window.removeEventListener("resize", this._watchVideo.resizeHandler);
            this._watchVideo.resizeHandler = null;
        }

        this._watchVideo.observer?.disconnect?.();
        this._watchVideo.observer = null;
    },

    enablePageRemoteKeys(dotNetRef) {
        this.disablePageRemoteKeys();
        this._pageRemoteKeysEnabled = true;
        this._pageRemoteKeysRef = dotNetRef;
        this._pageRemoteKeysHandler = (event) => {
            if (!this._pageRemoteKeysEnabled || event.repeat || event.metaKey || event.ctrlKey || event.altKey) {
                return;
            }

            const target = event.target;
            if (target instanceof Element) {
                if (target.closest("input, textarea, select, [contenteditable='true']")) {
                    return;
                }
            }

            const mapped = this._isPageRemoteKey(event.key);
            if (!mapped) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            event.stopImmediatePropagation();
            this._pageRemoteKeysRef?.invokeMethodAsync("HandlePageKey", event.key, !!event.shiftKey);
        };
        window.addEventListener("keydown", this._pageRemoteKeysHandler, true);
    },

    setPageRemoteKeysEnabled(enabled) {
        this._pageRemoteKeysEnabled = !!enabled;
    },

    disablePageRemoteKeys() {
        if (this._pageRemoteKeysHandler) {
            window.removeEventListener("keydown", this._pageRemoteKeysHandler, true);
        }

        this._pageRemoteKeysHandler = null;
        this._pageRemoteKeysRef = null;
        this._pageRemoteKeysEnabled = false;
    },

    _isPageRemoteKey(key) {
        return key === "ArrowUp" || key === "ArrowDown" || key === "ArrowLeft" || key === "ArrowRight" ||
            key === "Enter" || key === "Escape" || key === "Backspace" || key === "Home" ||
            key === " " || key === "Spacebar" || key === "+" || key === "-" || key === "=" ||
            key === "PageUp" || key === "PageDown" ||
            (key.length === 1 && ((key >= "0" && key <= "9") || "hHiIgGmMpP".includes(key)));
    },

    enableMiniRemote(panelId, handleId, storageKey) {
        const panel = document.getElementById(panelId);
        const handle = document.getElementById(handleId);
        if (!panel || !handle) {
            return;
        }

        const positionKey = storageKey || panelId;

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
            localStorage.setItem(this._miniRemoteKey(positionKey), JSON.stringify(clamped));
            panel.style.left = `${clamped.left}px`;
            panel.style.top = `${clamped.top}px`;
        };

        const loadPosition = () => {
            try {
                const raw = localStorage.getItem(this._miniRemoteKey(positionKey));
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
        let pointerId = null;

        const move = (event) => {
            if (!dragging || (pointerId !== null && event.pointerId !== pointerId)) {
                return;
            }

            event.preventDefault();
            lastX = event.clientX;
            lastY = event.clientY;
            const dx = event.clientX - startX;
            const dy = event.clientY - startY;
            const next = clampPosition(originLeft + dx, originTop + dy);
            panel.style.left = `${next.left}px`;
            panel.style.top = `${next.top}px`;
        };

        const up = (event) => {
            if (!dragging || (pointerId !== null && event?.pointerId !== pointerId)) {
                return;
            }

            dragging = false;
            savePosition(originLeft + (lastX - startX), originTop + (lastY - startY));
            if (pointerId !== null && typeof handle.releasePointerCapture === "function") {
                try {
                    handle.releasePointerCapture(pointerId);
                } catch {
                }
            }

            pointerId = null;
            document.removeEventListener("pointermove", move);
            document.removeEventListener("pointerup", up);
            document.removeEventListener("pointercancel", up);
        };

        handle.addEventListener("pointerdown", (event) => {
            if (event.button !== undefined && event.button !== 0) {
                return;
            }

            if (event.target instanceof Element && event.target.closest("button")) {
                return;
            }

            dragging = true;
            pointerId = event.pointerId ?? null;
            startX = event.clientX;
            startY = event.clientY;
            lastX = event.clientX;
            lastY = event.clientY;
            originLeft = parseInt(panel.style.left || "24", 10);
            originTop = parseInt(panel.style.top || "24", 10);
            event.preventDefault();

            if (pointerId !== null && typeof handle.setPointerCapture === "function") {
                try {
                    handle.setPointerCapture(pointerId);
                } catch {
                }
            }

            document.addEventListener("pointermove", move, { passive: false });
            document.addEventListener("pointerup", up);
            document.addEventListener("pointercancel", up);
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
