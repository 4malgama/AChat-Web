const scrollers = new WeakMap();

export function syncScroll(element, conversationKey, revision) {
    if (!element) return;
    let state = scrollers.get(element);
    if (!state) {
        state = { key: null, revision: -1, nearBottom: true };
        scrollers.set(element, state);
        element.addEventListener('scroll', () => {
            state.nearBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 100;
        }, { passive: true });
    }
    if (state.key !== conversationKey || (state.revision !== revision && state.nearBottom)) {
        element.scrollTop = element.scrollHeight;
        state.nearBottom = true;
    }
    state.key = conversationKey;
    state.revision = revision;
}

export function scrollToEnd(element) {
    if (element) element.scrollTop = element.scrollHeight;
}

export function safeFileName(value) {
    const name = String(value || 'attachment').split(/[\\/]/).pop()
        .replace(/[\u0000-\u001f\u007f<>:"|?*\u202a-\u202e\u2066-\u2069]/g, '_')
        .replace(/[. ]+$/g, '').slice(0, 180);
    return name && name !== '.' && name !== '..' ? name : 'attachment';
}

export async function saveFile(name, streamReference) {
    const buffer = await streamReference.arrayBuffer();
    // Do not render HTML, SVG, PDFs or images from attachments in the chat origin.
    const blob = new Blob([buffer], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = safeFileName(name);
    anchor.rel = 'noopener';
    anchor.hidden = true;
    document.body.appendChild(anchor);
    try { anchor.click(); }
    finally {
        anchor.remove();
        // Keep the URL alive long enough for Firefox to start the download.
        setTimeout(() => URL.revokeObjectURL(url), 60000);
    }
}
