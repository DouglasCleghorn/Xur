(() => {
 'use strict';
 const $ = id => document.getElementById(id), api = window.XurRobot.api;
 const phases = ['left-reference', 'left-range', 'right-reference', 'right-range', 'review'];
 const titles = { connecting: 'Checking torque is off…', 'left-reference': 'Place the left arm and head at their reference pose.', 'left-range': 'Move the left arm and head by hand.', 'right-reference': 'Place the right arm at its reference pose.', 'right-range': 'Move the right arm by hand.', review: 'Review the ranges before saving.', completed: 'Setup complete.', stopped: 'Setup stopped.', failed: 'Setup needs attention.' };
 let connection, connected = false, latest, ownedSession, busy = false, renderedPhase, lastUpdate = 0;
 const active = state => state && !['idle', 'completed', 'failed', 'stopped'].includes(state.phase);
 const error = e => { $('error').textContent = e.message; $('error').hidden = false; };
 function availability() {
  const fresh = connected && Date.now() - lastUpdate < 3000;
  const robot = latest?.robot, state = latest?.calibration;
  const checks = ['operator-present', 'arms-supported', 'disconnect-ready'].every(id => $(id).checked);
  const ready = robot && robot.stopLatched && !robot.emergencyStopLatched && robot.job?.state !== 'running';
  $('setup-begin').disabled = busy || !fresh || active(state) || !ready || !checks || robot.motionEnabled || !robot.configured;
  $('start-reason').textContent = !fresh ? 'Live updates are unavailable. Reconnect before starting.' : active(state) ? 'A setup session is active below.' : !ready ? 'Stop and disarm the robot before starting.' : robot.motionEnabled ? 'Disable motion in hardware settings first.' : !robot.configured ? 'Save the robot’s hardware settings first.' : !checks ? 'Confirm all three checks before starting.' : 'Ready. Motors will stay off while you guide the joints.';
  $('setup-next').disabled = busy || !fresh || !active(state) || ownedSession !== state?.sessionId || !state?.canAdvance || !$('step-confirm').checked;
 }
 function render(state) {
  if (latest) latest.calibration = state;
  if (state.phase === 'idle') { availability(); return; }
  $('setup-flow').hidden = false;
  const step = phases.indexOf(state.phase);
  $('step-number').textContent = step >= 0 ? `STEP ${step + 2} OF 6` : '';
  $('step-title').textContent = titles[state.phase] || 'Setup status';
  $('step-instruction').textContent = state.detail;
  $('torque-status').textContent = state.torqueOffVerified ? `Verified torque off · updated ${new Date(state.updated).toLocaleTimeString()}` : ['failed', 'stopped'].includes(state.phase) ? 'Current torque state is unverified. Inspect motor power before restarting hardware setup.' : 'Waiting for a verified torque-off reading.';
  $('reference-guide').hidden = !state.phase.endsWith('-reference');
  $('step-confirm-label').hidden = !active(state) || state.phase === 'connecting';
  $('setup-next').hidden = !active(state) || state.phase === 'connecting';
  $('finish-next').hidden = state.phase !== 'completed';
  $('step-confirm-text').textContent = state.phase === 'review' ? 'I checked every range covers clear travel and want to save the measured calibration.' : state.phase.endsWith('-range') ? 'I moved every listed joint through its clear travel, avoiding hard stops.' : 'The arm is supported, every joint is at mid travel, and the gripper is half open.';
  $('setup-next').textContent = state.phase === 'review' ? 'Save measured calibration' : state.phase.endsWith('-range') ? 'Finish this side' : 'Reference pose is ready';
  $('step-wait').textContent = active(state) && ownedSession !== state.sessionId ? 'This session is controlled by another page. Closing that page stops it after 15 seconds.' : state.phase.endsWith('-range') && !state.canAdvance ? 'Waiting for every joint to show a recorded range…' : '';
  for (const item of document.querySelectorAll('[data-phase]')) { if (item.dataset.phase === state.phase) item.setAttribute('aria-current', 'step'); else item.removeAttribute('aria-current'); }
  const existing = new Map([...$('range-readings').children].map(card => [card.dataset.motor, card]));
  const wanted = new Set(state.motors.map(m => m.name));
  for (const [name, card] of existing) if (!wanted.has(name)) card.remove();
  for (const motor of state.motors) {
   let card = existing.get(motor.name);
   if (!card) {
    card = document.createElement('div'); card.className = 'range-card'; card.dataset.motor = motor.name;
    const title = document.createElement('strong'), value = document.createElement('p'), bar = document.createElement('progress'), range = document.createElement('p');
    title.textContent = motor.name.replaceAll('_', ' '); value.className = 'range-value'; bar.max = 4095; bar.setAttribute('aria-label', `${title.textContent} encoder position`);
    card.append(title, value, bar, range); $('range-readings').append(card);
   }
   card.children[1].textContent = `${motor.position} counts`; card.children[2].value = motor.position;
   card.children[3].textContent = `${motor.minimum}–${motor.maximum} · span ${motor.maximum - motor.minimum} · ${motor.temperature}°C`;
  }
  if (renderedPhase !== `${state.sessionId}:${state.phase}`) {
   $('step-confirm').checked = false; renderedPhase = `${state.sessionId}:${state.phase}`;
   if (ownedSession === state.sessionId) $('step-title').focus({ preventScroll: true });
  }
  availability();
 }
 async function mutation(run) {
  busy = true; $('error').hidden = true; availability();
  try { await run(); } catch (e) { error(e); } finally { busy = false; availability(); }
 }
 $('setup-begin').addEventListener('click', () => mutation(async () => {
  await api('hand-calibration/start', { mode: 'hardware', operatorPresent: $('operator-present').checked, armsSupported: $('arms-supported').checked, disconnectReady: $('disconnect-ready').checked });
  const state = await api('hand-calibration'); ownedSession = state.sessionId; render(state); $('setup-flow').scrollIntoView({ behavior: 'smooth', block: 'start' });
 }));
 $('setup-next').addEventListener('click', () => mutation(async () => {
  const state = latest.calibration;
  render(await api('hand-calibration/advance', { sessionId: state.sessionId, phase: state.phase, revision: state.revision, confirmed: $('step-confirm').checked }));
 }));
 for (const input of document.querySelectorAll('input')) input.addEventListener('change', availability);
 async function connect() {
  try {
   const response = await fetch('/robot/csrf', { cache: 'no-store' }); if (!response.ok) throw Error('Sign in to Xur and refresh this page.');
   const token = (await response.json()).token;
   connection = new signalR.HubConnectionBuilder().withUrl('/robot/realtime', { transport: signalR.HttpTransportType.ServerSentEvents | signalR.HttpTransportType.LongPolling, headers: { RequestVerificationToken: token } }).withAutomaticReconnect([0, 2000, 5000, 10000]).build();
   const status = value => { connected = value; $('live-state').dataset.connected = String(value); $('live-state').textContent = value ? 'Live · SignalR' : 'Reconnecting · steps paused'; availability(); };
   connection.on('setup', raw => { try { latest = JSON.parse(raw); lastUpdate = Date.now(); render(latest.calibration); } catch (e) { status(false); error(e); } });
   connection.onreconnecting(() => status(false)); connection.onreconnected(() => status(true)); connection.onclose(() => { status(false); setTimeout(connect, 5000); });
   await connection.start(); status(true);
  } catch (e) { connected = false; $('live-state').textContent = 'Live connection unavailable'; error(e); availability(); setTimeout(connect, 5000); }
 }
 setInterval(() => availability(), 500);
 setInterval(async () => {
  const state = latest?.calibration;
  if (!connected || !active(state) || ownedSession !== state.sessionId || document.hidden) return;
  try { await api('hand-calibration/heartbeat', { sessionId: ownedSession }); } catch (e) { error(e); ownedSession = null; availability(); }
 }, 3000);
 connect();
})();
