(() => {
    const notice = document.getElementById('update-window-notice');
    if (!notice) return;
    const form = notice.querySelector('form');
    const message = notice.querySelector('[data-update-window-message]');
    const id = form.elements.windowId;
    const button = form.querySelector('button');
    let busy = false;
    let skippedId = null;
    async function refresh() {
        if (busy) return;
        busy = true;
        try {
            const response = await (window.xurFetch ?? window.fetch)('/api/updates', { cache: 'no-store' });
            if (!response.ok) return;
            const status = await response.json();
            const upcoming = status.automatic && status.window?.state === 'Waiting' && status.window.id !== skippedId ? status.window : null;
            notice.hidden = !upcoming;
            if (upcoming) {
                id.value = upcoming.id;
                message.textContent = `OS update ${upcoming.version} will be staged at ${new Date(upcoming.startsAt * 1000).toLocaleString()} (your local time). Reboot remains manual.`;
            }
        } catch { /* Preserve the notice while reconnecting; the server validates skips. */ }
        finally { busy = false; }
    }
    form.addEventListener('submit', async event => {
        event.preventDefault();
        button.disabled = true;
        const selectedId = id.value;
        try {
            const response = await (window.xurFetch ?? window.fetch)(form.action, { method: 'POST', body: new FormData(form) });
            if (response.ok) { skippedId = selectedId; notice.hidden = true; }
            else {
                const result = await response.json().catch(() => ({}));
                message.textContent = result.error || 'Could not skip this window. Refresh and retry.';
            }
        } catch { message.textContent = 'Connection interrupted. Refresh status before retrying.'; }
        finally { button.disabled = false; }
    });
    refresh();
    setInterval(refresh, 5000);
})();
