// Copying a value (phone, e-mail, NIP…) to the clipboard – used by the CopyButton component.
// navigator.clipboard works only on HTTPS / localhost; elsewhere (plain-HTTP intranet address) a hidden textarea
// with execCommand("copy") does the job. Returns true when the text landed on the clipboard.
window.gaskaClipboard = {
    copy: async function (text) {
        if (navigator.clipboard && window.isSecureContext) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
                // fall through to the legacy way (e.g. permission denied)
            }
        }

        const area = document.createElement("textarea");
        area.value = text;
        area.setAttribute("readonly", "");
        area.style.position = "fixed";
        area.style.top = "0";
        area.style.left = "0";
        area.style.opacity = "0";
        area.style.fontSize = "16px"; // no zoom on iOS when the textarea gets focus
        // Inside an open MudDialog the focus trap would steal focus from an element outside it.
        const host = document.activeElement?.closest(".mud-dialog") ?? document.body;
        host.appendChild(area);
        area.select();
        area.setSelectionRange(0, text.length);
        let ok = false;
        try {
            ok = document.execCommand("copy");
        } catch {
            ok = false;
        }
        area.remove();
        return ok;
    }
};
