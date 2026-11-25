// Users' own order of the camera tiles: drag a tile by its handle (mouse or touch), or focus the handle and use the
// arrow keys. The page (CamerasPage.MoveCameraAsync) saves the order and renders the tiles in it – this script only
// follows the pointer and shows where the tile will land.

let page = null;
let drag = null;

const GRID = ".gaska-camera-grid[data-camera-location]";
const TILE = ".gaska-camera-tile[data-camera-id]";
const EDGE = 64; // px from the window edge where dragging scrolls the page

export function init(dotNetRef) {
    page = dotNetRef;
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
}

export function dispose() {
    cancel();
    document.removeEventListener("pointerdown", onPointerDown);
    document.removeEventListener("keydown", onKeyDown);
    page = null;
}

function tilesOf(grid) {
    return [...grid.querySelectorAll(TILE)];
}

function move(grid, tile, index) {
    page?.invokeMethodAsync("MoveCameraAsync", grid.dataset.cameraLocation, tile.dataset.cameraId, index)
        .catch(() => { /* page gone */ });
}

function onKeyDown(event) {
    const handle = event.target.closest?.("[data-camera-drag]");
    if (!handle || !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) {
        return;
    }

    const tile = handle.closest(TILE);
    const grid = tile?.closest(GRID);
    if (!grid) {
        return;
    }

    event.preventDefault();
    const tiles = tilesOf(grid);
    const index = tiles.indexOf(tile);
    const target = event.key === "Home" ? 0
        : event.key === "End" ? tiles.length - 1
        : event.key === "ArrowLeft" || event.key === "ArrowUp" ? index - 1 : index + 1;
    if (target >= 0 && target < tiles.length && target !== index) {
        move(grid, tile, target);
    }
}

function onPointerDown(event) {
    const handle = event.target.closest?.("[data-camera-drag]");
    if (!handle || event.button > 0) {
        return;
    }

    const tile = handle.closest(TILE);
    const grid = tile?.closest(GRID);
    if (!grid) {
        return;
    }

    event.preventDefault();
    handle.setPointerCapture?.(event.pointerId);
    const rect = tile.getBoundingClientRect();
    drag = {
        handle, tile, grid,
        pointerId: event.pointerId,
        startX: event.clientX, startY: event.clientY,
        x: event.clientX, y: event.clientY,
        // One column (phone): the drop place is above / below a tile; otherwise left / right of it.
        vertical: rect.width > grid.getBoundingClientRect().width * 0.75,
        target: null, after: false,
        scrolled: scrolledSince(window.scrollY),
        scrollTimer: setInterval(autoScroll, 30),
    };
    tile.classList.add("is-dragging");
    document.body.classList.add("gaska-camera-sorting");
    handle.addEventListener("pointermove", onPointerMove);
    handle.addEventListener("pointerup", onPointerUp);
    handle.addEventListener("pointercancel", cancel);
    handle.addEventListener("lostpointercapture", onPointerUp);
}

function onPointerMove(event) {
    if (!drag || event.pointerId !== drag.pointerId) {
        return;
    }

    drag.x = event.clientX;
    drag.y = event.clientY;
    drag.tile.style.transform = `translate(${drag.x - drag.startX}px, ${drag.y - drag.startY + drag.scrolled()}px)`;
    findTarget();
}

// Scrolling while dragging moves the tile with the page – keep it under the finger.
function scrolledSince(start) {
    return () => start - window.scrollY;
}

function findTarget() {
    // The dragged tile is under the pointer itself – look through it.
    const under = document.elementsFromPoint(drag.x, drag.y).map(e => e.closest(TILE)).find(t => t && t !== drag.tile);
    clearMarker();
    if (!under || under === drag.tile || under.closest(GRID) !== drag.grid) {
        drag.target = null;
        return;
    }

    const rect = under.getBoundingClientRect();
    drag.target = under;
    drag.after = drag.vertical ? drag.y > rect.top + rect.height / 2 : drag.x > rect.left + rect.width / 2;
    under.classList.add(drag.after ? "drop-after" : "drop-before", drag.vertical ? "drop-y" : "drop-x");
}

function clearMarker() {
    drag?.grid.querySelectorAll(".drop-before, .drop-after").forEach(t => t.classList.remove("drop-before", "drop-after", "drop-x", "drop-y"));
}

function autoScroll() {
    if (!drag) {
        return;
    }

    const step = drag.y < EDGE ? -12 : drag.y > window.innerHeight - EDGE ? 12 : 0;
    if (step !== 0) {
        window.scrollBy(0, step);
        drag.tile.style.transform = `translate(${drag.x - drag.startX}px, ${drag.y - drag.startY + drag.scrolled()}px)`;
        findTarget();
    }
}

function onPointerUp(event) {
    if (!drag || (event.pointerId !== undefined && event.pointerId !== drag.pointerId)) {
        return;
    }

    const { grid, tile, target, after } = drag;
    cancel();
    if (!target) {
        return;
    }

    // The index among the other tiles (the dragged one taken out).
    const others = tilesOf(grid).filter(t => t !== tile);
    const index = others.indexOf(target) + (after ? 1 : 0);
    move(grid, tile, index);
}

function cancel() {
    if (!drag) {
        return;
    }

    const { handle, tile, scrollTimer } = drag;
    clearInterval(scrollTimer);
    clearMarker();
    handle.removeEventListener("pointermove", onPointerMove);
    handle.removeEventListener("pointerup", onPointerUp);
    handle.removeEventListener("pointercancel", cancel);
    handle.removeEventListener("lostpointercapture", onPointerUp);
    tile.classList.remove("is-dragging");
    tile.style.transform = "";
    document.body.classList.remove("gaska-camera-sorting");
    drag = null;
}
