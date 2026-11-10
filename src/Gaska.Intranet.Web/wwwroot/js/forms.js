// Feedback while a classic (non-Blazor) form is being submitted: login, codes, logout.
// The submit button shows a spinner and the form ignores further clicks (no double submits).
// The button is NOT disabled – a disabled submitter would drop its name/value (e.g. "forgetDevice").
(function () {
    document.addEventListener("submit", event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.matches("[data-busy-on-submit]")) {
            return;
        }

        if (form.dataset.busy === "true") {
            event.preventDefault();
            return;
        }

        form.dataset.busy = "true";
        form.setAttribute("aria-busy", "true");
        const button = event.submitter ?? form.querySelector("button[type=submit]");
        button?.classList.add("is-busy");
    });

    // Coming back with the browser "Back" button restores the page from cache – reset the state.
    window.addEventListener("pageshow", () => {
        document.querySelectorAll("form[data-busy-on-submit]").forEach(form => {
            delete form.dataset.busy;
            form.removeAttribute("aria-busy");
            form.querySelectorAll(".is-busy").forEach(b => b.classList.remove("is-busy"));
        });
    });
})();
