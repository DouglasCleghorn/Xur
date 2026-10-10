(() => {
 'use strict';
 const $ = id => document.getElementById(id), page = document.body.dataset.page;
 let csrf, mutating = false, observationRequest, markerReport, markerJob, motorData, renderedTagJob;
 const set = (id, text) => { if ($(id)) $(id).textContent = text; };
 const showError = error => { $('error').hidden = false; $('error').textContent = error.message; };
 const clearError = () => { $('error').hidden = true; };
 const time = value => value ? new Date(value).toLocaleTimeString() : 'No timestamp';
 const ago = value => value ? `${Math.max(0, Math.floor((Date.now() - Date.parse(value)) / 1000))}s ago` : 'No reading';
 const value = (data, key, unit = '') => data?.[key] === undefined ? '—' : `${data[key]}${unit}`;
 function list(id, values) {
  if (!$(id)) return;
  $(id).replaceChildren(...values.map(text => { const item = document.createElement('li'); item.textContent = text; return item; }));
 }
 async function api(path, body) {
  if (body !== undefined && !csrf) {
   const response = await fetch('/robot/csrf', { cache: 'no-store' });
   if (!response.ok) throw Error('Sign in to Xur and refresh this page.');
   csrf = (await response.json()).token;
  }
  const response = await fetch(`/robot/api/${path}`, body === undefined ? { cache: 'no-store' } : {
   method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: csrf }, body: JSON.stringify(body)
  });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw Error(data?.error || `Robot request failed (${response.status}).`);
  return data;
 }
 function operation(buttonId, path, body, message) {
  $(buttonId)?.addEventListener('click', async () => {
   mutating = true; $(buttonId).disabled = true; clearError();
   try {
    if (!['stop', 'estop'].includes(buttonId) && observationRequest) await observationRequest.catch(() => {});
    const result = await api(path, body);
    if (path === 'tasks') markerJob = result.id;
    set('action-status', message);
    await refresh();
   } catch (error) { showError(error); }
   finally { mutating = false; if (!['controller', 'calibrate'].includes(buttonId)) $(buttonId).disabled = false; }
  });
 }
 operation('stop', 'stop', {}, 'Stopped and disarmed.');
 operation('estop', 'estop', {}, 'E-stop requested. Motion is latched off; disconnect motor power if necessary.');
 operation('reset-estop', 'reset-estop', {}, 'Checking fresh motor feedback before resetting E-stop…');
 operation('calibrate', 'auto-calibrate', {}, 'Checking motors and visual references for automatic calibration…');
 operation('controller', 'start-controller', { seconds: 60 }, 'Controller session requested. Hold Start / Menu to move; Back / View stops.');
 operation('prepare', 'prepare', {}, 'Checking the installed LeRobot tools…');
 operation('scan-tags', 'tasks', { kind: 'inspect-markers' }, 'Capturing three frames from each camera and detecting AprilTags…');
 async function copy(text, fallback) {
  try { await navigator.clipboard.writeText(text); set('action-status', 'JSON copied.'); }
  catch { fallback.hidden = false; fallback.focus(); fallback.select(); set('action-status', 'Select and copy the JSON below.'); }
 }
 $('copy-motors')?.addEventListener('click', () => copy(JSON.stringify(motorData ?? {}, null, 2), $('motor-data')));
 $('copy-tags')?.addEventListener('click', () => copy($('tag-data').value, $('tag-data')));
 $('download-tags')?.addEventListener('click', () => {
  const url = URL.createObjectURL(new Blob([$('tag-data').value], { type: 'application/json' }));
  const link = document.createElement('a'); link.href = url; link.download = `apriltags-${markerJob || 'observations'}.json`; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
 });
 $('overlay')?.addEventListener('change', () => {
  for (const camera of ['head', 'hand']) $(`${camera}-overlay`).hidden = !$('overlay').checked;
 });
 function image(camera, source, capturedAt, error) {
  const img = $(`${camera}-image`), placeholder = $(`${camera}-empty`);
  if (source) { img.src = source; img.hidden = false; placeholder.hidden = true; }
  if (error) { img.hidden = true; placeholder.hidden = false; placeholder.textContent = error; }
  set(`${camera}-time`, error || `${time(capturedAt)} · ${ago(capturedAt)}`);
 }
 function positionChart(motor, diagnostic) {
  const r = motor.registers || {}, range = diagnostic?.calibratedRange, ns = 'http://www.w3.org/2000/svg';
  const figure = document.createElement('figure'); figure.className = 'motor-chart';
  const calibrated = range?.state === 'verified';
  const label = calibrated ? `Calibrated range: ${range.minimumCounts}–${range.maximumCounts} counts` : range?.state === 'not-applicable' ? 'Angular range not applicable' : 'Not calibrated';
  const caption = document.createElement('figcaption'); caption.textContent = label;
  const svg = document.createElementNS(ns, 'svg'); svg.setAttribute('viewBox', '0 0 360 54'); svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', `${label}. Encoder position ${r.Present_Position ?? 'unavailable'} counts. Display scale 0 to 4095 counts.`);
  const element = (name, attributes) => { const item = document.createElementNS(ns, name); for (const [key, value] of Object.entries(attributes)) item.setAttribute(key, value); svg.append(item); return item; };
  element('rect', { x: 12, y: 10, width: 336, height: 12, rx: 3, class: 'encoder-track' });
  const x = counts => 12 + counts / 4095 * 336;
  if (calibrated) element('rect', { x: x(range.minimumCounts), y: 10, width: x(range.maximumCounts) - x(range.minimumCounts), height: 12, class: 'calibrated-range' });
  if (Number.isInteger(r.Present_Position) && r.Present_Position >= 0 && r.Present_Position <= 4095)
   element('line', { x1: x(r.Present_Position), x2: x(r.Present_Position), y1: 5, y2: 29, class: 'encoder-position' });
  element('text', { x: 12, y: 47 }).textContent = '0 counts';
  element('text', { x: 348, y: 47, 'text-anchor': 'end' }).textContent = '4095 counts';
  const explanation = document.createElement('p'); explanation.className = 'subtle';
  explanation.textContent = `${range?.detail || 'Calibration evidence is unavailable.'}${calibrated ? ` Source: ${range.sourceFile}. ${range.convention}.` : ''}`;
  const stored = document.createElement('p'); stored.className = 'subtle';
  stored.textContent = `Stored EEPROM position limits: ${value(r, 'Min_Position_Limit')}–${value(r, 'Max_Position_Limit')} counts. These do not establish a calibrated range.`;
  figure.append(caption, svg, explanation, stored); return figure;
 }
 function motors(observation, diagnostics) {
  motorData = { observedAt: observation.observedAt, buses: observation.buses, diagnostics: diagnostics || [] };
  const byMotor = new Map((diagnostics || []).map(item => [JSON.stringify([item.port, item.id]), item]));
  const open = new Set([...$('motors').querySelectorAll('details[open]')].map(item => item.dataset.motor));
  const rows = [];
  for (const bus of observation.buses || []) {
   if (bus.error) {
    const row = document.createElement('tr'), cell = document.createElement('td'); cell.colSpan = 11;
    cell.textContent = `${bus.role}: ${bus.error}`; row.append(cell); rows.push(row);
   }
   for (const motor of bus.motors || []) {
    const row = document.createElement('tr'), first = document.createElement('td'), details = document.createElement('details');
    const key = JSON.stringify([bus.port, motor.id]), diagnostic = byMotor.get(key); details.dataset.motor = key; details.open = open.has(key);
    const title = document.createElement('summary'); title.textContent = (diagnostic?.name || motor.name || `Motor ${motor.id}`).replaceAll('_', ' ');
    const raw = document.createElement('pre'); raw.textContent = JSON.stringify({ bus: bus.port, ...motor }, null, 2);
    details.append(title, positionChart(motor, diagnostic), raw); first.append(details); row.append(first);
    const r = motor.registers || {};
    const cells = [`${bus.role} · ${motor.id}`, value(r, 'Present_Position', ' counts'), value(r, 'Present_Velocity', ' raw'),
     value(r, 'Present_Load', ' raw'), value(r, 'Present_Current', ' raw'), value(r, 'Present_Temperature', '°C'),
     diagnostic?.temperature.maximumCelsius == null ? '—' : `${diagnostic.temperature.maximumCelsius}°C`,
     r.Present_Voltage === undefined ? '—' : `${(r.Present_Voltage / 10).toFixed(1)} V`,
     r.Torque_Enable === undefined ? '—' : r.Torque_Enable ? 'Enabled' : 'Off',
     motor.error || (r.Status ? `Status ${r.Status}` : r.Moving ? 'Moving' : 'Stationary')];
    cells.forEach((text, index) => {
     const cell = document.createElement('td'); cell.textContent = text;
     if (index === 6) {
      const window = diagnostic?.temperature, note = document.createElement('small'); note.className = 'temperature-coverage';
      note.textContent = window?.sampleCount ? `${window.sampleCount} samples · ${Math.round(window.observedSpanSeconds)}s span${window.latestAgeSeconds >= 60 ? ' · stale' : ''}` : 'No fresh samples';
      cell.title = window?.sampleCount ? `${window.coverage}. First: ${window.firstSampleAt}. Latest: ${window.lastSampleAt}. History resets when the app restarts.` : 'No accepted temperature samples in the last five minutes.';
      cell.append(note);
     }
     row.append(cell);
    });
    rows.push(row);
   }
  }
  $('motors').replaceChildren(...rows);
  const count = (observation.buses || []).reduce((sum, bus) => sum + (bus.motors?.length || 0), 0);
  set('motor-summary', `${count} motors reported · ${time(observation.observedAt)} · expand rows for all available details`);
  $('motor-data').value = JSON.stringify(motorData, null, 2);
 }
 async function observe() {
  if (document.hidden || mutating || observationRequest) return;
  observationRequest = (async () => {
   const data = await api('observation'), observation = data.observation;
   if (observation) {
    for (const camera of observation.cameras || [])
     image(camera.name, camera.jpeg ? `data:image/jpeg;base64,${camera.jpeg}` : null, camera.capturedAt, camera.error);
    motors(observation, data.motors);
   }
   set('observation-status', data.paused ? `Refresh paused during ${data.operation || 'robot operation'} · last capture ${ago(observation?.observedAt)}` : `Updated ${time(observation?.observedAt)} · refreshes every 5 seconds`);
   if (data.problem) throw Error(data.problem);
  })();
  try { await observationRequest; } catch (error) { showError(error); set('observation-status', 'Refresh unavailable · previous captures retained'); }
  finally { observationRequest = null; }
 }
 function overlay(camera) {
  const frame = camera.frames.at(-1), svg = $(`${camera.name}-overlay`), ns = 'http://www.w3.org/2000/svg';
  svg.replaceChildren(); svg.setAttribute('viewBox', `0 0 ${frame.width} ${frame.height}`); svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
  for (const tag of frame.detections) {
   const polygon = document.createElementNS(ns, 'polygon'); polygon.setAttribute('points', tag.corners.map(point => point.join(',')).join(' '));
   polygon.setAttribute('class', `tag-outline${frame.ambiguousDuplicateIds.includes(tag.id) ? ' ambiguous' : ''}`);
   const text = document.createElementNS(ns, 'text'); text.setAttribute('x', tag.center[0]); text.setAttribute('y', tag.center[1] - 12); text.setAttribute('class', 'tag-label'); text.textContent = String(tag.id).padStart(2, '0');
   svg.append(polygon, text);
  }
  // Match the image viewport to its native aspect ratio, including non-16:9 cameras.
  $(`${camera.name}-image`).parentElement.style.aspectRatio = `${frame.width} / ${frame.height}`;
  set(`${camera.name}-tags`, `${frame.detections.length} detections · ${frame.width} × ${frame.height} · ${camera.markers.map(tag => `${String(tag.id).padStart(2, '0')}: ${tag.detectedFrames}/3 frames`).join(' · ') || 'No unambiguous tags'}`);
  image(camera.name, `/robot/api/jobs/${markerJob}/captures/${camera.name}-markers.jpg`, frame.capturedAt);
 }
 async function tags(jobs) {
  markerJob ??= jobs.find(job => job.kind === 'inspect-markers' && job.state === 'completed')?.id;
  if (!markerJob || renderedTagJob === markerJob) return;
  const job = jobs.find(item => item.id === markerJob);
  if (job?.state === 'failed' || job?.state === 'stopped') { set('action-status', job.detail); markerJob = null; return; }
  if (job?.state !== 'completed') return;
  markerReport = await api(`jobs/${markerJob}/markers`);
  const rows = [];
  for (const camera of markerReport.cameras) {
   overlay(camera);
   const frame = camera.frames.at(-1);
   for (const tag of frame.detections) {
    const row = document.createElement('tr');
    for (const text of [camera.name, String(tag.id).padStart(2, '0'), tag.center.map(n => n.toFixed(2)).join(', '), tag.decisionMargin.toFixed(2), tag.hamming,
     camera.markers.find(item => item.id === tag.id)?.detectedFrames ?? '—', frame.ambiguousDuplicateIds.includes(tag.id) ? 'Ambiguous' : 'No']) {
     const cell = document.createElement('td'); cell.textContent = text; row.append(cell);
    }
    rows.push(row);
   }
  }
  if (!rows.length) { const row = document.createElement('tr'), cell = document.createElement('td'); cell.colSpan = 7; cell.textContent = 'No tags decoded in the displayed frames.'; row.append(cell); rows.push(row); }
  $('tag-rows').replaceChildren(...rows);
  $('tag-data').value = JSON.stringify({ jobId: markerJob, ...markerReport }, null, 2);
  const metric=markerReport.metric;
  set('tag-job', markerJob); set('action-status', `Scan complete · shared tag IDs: ${markerReport.sharedIds.join(', ') || 'none'}`+
   (metric?` · visual poses: ${metric.cameras.map(camera=>`${camera.name} ${camera.state}`).join(', ')}; candidates and errors are in the raw data. Joint calibration remains unapproved.`:' · pixel observations only; measured camera settings are not supplied.'));
  renderedTagJob = markerJob;
 }
 async function refresh() {
  if (document.hidden) return;
  try {
   const [status, jobs] = await Promise.all([api('status'), ['guide', 'setup'].includes(page) ? Promise.resolve([]) : api('jobs')]);
   set('mode', status.mode); list('problems', status.problems || []);
   const latched = status.emergencyStopLatched === true;
   $('estop').closest('.stop-bar').classList.toggle('latched', latched);
   set('estop-state', latched ? 'E-stop latched · motion disabled' : 'Software emergency stop');
   $('reset-estop').hidden = !latched;
   for (const id of ['controller', 'calibrate']) if ($(id)) $(id).disabled = latched;
   if (page === 'guide' || page === 'setup') return;
   if (page === 'tags') await tags(jobs);
   else {
    set('identity', status.workloadId ? `${status.workloadId} · ${status.stopLatched ? 'disarmed' : 'operator session'} · base movement disabled` : 'Load the Robotics workload in your Xur profile.');
    list('jobs', jobs.slice(0, 8).map(job => `${time(job.updated)} · ${job.kind} · ${job.state}: ${job.detail}`));
    if (status.job && ['auto-calibrate', 'controller', 'prepare', 'reset-estop'].includes(status.job.kind)) set('action-status', `${status.job.kind} · ${status.job.state}: ${status.job.detail}`);
    const calibration = await api('calibration-assessment');
    if (calibration) { set('calibration-status', calibration.summary); list('calibration-blockers', calibration.blockers || []); }
   }
  } catch (error) { showError(error); set('mode', 'Unavailable'); }
 }
 async function start() {
  await refresh();
  if (page === 'dashboard') {
   try { const configuration = await api('configuration'); set('head-device', configuration?.headCamera || 'Head camera not configured'); set('hand-device', configuration?.handCamera || 'Hand camera not configured'); }
   catch (error) { showError(error); }
   await observe();
  }
  setInterval(() => refresh(), 3000);
  if (page === 'dashboard') setInterval(() => observe(), 5000);
 }
 window.XurRobot = { api };
 start();
})();
