// Camera player. Video comes from go2rtc through the intranet's own WebSocket (/kamery/strumien/…, /kamery/nagranie/…):
// fragmented MP4 played with Media Source Extensions (ManagedMediaSource on iPhone / iPad), MJPEG frames when MSE is
// missing. When the WebSocket does not get through (IIS without WebSockets, a proxy) the same video comes over plain
// HTTP, and only when that fails too – or in demo mode – a picture refreshed every second or two.
// A player runs only while it is on screen, the browser tab is visible and it is not paused (e.g. while one camera is
// enlarged), so a page of tiles costs nothing in the background. Blazor never renders inside the player's element:
// this script owns its <video> / <img>.

const CODECS = ["avc1.640029", "avc1.64002A", "avc1.640033", "hvc1.1.6.L153.B0", "mp4a.40.2", "mp4a.40.5", "flac", "opus"];
const LIVE_EDGE_SECONDS = 1.5;
const KEEP_BUFFER_SECONDS = 8;
const STALL_TIMEOUT_MS = 12000;
const PLAYBACK_STALL_TIMEOUT_MS = 20000;

const players = new WeakMap();

// Full-screen buttons (data-camera-fullscreen="<view element id>") are handled here, in the click itself – browsers allow
// full screen only straight from a user's gesture, not after a round trip to the server.
document.addEventListener("click", event => {
    const button = event.target instanceof Element ? event.target.closest("[data-camera-fullscreen]") : null;
    const view = button ? document.getElementById(button.dataset.cameraFullscreen) : null;
    if (view) {
        fullscreen(view);
    }
});

export function attach(element, options) {
    detach(element);
    const player = new Player(element, options);
    players.set(element, player);
    player.init();
}

export function detach(element) {
    const player = players.get(element);
    if (player) {
        player.destroy();
        players.delete(element);
    }
}

/** Pauses / resumes a player without forgetting it (e.g. the tiles behind an enlarged camera). */
export function setPaused(element, paused) {
    players.get(element)?.setPaused(paused);
}

/**
 * The picture on the whole screen – on a phone turned to landscape where the browser allows it. iPhone Safari has no
 * fullscreen for elements, only for the video itself.
 */
export async function fullscreen(element) {
    const video = element.querySelector("video");
    try {
        if (element.requestFullscreen) {
            await element.requestFullscreen({ navigationUI: "hide" });
            await screen.orientation?.lock?.("landscape").catch(() => { /* not allowed on this device */ });
        } else if (video?.webkitEnterFullscreen) {
            video.webkitEnterFullscreen();
        }
    } catch {
        video?.webkitEnterFullscreen?.();
    }
}

/** Plays at another rate without restarting (the camera already sends at that speed). */
/** Pauses a playback on the picture shown and ends its stream. */
export function hold(element) {
    players.get(element)?.hold();
}

export function setRate(element, rate) {
    players.get(element)?.setRate(rate);
}

/** Seconds of video played so far (recording time without the gaps the camera skips); null before the first frame. */
export function position(element) {
    return players.get(element)?.mediaSeconds() ?? null;
}

class Player {
    constructor(element, options) {
        this.el = element;
        this.opts = Object.assign({ live: true, lazy: false, refreshMs: 1500, clockStart: 0, catchUp: true, rate: 1 }, options);
        this.visible = !this.opts.lazy;
        this.paused = false;
        this.running = false;
        this.failures = 0;
        this.mode = this.opts.live && this.opts.socketUrl ? "video" : "snapshot";
        this.playback = !this.opts.catchUp;
        this.onVisibility = () => this.update();
    }

    init() {
        this.el.dataset.state = "loading";
        this.borrow();
        document.addEventListener("visibilitychange", this.onVisibility);
        if (this.opts.lazy && "IntersectionObserver" in window) {
            this.observer = new IntersectionObserver(entries => {
                this.visible = entries.some(e => e.isIntersecting);
                this.update();
            });
            this.observer.observe(this.el);
        } else {
            this.visible = true;
        }

        this.update();
    }

    destroy() {
        this.giveBack();
        document.removeEventListener("visibilitychange", this.onVisibility);
        this.observer?.disconnect();
        this.stop();
        this.el.replaceChildren();
    }

    /** Pictures (demo, MJPEG) have no media clock – the played time is counted from the first picture. */
    usesWallClock() {
        return this.mode === "snapshot" || this.wallClock === true;
    }

    setRate(rate) {
        if (this.usesWallClock()) {
            // Demo / pictures: the "recording clock" goes on from where it is at the new rate.
            this.mediaBase = this.mediaSeconds() ?? 0;
            this.startedAt = Date.now();
        }

        this.opts.rate = rate;
        if (this.video) {
            this.video.playbackRate = rate;
        }
    }

    setPaused(paused) {
        this.paused = paused;
        if (this.lentTo && paused) {
            return; // The enlarged view shows this video right now – it stops when it gets it back.
        }

        this.update();
    }

    // ---- Hand-off: a tile's running video shows in the enlarged view at once --------------------------------------

    /** Shows the running video of another player (the tile) until this player's own stream plays. */
    borrow() {
        const source = this.opts.handoffFrom ? players.get(document.getElementById(this.opts.handoffFrom)) : null;
        if (!source || source.lentTo || !source.video || source.video.parentElement !== source.el
            || source.el.dataset.state !== "live") {
            return;
        }

        source.lentTo = this;
        this.borrowed = source;
        const shield = document.createElement("div");
        shield.className = "gaska-camera-shield";
        this.el.replaceChildren(source.video, shield);
        this.el.dataset.state = "live";
    }

    /** Returns the borrowed video to its tile (this player's own stream plays, or the view closes). */
    giveBack() {
        const source = this.borrowed;
        if (!source) {
            return;
        }

        this.borrowed = null;
        source.lentTo = null;
        source.showVideo(true);
        source.update(); // a pause asked for meanwhile happens now
    }

    update() {
        const shouldRun = this.visible && !this.paused && document.visibilityState === "visible";
        if (shouldRun && !this.running) {
            this.start();
        } else if (!shouldRun && this.running) {
            this.stop();
            this.el.dataset.state = "paused";
        }
    }

    start() {
        if (this.playback && this.finished) {
            return; // A playback that reached its end is not started again by scrolling or switching tabs.
        }

        this.running = true;
        this.startedAt = Date.now();
        if (!this.borrowed) {
            this.el.dataset.state = "loading";
        }
        if (this.mode === "video") {
            this.connect();
        } else if (this.mode === "http") {
            this.startHttpVideo();
        } else {
            this.startSnapshots();
        }
    }

    stop() {
        this.running = false;
        clearTimeout(this.retryTimer);
        clearTimeout(this.snapshotTimer);
        clearInterval(this.stallTimer);
        if (this.ws) {
            const ws = this.ws;
            ws.onclose = null;
            ws.onmessage = null;
            ws.onerror = null;
            if (ws.readyState === WebSocket.CONNECTING) {
                // Closing a socket mid-handshake only floods the console – close it once it opens.
                ws.onopen = () => ws.close();
            } else {
                ws.close();
            }
            this.ws = null;
        }

        if (this.video) {
            this.video.onerror = this.video.onended = this.video.onloadeddata = null;
            this.video.pause();
            this.video.removeAttribute("src");
            this.video.srcObject = null;
            this.video.load();
        }

        if (this.frameUrl) {
            URL.revokeObjectURL(this.frameUrl);
            this.frameUrl = null;
        }
    }

    mediaSeconds() {
        if (this.usesWallClock()) {
            return this.startedAt ? (this.mediaBase ?? 0) + (Date.now() - this.startedAt) / 1000 * this.opts.rate : null;
        }

        if (!this.video || this.mediaStart == null) {
            return null;
        }

        return Math.max(0, this.video.currentTime - this.mediaStart);
    }

    hold() {
        // The position stays where it is (a stopped clock for the demo pictures).
        if (this.usesWallClock()) {
            this.mediaBase = this.mediaSeconds() ?? 0;
            this.startedAt = null;
        }

        // The frame on screen stays as a still picture; the stream (WebSocket or HTTP download) ends.
        let still = null;
        const video = this.video;
        if (video && video.parentElement === this.el && video.videoWidth > 0) {
            still = document.createElement("canvas");
            still.width = video.videoWidth;
            still.height = video.videoHeight;
            still.getContext("2d").drawImage(video, 0, 0);
        }

        this.finished = true; // not started again by scrolling or switching tabs
        this.stop();
        if (still) {
            this.el.replaceChildren(still);
        }

        this.el.dataset.state = "paused";
    }

    /** A playback that cannot go on: "ended" after the last recording, "stalled" when the camera stopped sending. */
    finish(state) {
        this.finished = true;
        this.stop();
        this.el.dataset.state = state;
    }

    // ---- Video over the WebSocket ------------------------------------------------------------------------------

    connect() {
        this.showPoster();
        const url = new URL(this.opts.socketUrl, location.href);
        url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        const ws = new WebSocket(url);
        ws.binaryType = "arraybuffer";
        this.ws = ws;
        this.received = false;
        this.lastData = Date.now();

        // A proxy that holds the handshake must not keep the tile waiting.
        const openTimeout = setTimeout(() => ws.readyState === WebSocket.CONNECTING && ws.close(), 8000);
        ws.onopen = () => {
            clearTimeout(openTimeout);
            this.requestStream();
        };
        ws.onmessage = event => {
            if (typeof event.data === "string") {
                this.onControl(JSON.parse(event.data));
            } else {
                this.lastData = Date.now();
                this.ondata?.(event.data);
            }
        };
        ws.onclose = () => {
            this.ws = null;
            if (!this.running) {
                return;
            }

            // A playback is not restarted from its beginning: once it played, the end of the stream is its end.
            if (this.playback && this.received) {
                this.finish(this.stalled ? "stalled" : "ended");
                return;
            }

            this.failures = this.received ? 0 : this.failures + 1;
            // Two failed attempts in a row: the WebSocket does not get through (IIS without WebSockets, a proxy) or the
            // browser cannot play it – try the same video over plain HTTP, then pictures.
            if (this.failures >= 2) {
                this.fallBack();
                return;
            }

            this.el.dataset.state = "loading";
            this.retryTimer = setTimeout(() => this.running && this.connect(), Math.min(2000 * (this.failures + 1), 15000));
        };

        clearInterval(this.stallTimer);
        this.stalled = false;
        const timeout = this.playback ? PLAYBACK_STALL_TIMEOUT_MS : STALL_TIMEOUT_MS;
        this.stallTimer = setInterval(() => {
            if (this.ws && Date.now() - this.lastData > timeout) {
                this.stalled = this.received;
                this.ws.close(); // onclose reconnects (live) or ends (playback)
            }
        }, 3000);
    }

    fallBack() {
        if (this.mode === "video" && this.opts.videoUrl) {
            this.mode = "http";
            this.failures = 0;
            this.startHttpVideo();
        } else if (this.opts.snapshotUrl) {
            this.mode = "snapshot";
            this.startSnapshots();
        } else {
            this.el.dataset.state = "error";
        }
    }

    // ---- Video over plain HTTP (fragmented MP4 in a <video>) ---------------------------------------------------

    startHttpVideo() {
        this.showPoster();
        const video = this.ensureVideo();
        const url = new URL(this.opts.videoUrl, location.href);
        url.searchParams.set("t", String(Date.now()));
        this.received = false;
        this.mediaStart = 0;
        video.srcObject = null;
        video.onloadeddata = () => {
            this.received = true;
            this.failures = 0;
            this.el.dataset.state = "live";
            this.showVideo();
            video.playbackRate = this.opts.rate;
        };
        video.onerror = video.onended = () => {
            if (!this.running || this.mode !== "http") {
                return;
            }

            if (this.playback && this.received) {
                this.finish(video.error ? "stalled" : "ended");
                return;
            }

            this.failures = this.received ? 0 : this.failures + 1;
            if (this.failures >= 2) {
                this.fallBack();
                return;
            }

            this.retryTimer = setTimeout(() => this.running && this.startHttpVideo(), 2000);
        };
        video.src = url.toString();
        video.play().catch(() => { /* muted autoplay is allowed; ignore races with stop() */ });

        // Live: the browser buffers a progressive stream – keep close to "now".
        clearInterval(this.stallTimer);
        if (this.opts.catchUp) {
            this.stallTimer = setInterval(() => {
                const buffered = video.buffered;
                if (buffered.length && buffered.end(buffered.length - 1) - video.currentTime > 3) {
                    video.currentTime = buffered.end(buffered.length - 1) - 0.5;
                }
            }, 2000);
        }
    }

    send(message) {
        if (this.ws?.readyState === WebSocket.OPEN) {
            this.ws.send(JSON.stringify(message));
        }
    }

    requestStream() {
        const MS = window.ManagedMediaSource || window.MediaSource;
        if (MS && !this.useMjpeg) {
            this.startMse(MS);
        } else {
            this.startMjpeg();
        }
    }

    onControl(message) {
        if (message.type === "mse" && this.onMseType) {
            this.onMseType(message.value);
        } else if (message.type === "error") {
            // No common codec for MSE – try MJPEG once, then the fallbacks.
            if (!this.useMjpeg) {
                this.useMjpeg = true;
                this.ws?.close();
            } else {
                this.failures = 2;
                this.ws?.close();
            }
        }
    }

    // A current picture while the video connects (the first connection to a camera takes a few seconds).
    showPoster() {
        if (!this.opts.snapshotUrl || this.el.childElementCount > 0) {
            return;
        }

        const img = document.createElement("img");
        img.className = "gaska-camera-poster";
        img.alt = "";
        const url = new URL(this.opts.snapshotUrl, location.href);
        url.searchParams.set("t", String(Date.now()));
        img.src = url.toString();
        this.el.replaceChildren(img);
    }

    // The video goes on screen with its first frame – until then the poster (or the last picture) stays visible.
    // A transparent shield covers it: browsers (Opera) put their own "pop out" buttons on a hovered <video>.
    showVideo(force) {
        if (this.borrowed) {
            const borrowed = this.borrowed;
            this.el.replaceChildren();
            this.giveBack();
            if (borrowed.video === this.video) {
                return;
            }
        }

        if (this.video && (force || this.video.parentElement !== this.el)) {
            if (!this.shield) {
                this.shield = document.createElement("div");
                this.shield.className = "gaska-camera-shield";
            }

            this.el.replaceChildren(this.video, this.shield);
        }
    }

    ensureVideo() {
        if (!this.video) {
            const video = document.createElement("video");
            video.muted = true;
            video.playsInline = true;
            video.autoplay = true;
            video.disableRemotePlayback = true;
            video.disablePictureInPicture = true;
            video.setAttribute("disablepictureinpicture", "");
            video.setAttribute("controlslist", "nodownload nofullscreen noremoteplayback noplaybackrate");
            video.setAttribute("aria-hidden", "true");
            this.video = video;
        }

        return this.video;
    }

    startMse(MS) {
        const video = this.ensureVideo();
        const managed = MS === window.ManagedMediaSource;
        const ms = new MS();
        this.mediaStart = null;
        ms.addEventListener("sourceopen", () => {
            if (!managed) {
                URL.revokeObjectURL(video.src);
            }

            const isSupported = type => MS.isTypeSupported(type);
            const codecs = CODECS.filter(c => isSupported(`video/mp4; codecs="${c}"`)).join();
            this.send({ type: "mse", value: codecs });
        }, { once: true });

        if (managed) {
            video.srcObject = ms;
        } else {
            video.src = URL.createObjectURL(ms);
        }

        this.onMseType = type => {
            let sb;
            try {
                sb = ms.addSourceBuffer(type);
            } catch {
                this.onControl({ type: "error" });
                return;
            }

            sb.mode = "segments";
            const queue = [];
            const pump = () => {
                if (sb.updating || ms.readyState !== "open") {
                    return;
                }

                if (queue.length) {
                    try {
                        sb.appendBuffer(queue.shift());
                    } catch {
                        queue.length = 0;
                    }

                    return;
                }

                const buffered = sb.buffered;
                if (!buffered.length) {
                    return;
                }

                if (this.mediaStart == null) {
                    this.mediaStart = buffered.start(0);
                }

                const end = buffered.end(buffered.length - 1);
                // Playback keeps more behind the play position – a faster playback reads further ahead.
                const keep = KEEP_BUFFER_SECONDS * Math.max(1, this.opts.rate);
                const startKeep = Math.min(end, video.currentTime) - keep;
                if (startKeep > buffered.start(0)) {
                    sb.remove(buffered.start(0), startKeep);
                    return;
                }

                // Live: stay close to "now" (the tab may have been throttled); playback just plays on.
                if (this.opts.catchUp && end - video.currentTime > LIVE_EDGE_SECONDS) {
                    video.currentTime = end - 0.3;
                } else if (video.currentTime < buffered.start(0)) {
                    video.currentTime = buffered.start(0);
                }
            };

            sb.addEventListener("updateend", pump);
            this.ondata = data => {
                if (!this.received) {
                    this.received = true;
                    this.failures = 0;
                    this.el.dataset.state = "live";
                    this.showVideo();
                    video.playbackRate = this.opts.rate;
                    video.play().catch(() => { /* muted autoplay is allowed; ignore races with stop() */ });
                }

                queue.push(data);
                if (queue.length > 120) {
                    queue.splice(0, queue.length - 120);
                }

                pump();
            };

            video.play().catch(() => { /* autoplay of a muted video is allowed; ignore races with stop() */ });
        };
    }

    startMjpeg() {
        const img = document.createElement("img");
        img.alt = "";
        this.el.replaceChildren(img);
        this.onMseType = null;
        this.wallClock = false;
        this.ondata = data => {
            if (!this.received) {
                this.received = true;
                this.failures = 0;
                this.el.dataset.state = "live";
                // Playback position: counted from the first picture at the playback rate.
                this.wallClock = true;
                this.mediaBase = 0;
                this.startedAt = Date.now();
            }

            const url = URL.createObjectURL(new Blob([data], { type: "image/jpeg" }));
            img.onload = () => {
                if (this.frameUrl && this.frameUrl !== url) {
                    URL.revokeObjectURL(this.frameUrl);
                }

                this.frameUrl = url;
            };
            img.src = url;
        };
        this.send({ type: "mjpeg" });
    }

    // ---- Refreshed pictures (demo, fallback) ----------------------------------------------------------------------

    startSnapshots() {
        if (!this.opts.snapshotUrl) {
            this.el.dataset.state = "error";
            return;
        }

        let img = this.el.querySelector("img.gaska-camera-snapshot");
        if (!img) {
            img = document.createElement("img");
            img.className = "gaska-camera-snapshot";
            img.alt = "";
            this.el.replaceChildren(img);
        }

        const next = () => {
            if (!this.running) {
                return;
            }

            const url = new URL(this.opts.snapshotUrl, location.href);
            if (this.opts.clockStart) {
                // Demo recordings: the frame of the moment the "recording" has reached.
                url.searchParams.set("czas", String(this.opts.clockStart + Math.floor(this.mediaSeconds() ?? 0)));
            }

            url.searchParams.set("t", String(Date.now()));
            const loader = new Image();
            loader.onload = () => {
                img.src = loader.src;
                this.el.dataset.state = this.opts.live && this.opts.socketUrl ? "snapshot" : "live";
                this.snapshotTimer = setTimeout(next, this.opts.refreshMs);
            };
            loader.onerror = () => {
                this.el.dataset.state = "error";
                this.snapshotTimer = setTimeout(next, Math.max(this.opts.refreshMs * 3, 5000));
            };
            loader.src = url.toString();
        };

        next();
    }
}
