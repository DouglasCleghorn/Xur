// Unit-test notification polling and request races without a browser or host.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const script = fs.readFileSync('src/Xur.Control/wwwroot/update-window.js', 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));

(async () => {
    const message = { textContent: '' }, button = { disabled: false }, id = { value: '' };
    let submit, tick, status = { automatic: true, window: { id: '123', startsAt: 1800000000, version: '45', state: 'Waiting' } };
    let rejectSkip = false, offline = false, pendingGet = null;
    const posts = [];
    const form = { action: '/updates/action', elements: { windowId: id },
        querySelector: () => button, addEventListener: (_, callback) => { submit = callback; } };
    const notice = { hidden: true, querySelector: selector => selector === 'form' ? form : message };
    const fetch = async (_, options = {}) => {
        if (offline) throw Error('Connection lost');
        if (options.method === 'POST') {
            posts.push(options.body.windowId);
            if (rejectSkip) return { ok: false, json: async () => ({ error: 'This window has ended or changed.' }) };
            status = { automatic: true, window: null };
            return { ok: true };
        }
        if (pendingGet) return new Promise(resolve => { pendingGet.resolve = resolve; });
        return { ok: true, json: async () => status };
    };
    vm.runInNewContext(script, { window: { fetch }, document: { getElementById: () => notice },
        FormData: class { constructor() { this.windowId = id.value; } }, Date,
        setInterval: callback => { tick = callback; } });
    await flush();
    assert.equal(notice.hidden, false); assert.equal(id.value, '123'); assert.match(message.textContent, /45.*will be staged/);
    status = { automatic: true, window: null }; await tick(); assert.equal(notice.hidden, true);
    status = { automatic: false, window: { id: '456', state: 'Waiting' } }; await tick(); assert.equal(notice.hidden, true);
    status = { automatic: true, window: { id: '456', startsAt: 1800000000, version: '46', state: 'Waiting' } };
    await tick(); rejectSkip = true;
    await submit({ preventDefault() {} });
    assert.match(message.textContent, /window has ended/); assert.equal(button.disabled, false); assert.equal(notice.hidden, false);
    rejectSkip = false; await tick();
    // An earlier GET finishes after a successful skip and returns the old window.
    pendingGet = {}; const polling = tick();
    await submit({ preventDefault() {} }); assert.equal(notice.hidden, true); assert.equal(status.automatic, true);
    pendingGet.resolve({ ok: true, json: async () => ({ automatic: true, window: { id: '456', startsAt: 1800000000, version: '46', state: 'Waiting' } }) });
    await polling; assert.equal(notice.hidden, true, 'A stale poll must not resurrect a skipped notice');
    pendingGet = null;
    status = { automatic: true, window: { id: '789', startsAt: 1800000000, version: '47', state: 'Waiting' } };
    await tick(); assert.equal(notice.hidden, false); assert.equal(id.value, '789');
    offline = true; await submit({ preventDefault() {} });
    assert.match(message.textContent, /Connection interrupted/); assert.equal(button.disabled, false);
    assert.deepEqual(posts, ['456', '456']);
    console.log('Update notice checks passed: availability-only notices, pause, window identities, skip failures, stale polling, next windows and reconnect errors');
})().catch(error => { console.error(error); process.exitCode = 1; });
