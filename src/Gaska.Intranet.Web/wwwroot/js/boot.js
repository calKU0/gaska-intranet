// Theme: dark unless the user chose light. The server renders <html class="gaska-dark"> from the "gaska-theme"
// cookie, so the page never flashes the other colours.
(function () {
    const root = document.documentElement;

    // Called by the theme switch (MainLayout) – keeps our CSS in step with MudBlazor's palette.
    window.gaskaTheme = {
        set(mode, dark) {
            document.cookie = `gaska-theme=${mode}; path=/; max-age=31536000; samesite=lax; secure`;
            root.classList.toggle("gaska-dark", dark);
            // Colour of the phone's status bar / app title bar follows the theme.
            document.querySelector('meta[name="theme-color"]')?.setAttribute("content", dark ? "#161616" : "#FFFFFF");
        },
    };
})();

// Starts Blazor and switches entry animations on only once the interactive page has replaced the pre-rendered one.
// Without this the first visit animated twice: once for the server-rendered HTML and again when Blazor swapped it
// for the interactive copy, which looked like the page reloading itself.
// <html class="gaska-booting"> (set by App.razor on interactive pages) suppresses the animations until then.
(function () {
    const root = document.documentElement;
    const prerendered = document.querySelector(".gaska-page");
    const started = performance.now();

    function finish() {
        // A short pause lets the browser paint the interactive DOM without animations first.
        // (Timers, not animation frames: those never fire in a tab opened in the background.)
        setTimeout(() => root.classList.remove("gaska-booting"), 50);
    }

    function waitForSwap() {
        const swapped = !prerendered || !prerendered.isConnected;
        if (swapped || performance.now() - started > 5000) {
            finish();
            return;
        }

        setTimeout(waitForSwap, 16);
    }

    Blazor.start().then(waitForSwap, finish);
})();

// Scrolls an element into view (centred) and focuses a field inside it – e.g. "Napisz odpowiedź" in a long thread.
// Form errors: bring an element into view unless it is already fully visible (below the app bar).
// Works inside dialogs too – scrollIntoView also scrolls the dialog's own content.
window.gaskaReveal = element => {
    if (!element || !element.getBoundingClientRect) {
        return;
    }

    const rect = element.getBoundingClientRect();
    const appBar = document.querySelector(".mud-appbar")?.getBoundingClientRect().bottom ?? 0;
    if (rect.top >= appBar && rect.bottom <= window.innerHeight) {
        return;
    }

    element.scrollIntoView({ behavior: "smooth", block: "center" });
};

// Form submitted with invalid fields: scroll to the first field with a validation message and focus it.
window.gaskaRevealInvalid = () => {
    // The validation messages render right after the submit – look once they are on the page.
    setTimeout(() => {
        const invalid = document.querySelector(".mud-input-error, .validation-message");
        const field = invalid?.closest(".mud-input-control") ?? invalid;
        if (!field) {
            return;
        }

        window.gaskaReveal(field);
        field.querySelector("input, textarea")?.focus({ preventScroll: true });
    }, 50);
};

window.gaskaScrollTo = (id, focusSelector) => {
    const element = document.getElementById(id);
    if (!element) {
        return;
    }

    element.scrollIntoView({ behavior: "smooth", block: "center" });
    const field = focusSelector ? element.querySelector(focusSelector) : null;
    field?.focus({ preventScroll: true });
};

// Phones / tablets: open or close the menu the moment it is tapped. The click still goes to the server (Blazor sets
// the same classes a moment later); without this the slide waited for the server's reply – a noticeable lag on mobile data.
(function () {
    const narrow = window.matchMedia("(max-width: 959.98px)");

    document.addEventListener("click", event => {
        if (!narrow.matches || !(event.target instanceof Element)) {
            return;
        }

        const drawer = document.querySelector(".gaska-drawer");
        if (!drawer) {
            return;
        }

        const opening = event.target.closest(".gaska-menu-button") && drawer.classList.contains("mud-drawer--closed");
        const closing = drawer.classList.contains("mud-drawer--open") &&
            (event.target.closest(".gaska-menu-button") || event.target.closest(".mud-drawer-overlay"));
        if (!opening && !closing) {
            return;
        }

        drawer.classList.remove("mud-drawer--initial");
        drawer.classList.toggle("mud-drawer--open", !!opening);
        drawer.classList.toggle("mud-drawer--closed", !opening);
    }, true);
})();

// After navigating to another page: focus its heading for keyboard and screen-reader users, without scrolling.
window.gaskaFocusHeading = () => {
    // A timer, not an animation frame: frames do not run in a tab opened in the background.
    setTimeout(() => {
        const heading = document.querySelector(".gaska-page h1, h1");
        if (!heading) {
            return;
        }

        if (!heading.hasAttribute("tabindex")) {
            heading.setAttribute("tabindex", "-1");
        }

        heading.focus({ preventScroll: true });
    }, 50);
};

// Installed app (PWA): the service worker only serves a "no connection" page when the network is down.
if ("serviceWorker" in navigator) {
    window.addEventListener("load", () => {
        navigator.serviceWorker.register("/sw.js").catch(() => { /* not essential */ });
    });
}

// Installing the app (PWA). Inside the installed app <html> gets "gaska-standalone" and everything marked
// .gaska-browser-only (install banner, "Zainstaluj aplikację") disappears. Chrome / Edge offer their install
// dialog through "beforeinstallprompt" – kept here so the intranet's own button can open it.
(function () {
    const root = document.documentElement;
    const standalone = window.matchMedia("(display-mode: standalone)").matches ||
        window.matchMedia("(display-mode: fullscreen)").matches || window.navigator.standalone === true;
    let deferredPrompt = null;

    if (standalone) {
        root.classList.add("gaska-standalone");
    }

    try {
        if (localStorage.getItem("gaska-install-dismissed") === "1") {
            root.classList.add("gaska-install-dismissed");
        }
    } catch {
        // Storage blocked (private mode) – the banner simply shows.
    }

    window.addEventListener("beforeinstallprompt", event => {
        event.preventDefault();
        deferredPrompt = event;
        root.classList.add("gaska-can-install");
    });

    window.addEventListener("appinstalled", () => {
        deferredPrompt = null;
        root.classList.remove("gaska-can-install");
    });

    window.gaskaInstall = {
        info() {
            const ua = navigator.userAgent;
            return {
                standalone,
                canPrompt: deferredPrompt !== null,
                ios: /iphone|ipad|ipod/i.test(ua) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1),
                android: /android/i.test(ua),
            };
        },
        async prompt() {
            if (!deferredPrompt) {
                return "unavailable";
            }

            deferredPrompt.prompt();
            const choice = await deferredPrompt.userChoice;
            deferredPrompt = null;
            root.classList.remove("gaska-can-install");
            return choice.outcome;
        },
        dismiss() {
            try {
                localStorage.setItem("gaska-install-dismissed", "1");
            } catch {
                // ignore
            }

            root.classList.add("gaska-install-dismissed");
        },
    };

    // "Nie teraz" on the home-page banner (plain button – works before Blazor is interactive too).
    document.addEventListener("click", event => {
        if (event.target instanceof Element && event.target.closest("[data-install-dismiss]")) {
            window.gaskaInstall.dismiss();
        }
    });
})();

// User manual (/instrukcja): jump to a chapter and mark the chapter being read in the table of contents.
// The current chapter = the last one whose heading has passed the top of the screen (below the app bar).
window.gaskaManual = (function () {
    let sections = [];
    let ticking = false;
    // While the page glides to a chapter picked in the table of contents, that chapter stays marked –
    // the chapters scrolled past on the way are not highlighted one by one.
    let target = null;
    let settleTimer = 0;

    function mark(id) {
        document.querySelectorAll("[data-manual-target]").forEach(link => {
            const active = link.getAttribute("data-manual-target") === id;
            link.classList.toggle("is-active", active);
            // Phones: keep the active chip visible in the horizontal chapter bar.
            if (active && link.classList.contains("gaska-manual-chip")) {
                const bar = link.parentElement;
                bar.scrollTo({ left: link.offsetLeft - (bar.clientWidth - link.offsetWidth) / 2, behavior: "smooth" });
            }
        });
    }

    function update() {
        ticking = false;
        if (sections.length === 0) {
            return;
        }

        // A chapter becomes current once its heading reaches the upper third of the screen.
        const line = Math.max(180, window.innerHeight * 0.3);
        let current = sections[0];
        for (const section of sections) {
            if (section.getBoundingClientRect().top - line <= 0) {
                current = section;
            }
        }

        // At the very bottom the last chapter may never reach the line – mark it anyway.
        if (window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 4) {
            current = sections[sections.length - 1];
        }

        if (current.id !== window.gaskaManual.current) {
            window.gaskaManual.current = current.id;
            mark(current.id);
        }
    }

    // The picked chapter stays marked after landing (a short chapter near the end may never reach the reading line);
    // the next scroll by the reader works it out again.
    function release() {
        clearTimeout(settleTimer);
        target = null;
    }

    function onScroll() {
        if (target) {
            // No "scrollend" in every browser: treat 150 ms without scrolling as the end of the glide.
            clearTimeout(settleTimer);
            settleTimer = setTimeout(release, 150);
            return;
        }

        if (!ticking) {
            ticking = true;
            requestAnimationFrame(update);
        }
    }

    function start() {
        stop();
        sections = [...document.querySelectorAll(".manual > section[id]")];
        window.addEventListener("scroll", onScroll, { passive: true });
        window.addEventListener("scrollend", onScrollEnd);
        // The reader takes over (wheel, touch, keys) – stop holding the picked chapter.
        ["wheel", "touchstart", "keydown"].forEach(e => window.addEventListener(e, onUserScroll, { passive: true }));
        window.gaskaManual.current = null;
        update();
    }

    function stop() {
        window.removeEventListener("scroll", onScroll);
        window.removeEventListener("scrollend", onScrollEnd);
        ["wheel", "touchstart", "keydown"].forEach(e => window.removeEventListener(e, onUserScroll));
        clearTimeout(settleTimer);
        target = null;
        sections = [];
    }

    function go(id) {
        const section = document.getElementById(id);
        if (!section) {
            return;
        }

        target = id;
        window.gaskaManual.current = id;
        mark(id);
        clearTimeout(settleTimer);
        // If the page cannot move (already there), no scroll event comes – release anyway.
        settleTimer = setTimeout(release, 400);
        section.scrollIntoView({ behavior: "smooth", block: "start" });
    }

    function onScrollEnd() {
        if (target) {
            release();
        }
    }

    function onUserScroll() {
        if (target) {
            release();
        }
    }

    return { start, stop, go, update, current: null };
})();
