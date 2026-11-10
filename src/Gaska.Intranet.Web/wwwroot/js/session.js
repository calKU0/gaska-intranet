// Keeps the session alive while the user works on the page.
// A Blazor page talks to the server over a WebSocket, so the session cookie never refreshes by itself.
// The script: (1) after user activity pings the server at most once a minute – the session is extended,
//             (2) after the idle timeout reloads the page – the server redirects to the login page.
(function () {
    const script = document.currentScript;
    const idleMs = (parseInt(script?.dataset.idleMinutes, 10) || 60) * 60 * 1000;
    const pingUrl = script?.dataset.pingUrl || "/konto/sesja";
    const pingEveryMs = 60 * 1000;

    let lastActivity = Date.now();
    let lastPing = Date.now();

    const markActive = () => { lastActivity = Date.now(); };
    ["pointerdown", "keydown", "wheel", "touchstart", "mousemove", "scroll"]
        .forEach(e => window.addEventListener(e, markActive, { passive: true }));

    async function ping() {
        lastPing = Date.now();
        try {
            const response = await fetch(pingUrl, { credentials: "same-origin", cache: "no-store" });
            const body = await response.json();
            if (!body.authenticated) {
                location.reload();
            }
        } catch {
            // Network error – retry on the next activity.
        }
    }

    setInterval(() => {
        const now = Date.now();
        if (now - lastActivity >= idleMs) {
            location.reload();
        } else if (lastActivity > lastPing && now - lastPing >= pingEveryMs) {
            ping();
        }
    }, 15 * 1000);
})();
