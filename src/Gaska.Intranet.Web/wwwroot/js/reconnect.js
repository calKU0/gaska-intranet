// The connection to the server was lost (Blazor shows #components-reconnect-modal and sets its state classes).
// - "rejected": the server no longer knows this page (it was restarted) - reload it, nothing can be resumed.
// - "failed": Blazor gave up retrying - "Spróbuj ponownie" retries, and so does coming back to the tab or network.
(function () {
    const modal = document.getElementById("components-reconnect-modal");
    if (!modal) {
        return;
    }

    async function retry() {
        try {
            // Resumes the circuit; false = the server does not have it any more.
            const resumed = await Blazor.reconnect();
            if (!resumed) {
                location.reload();
            }
        } catch {
            location.reload();
        }
    }

    modal.addEventListener("components-reconnect-state-changed", event => {
        const state = event.detail?.state;
        if (state === "rejected" || state === "resume-failed") {
            location.reload();
        }
    });

    modal.querySelector("[data-reconnect]")?.addEventListener("click", retry);

    const failed = () => modal.classList.contains("components-reconnect-failed");
    window.addEventListener("online", () => failed() && retry());
    document.addEventListener("visibilitychange", () => document.visibilityState === "visible" && failed() && retry());
})();
