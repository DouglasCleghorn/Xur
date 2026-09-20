(() => {
    const logs = document.getElementById('install-logs');
    if (!logs) return;
    const connection = document.getElementById('log-connection');
    logs.scrollTop = logs.scrollHeight;
    async function refresh() {
        if(document.hidden){setTimeout(refresh,2000);return;}
        try {
            const responses = await Promise.all([(window.xurFetch ?? window.fetch)('/api/installer'), (window.xurFetch ?? window.fetch)('/api/logs')]);
            if (responses.some(r => r.status === 401)) { location.assign('/login'); return; }
            if (responses.some(r => !r.ok)) throw new Error();
            const [state, text] = await Promise.all([responses[0].json(), responses[1].text()]);
            const follow = logs.scrollHeight - logs.scrollTop - logs.clientHeight < 40;
            if (logs.textContent !== text) { logs.textContent = text; if (follow) logs.scrollTop = logs.scrollHeight; }
            const op = state.operation;
            document.getElementById('operation-stage').textContent = op?.stage ?? 'Not started';
            document.getElementById('operation-message').textContent = op?.message ?? 'Select a disk to begin installation.';
            document.getElementById('operation-id').textContent = op?.id ?? '—';
            document.getElementById('operation-updated').textContent = op ? new Date(op.updated).toLocaleString() : '—';
            document.getElementById('reboot-form').hidden = op?.stage !== 'Complete';
            connection.textContent = '';
        } catch { connection.textContent = 'Reconnecting to logs…'; }
        setTimeout(refresh, 2000);
    }
    refresh();
})();
