// Tab bars marked "gaska-tabs-scroll" scroll freely instead of with MudBlazor's arrow buttons: a finger swipes them
// natively (CSS overflow), a mouse can drag them or turn the wheel, and the active tab is brought into view.
// Listeners are delegated on the document, so tab bars Blazor renders later work too.
(function () {
    const BAR = ".gaska-tabs-scroll .mud-tabs-tabbar-content";
    const DRAG_THRESHOLD = 6;
    let drag = null;

    const overflows = bar => bar.scrollWidth > bar.clientWidth + 1;

    // A vertical wheel scrolls the bar sideways – only while it can still move, so the page scrolls at the ends.
    document.addEventListener("wheel", event => {
        const bar = event.target instanceof Element ? event.target.closest(BAR) : null;
        if (!bar || !overflows(bar) || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
            return;
        }

        const max = bar.scrollWidth - bar.clientWidth;
        if ((event.deltaY < 0 && bar.scrollLeft <= 0) || (event.deltaY > 0 && bar.scrollLeft >= max - 1)) {
            return;
        }

        bar.scrollLeft += event.deltaY;
        event.preventDefault();
    }, { passive: false });

    // Dragging with a mouse (touch already scrolls natively).
    document.addEventListener("pointerdown", event => {
        if (event.pointerType !== "mouse" || event.button !== 0 || !(event.target instanceof Element)) {
            return;
        }

        const bar = event.target.closest(BAR);
        if (bar && overflows(bar)) {
            drag = { bar, x: event.clientX, left: bar.scrollLeft, moved: false };
        }
    });

    document.addEventListener("pointermove", event => {
        if (!drag) {
            return;
        }

        const dx = event.clientX - drag.x;
        if (!drag.moved && Math.abs(dx) < DRAG_THRESHOLD) {
            return;
        }

        drag.moved = true;
        drag.bar.classList.add("is-dragging");
        drag.bar.scrollLeft = drag.left - dx;
    });

    const endDrag = () => {
        if (!drag) {
            return;
        }

        const { bar, moved } = drag;
        drag = null;
        bar.classList.remove("is-dragging");
        if (moved) {
            draggedAt = performance.now();
        }
    };

    // The release after a drag is not a click on the tab under the pointer.
    let draggedAt = -Infinity;
    document.addEventListener("click", event => {
        if (performance.now() - draggedAt < 100) {
            event.stopPropagation();
            event.preventDefault();
        }
    }, true);

    document.addEventListener("pointerup", endDrag);
    document.addEventListener("pointercancel", endDrag);

    // The line under the active tab (CSS draws it from these variables on the tab bar). A clicked tab gets it at once,
    // before the server answers; Blazor rewrites the class of both tabs when it does, which removes the mark.
    const SLIDE = "transform .3s cubic-bezier(.64, .09, .08, 1), width .3s cubic-bezier(.64, .09, .08, 1)";

    function placeLine(root) {
        const tab = root.querySelector(".mud-tab.is-pending") ?? root.querySelector(".mud-tab.mud-tab-active");
        if (!tab || !tab.offsetWidth) {
            return;
        }

        const wrapper = root.querySelector(".mud-tabs-tabbar-wrapper");
        const left = tab.getBoundingClientRect().left - wrapper.getBoundingClientRect().left;
        root.style.setProperty("--gaska-tab-left", `${left}px`);
        root.style.setProperty("--gaska-tab-width", `${tab.offsetWidth}px`);

        // The first placement does not slide in from the left edge.
        if (!root.dataset.lineReady) {
            root.dataset.lineReady = "1";
            requestAnimationFrame(() => root.style.setProperty("--gaska-tab-transition", SLIDE));
        }
    }

    let scheduled = false;
    function placeAll() {
        if (scheduled) {
            return;
        }

        scheduled = true;
        requestAnimationFrame(() => {
            scheduled = false;
            document.querySelectorAll(".gaska-tabs-scroll").forEach(placeLine);
        });
    }

    new MutationObserver(placeAll).observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ["class"] });
    window.addEventListener("resize", placeAll);
    document.fonts?.ready.then(placeAll);

    // A tab chosen near an edge slides fully into view.
    document.addEventListener("click", event => {
        const tab = event.target instanceof Element ? event.target.closest(".gaska-tabs-scroll .mud-tab") : null;
        const bar = tab?.closest(BAR);
        if (!tab || !bar || tab.classList.contains("mud-disabled")) {
            return;
        }

        if (!tab.classList.contains("mud-tab-active")) {
            bar.querySelectorAll(".mud-tab.is-pending").forEach(t => t.classList.remove("is-pending"));
            tab.classList.add("is-pending");
            const root = tab.closest(".gaska-tabs-scroll");
            if (root) {
                placeLine(root);
            }
        }

        if (!overflows(bar)) {
            return;
        }

        const tabRect = tab.getBoundingClientRect();
        const barRect = bar.getBoundingClientRect();
        if (tabRect.left < barRect.left) {
            bar.scrollBy({ left: tabRect.left - barRect.left - 24, behavior: "smooth" });
        } else if (tabRect.right > barRect.right) {
            bar.scrollBy({ left: tabRect.right - barRect.right + 24, behavior: "smooth" });
        }
    });
})();
