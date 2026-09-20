(() => {
    const panel = document.getElementById('application-update');
    if (!panel) return;
    const os = document.getElementById('os-update');
    let disconnected = false, busy = panel.dataset.busy === 'true', osBusy = os?.dataset.busy === 'true';
    const reload = () => {
        if (!document.activeElement?.matches('input,textarea,select')) location.reload();
    };
    setInterval(async () => {
        if(document.hidden)return;
        try {
            const response = await (window.xurFetch ?? window.fetch)('/api/application-updates');
            if (!response.ok) return;
            const state = await response.json();
            if (disconnected || state.current.id !== panel.dataset.current ||
                !state.busy && (busy || (state.operation?.id || '') !== (panel.dataset.operation || ''))) {
                reload(); return;
            }
            busy = busy || state.busy;
            const progress = panel.querySelector('[role="status"]');
            if (progress && state.operation) progress.textContent = state.operation.stage + ' · ' + state.operation.message;
            if (os) {
                const response = await (window.xurFetch ?? window.fetch)('/api/updates');
                if (!response.ok) return;
                const state = await response.json();
                if (!state.busy && (osBusy || (state.operation?.id || '') !== (os.dataset.operation || ''))) reload();
                osBusy = osBusy || state.busy;
            }
        } catch { disconnected = true; }
    }, 3000);
})();
