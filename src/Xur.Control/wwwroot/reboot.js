(() => {
    const status = document.getElementById('reboot-status');
    if (!status) return;
    async function wait() {
        try {
            const response = await (window.xurFetch ?? window.fetch)('/api/status', {cache: 'no-store', signal: AbortSignal.timeout(4000)});
            if (response.ok) {
                const state = await response.json();
                if (state.bootId && state.bootId !== status.dataset.bootId) { location.replace('/'); return; }
            }
        } catch { /* The server is expected to be unreachable during reboot. */ }
        setTimeout(wait, 1500);
    }
    wait();
})();
