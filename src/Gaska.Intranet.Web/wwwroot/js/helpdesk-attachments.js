// Helpdesk attachments: files pasted (Ctrl+V – e.g. a screenshot) or dropped onto the text box are handed to the
// component's hidden <input type="file">, so Blazor's InputFile reads them the same way as files picked from disk.
// Text on the clipboard always wins: copying from Excel puts both text and a picture there, and the user wants the text.

function extensionOf(type) {
    const ext = (type.split("/")[1] || "png").toLowerCase();
    return ext === "jpeg" ? "jpg" : ext;
}

function handOver(input, fileList) {
    const files = [...fileList];
    if (files.length === 0) {
        return false;
    }

    const now = new Date();
    const pad = n => String(n).padStart(2, "0");
    const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
    const transfer = new DataTransfer();
    files.forEach((file, i) => {
        // Browsers call every pasted picture "image.png" – give it a name that tells screenshots apart.
        const generic = !file.name || /^image\.\w+$/i.test(file.name);
        const name = generic ? `zrzut-ekranu-${stamp}${files.length > 1 ? "-" + (i + 1) : ""}.${extensionOf(file.type)}` : file.name;
        transfer.items.add(generic ? new File([file], name, { type: file.type }) : file);
    });

    input.files = transfer.files;
    input.dispatchEvent(new Event("change", { bubbles: true }));
    return true;
}

export function attach(container, input) {
    if (!container || !input || container._gaskaAttachments) {
        return;
    }

    const onPaste = event => {
        const data = event.clipboardData;
        if (!data || data.files.length === 0 || data.getData("text/plain")) {
            return;
        }

        if (handOver(input, data.files)) {
            event.preventDefault();
        }
    };

    const onDragOver = event => {
        if ([...(event.dataTransfer?.types ?? [])].includes("Files")) {
            event.preventDefault();
            container.classList.add("is-dragging");
        }
    };

    const onDragLeave = () => container.classList.remove("is-dragging");

    const onDrop = event => {
        container.classList.remove("is-dragging");
        if (event.dataTransfer && handOver(input, event.dataTransfer.files)) {
            event.preventDefault();
        }
    };

    container.addEventListener("paste", onPaste);
    container.addEventListener("dragover", onDragOver);
    container.addEventListener("dragleave", onDragLeave);
    container.addEventListener("drop", onDrop);
    container._gaskaAttachments = { onPaste, onDragOver, onDragLeave, onDrop };
}

export function detach(container) {
    const handlers = container?._gaskaAttachments;
    if (!handlers) {
        return;
    }

    container.removeEventListener("paste", handlers.onPaste);
    container.removeEventListener("dragover", handlers.onDragOver);
    container.removeEventListener("dragleave", handlers.onDragLeave);
    container.removeEventListener("drop", handlers.onDrop);
    delete container._gaskaAttachments;
}
