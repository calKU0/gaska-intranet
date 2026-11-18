// Drag-and-drop ordering of dashboard charts (mouse, touch and pen – pointer events).
//
// A cell is dragged by its handle ([data-drag-handle]). While dragging, the cell takes the place of the cell under the
// pointer, so the page shows the result at every moment. On drop the new order goes to the server.
//
// Blazor owns the DOM: it must find the cells where it left them. So on drop the DOM order is put back and the new order
// is kept on screen with CSS `order` until Blazor has rendered it (then `settle` removes the inline styles).

const grids = new WeakMap();

export function init(grid, dotnet) {
    dispose(grid);

    let drag = null;
    let lastMove = 0;
    let scroller = null;

    const cells = () => [...grid.children].filter(c => c.dataset.widgetId);

    function onDown(e) {
        const handle = e.target.closest('[data-drag-handle]');
        if (!handle || drag || (e.pointerType === 'mouse' && e.button !== 0)) {
            return;
        }

        const cell = handle.closest('[data-widget-id]');
        if (!cell || cell.parentElement !== grid) {
            return;
        }

        e.preventDefault();
        settle(grid);
        drag = { cell, handle, pointerId: e.pointerId, original: [...grid.children], y: e.clientY, moved: false };
        handle.setPointerCapture(e.pointerId);
        cell.classList.add('is-dragging');
        grid.classList.add('is-sorting');
        scroller = requestAnimationFrame(autoScroll);
    }

    function onMove(e) {
        if (!drag || e.pointerId !== drag.pointerId) {
            return;
        }

        drag.y = e.clientY;

        // A short pause after each move: the layout has just changed under the pointer.
        const now = performance.now();
        if (now - lastMove < 120) {
            return;
        }

        const over = document.elementFromPoint(e.clientX, e.clientY)?.closest('[data-widget-id]');
        if (!over || over === drag.cell || over.parentElement !== grid) {
            return;
        }

        // Moving down the page: go after the cell under the pointer; moving up: before it.
        const order = cells();
        const after = order.indexOf(drag.cell) < order.indexOf(over);
        grid.insertBefore(drag.cell, after ? over.nextSibling : over);
        drag.moved = true;
        lastMove = now;
    }

    function onUp(e) {
        if (!drag || e.pointerId !== drag.pointerId) {
            return;
        }

        const { cell, handle, original, moved } = drag;
        drag = null;
        cancelAnimationFrame(scroller);
        if (handle.hasPointerCapture(e.pointerId)) {
            handle.releasePointerCapture(e.pointerId);
        }

        cell.classList.remove('is-dragging');
        grid.classList.remove('is-sorting');

        const ids = cells().map(c => Number(c.dataset.widgetId));
        if (!moved || e.type === 'pointercancel') {
            original.forEach(c => grid.appendChild(c));
            return;
        }

        // Keep the new order on screen, give Blazor its DOM order back, then save.
        cells().forEach((c, i) => { c.style.order = i; });
        original.forEach(c => grid.appendChild(c));
        dotnet.invokeMethodAsync('OnReordered', ids).catch(() => settle(grid));
    }

    // Dragging near the top or bottom edge scrolls the page.
    function autoScroll() {
        if (!drag) {
            return;
        }

        const edge = 72;
        const speed = drag.y < edge ? -(edge - drag.y) / 5 : drag.y > innerHeight - edge ? (drag.y - (innerHeight - edge)) / 5 : 0;
        if (speed) {
            scrollTarget(grid).scrollBy(0, speed);
        }

        scroller = requestAnimationFrame(autoScroll);
    }

    grid.addEventListener('pointerdown', onDown);
    grid.addEventListener('pointermove', onMove);
    grid.addEventListener('pointerup', onUp);
    grid.addEventListener('pointercancel', onUp);
    grids.set(grid, { onDown, onMove, onUp });
}

// Blazor has rendered the saved order – the temporary visual order is no longer needed.
export function settle(grid) {
    if (grid) {
        [...grid.children].forEach(c => { c.style.order = ''; });
    }
}

export function dispose(grid) {
    const handlers = grid && grids.get(grid);
    if (!handlers) {
        return;
    }

    grid.removeEventListener('pointerdown', handlers.onDown);
    grid.removeEventListener('pointermove', handlers.onMove);
    grid.removeEventListener('pointerup', handlers.onUp);
    grid.removeEventListener('pointercancel', handlers.onUp);
    grids.delete(grid);
}

// The nearest ancestor that scrolls, else the window.
function scrollTarget(element) {
    for (let node = element.parentElement; node && node !== document.body; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight) {
            return node;
        }
    }

    return window;
}
