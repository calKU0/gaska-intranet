// Business card camera: on a phone or tablet, tapping an element marked data-gaska-camera opens the camera inside the
// page (full screen, a frame for the card, one big button) instead of a file dialog. Asking for camera access, and
// explaining how to allow it when it was refused, happens here. The photo is handed to the <input type="file"> inside
// that element, so the Blazor InputFile reads it exactly like a picked file.
// Computers (mouse) keep the normal file dialog; without a camera, the file dialog opens as well.
(function () {
    let bypass = false;
    const CAMERA_ICON = '<svg viewBox="0 0 24 24" width="40" height="40" aria-hidden="true"><path fill="currentColor" d="M9 3 7.17 5H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-3.17L15 3H9zm3 15a5 5 0 1 1 0-10 5 5 0 0 1 0 10zm0-2a3 3 0 1 0 0-6 3 3 0 0 0 0 6z"/></svg>';

    const isTouch = () => window.matchMedia("(pointer: coarse)").matches;
    const canUseCamera = () => !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia) && window.isSecureContext;
    const isIos = () => /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

    document.addEventListener("click", event => {
        const trigger = event.target.closest("[data-gaska-camera]");
        if (!trigger || bypass || !isTouch() || !canUseCamera()) {
            return;
        }

        const input = trigger.querySelector("input[type=file]");
        if (!input || input.disabled) {
            return;
        }

        event.preventDefault();
        open(input);
    }, true);

    /** Opens the file dialog of the input (gallery / files) – called from a tap, so the browser allows it. */
    function pickFile(input) {
        bypass = true;
        try {
            input.click();
        } finally {
            bypass = false;
        }
    }

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text) node.textContent = text;
        return node;
    }

    function button(text, className, onClick) {
        const node = el("button", "gaska-camera-button " + (className || ""), text);
        node.type = "button";
        node.addEventListener("click", onClick);
        return node;
    }

    async function cameraState() {
        try {
            const status = await navigator.permissions.query({ name: "camera" });
            return status.state; // granted | prompt | denied
        } catch {
            return "unknown"; // e.g. Firefox – just ask
        }
    }

    function deniedHelp() {
        return isIos()
            ? ["Otwórz Ustawienia telefonu → Safari → Aparat i wybierz „Pytaj” albo „Pozwalaj”.",
               "Wróć tutaj i spróbuj ponownie."]
            : ["Dotknij ikony obok adresu strony (kłódka lub suwaki) → Uprawnienia → Aparat → Zezwól.",
               "Jeśli to nie pomoże: Ustawienia telefonu → Aplikacje → przeglądarka (np. Chrome) → Uprawnienia → Aparat → Zezwól."];
    }

    async function open(input) {
        const overlay = el("div", "gaska-camera");
        overlay.setAttribute("role", "dialog");
        overlay.setAttribute("aria-modal", "true");
        overlay.setAttribute("aria-label", "Zdjęcie wizytówki");

        const stage = el("div", "gaska-camera-stage");
        const video = el("video", "gaska-camera-video");
        video.playsInline = true;
        video.muted = true;
        video.setAttribute("playsinline", "");
        const frame = el("div", "gaska-camera-frame");
        const hint = el("div", "gaska-camera-hint", "Ułóż wizytówkę w ramce");
        stage.append(video, frame, hint);

        const message = el("div", "gaska-camera-message");
        const bar = el("div", "gaska-camera-bar");
        overlay.append(stage, message, bar);
        document.body.append(overlay);
        document.documentElement.classList.add("gaska-camera-open");

        let stream = null;

        function close() {
            stream?.getTracks().forEach(t => t.stop());
            overlay.remove();
            document.documentElement.classList.remove("gaska-camera-open");
            document.removeEventListener("keydown", onKey);
        }

        function onKey(e) {
            if (e.key === "Escape") close();
        }
        document.addEventListener("keydown", onKey);

        const cancel = button("Anuluj", "is-text", close);
        const gallery = button("Z galerii", "is-text", () => {
            close();
            pickFile(input);
        });

        function showMessage(title, lines, actions) {
            stage.hidden = true;
            const icon = el("div", "gaska-camera-icon");
            icon.innerHTML = CAMERA_ICON;
            message.replaceChildren(icon, el("h2", null, title));
            for (const line of lines) message.append(el("p", null, line));
            message.hidden = false;
            bar.replaceChildren(...actions);
        }

        async function start() {
            message.hidden = true;
            stage.hidden = false;
            bar.replaceChildren(cancel, el("span", "gaska-camera-spacer"), gallery);
            try {
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: { ideal: "environment" }, width: { ideal: 2560 }, height: { ideal: 1920 } },
                    audio: false,
                });
            } catch (error) {
                handleError(error);
                return;
            }

            video.srcObject = stream;
            await video.play().catch(() => { });
            const shutter = button("", "gaska-camera-shutter", takePhoto);
            shutter.setAttribute("aria-label", "Zrób zdjęcie");
            bar.replaceChildren(cancel, shutter, gallery);
            shutter.focus();
        }

        function handleError(error) {
            const name = error && error.name;
            if (name === "NotAllowedError" || name === "SecurityError") {
                showMessage("Brak dostępu do aparatu", ["Intranet potrzebuje aparatu, żeby zrobić zdjęcie wizytówki.", ...deniedHelp()],
                    [cancel, button("Spróbuj ponownie", "is-primary", start), gallery]);
            } else if (name === "NotFoundError" || name === "OverconstrainedError") {
                close();
                pickFile(input); // no camera – pick a photo instead
            } else {
                showMessage("Aparat jest zajęty", ["Zamknij inne aplikacje, które używają aparatu, i spróbuj ponownie."],
                    [cancel, button("Spróbuj ponownie", "is-primary", start), gallery]);
            }
        }

        function takePhoto() {
            const width = video.videoWidth;
            const height = video.videoHeight;
            if (!width || !height) return;

            const canvas = document.createElement("canvas");
            canvas.width = width;
            canvas.height = height;
            canvas.getContext("2d").drawImage(video, 0, 0, width, height);
            canvas.toBlob(blob => {
                if (!blob) return;
                close();
                const file = new File([blob], "wizytowka.jpg", { type: "image/jpeg", lastModified: Date.now() });
                const transfer = new DataTransfer();
                transfer.items.add(file);
                input.files = transfer.files;
                input.dispatchEvent(new Event("change", { bubbles: true }));
            }, "image/jpeg", 0.92);
        }

        // Before asking: explain why, so the browser's question does not come out of nowhere.
        const state = await cameraState();
        if (state === "denied") {
            showMessage("Brak dostępu do aparatu", ["Intranet potrzebuje aparatu, żeby zrobić zdjęcie wizytówki.", ...deniedHelp()],
                [cancel, button("Spróbuj ponownie", "is-primary", start), gallery]);
        } else if (state === "prompt") {
            showMessage("Zezwól na dostęp do aparatu",
                ["Za chwilę przeglądarka zapyta o dostęp do aparatu – wybierz „Zezwól”.", "Zdjęcie służy tylko do odczytania wizytówki."],
                [cancel, button("Dalej", "is-primary", start), gallery]);
        } else {
            start();
        }
    }
})();
