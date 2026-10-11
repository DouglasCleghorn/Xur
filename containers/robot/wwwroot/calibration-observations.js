(() => {
 'use strict';
 const byId = id => document.getElementById(id), form = byId('calibration-observations-form');
 if (!form) return;
 const button = byId('calibration-observations-capture'), message = byId('calibration-observations-message'), raw = byId('calibration-observations-raw');
 const copyButton = byId('calibration-observations-copy'), manifestLink = byId('calibration-observations-manifest'), previews = byId('calibration-observations-previews');
 const api = (path, body) => window.XurRobot.api(path, body);
 const element = (tag, text) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; return node; };
 const validId = id => typeof id === 'string' && /^[a-f0-9]{32}$/.test(id);
 const validName = name => typeof name === 'string' && /^[a-z0-9][a-z0-9.-]{0,127}$/.test(name) && !name.includes('..');
 let refreshing = false, pendingJob = null, pendingSince = 0, renderedManifest = null, lastOperationMessage = null;

 async function show(capture) {
  if (!capture) {
   if (raw.value) { message.textContent = 'No saved observation matches the current setup. Previously loaded evidence remains displayed.'; return; }
   if (!pendingJob) message.textContent = 'No calibration observations saved yet. Capture the current view while the robot is disarmed.';
   return;
  }
  if (!validId(capture.id)) throw Error('The saved capture has an invalid identifier.');
  const prefix = `calibration-observations/${capture.id}`, key = `${capture.id}:${capture.manifestSha256}`;
  let manifest = renderedManifest?.key === key ? renderedManifest.value : null;
  let manifestError = null;
  if (!manifest) {
   try { manifest = await api(`${prefix}/manifest`); }
   catch (error) { manifestError = error.message; }
  }
  const report = { capture, manifest };
  raw.value = JSON.stringify(report, null, 2); copyButton.disabled = false;
  byId('calibration-observations-summary').textContent = `${new Date(capture.capturedAt).toLocaleString()} · ${capture.sampleCount} sample(s) · ${capture.complete ? 'capture complete' : 'capture incomplete'}. ${capture.backupVerified ? 'Independent local copy checksum verified.' : 'Independent local copy not verified.'} This does not verify an off-device backup or approve joint calibration.`;
  const problems = [...(capture.problems || [])];
  if (manifestError) problems.push(`Manifest unavailable: ${manifestError}`);
  byId('calibration-observations-problems').replaceChildren(...problems.map(problem => element('li', problem)));
  if (!pendingJob) message.textContent = capture.complete ? 'Read-only evidence saved. No motor commands were issued; calibration remains unapproved.' : 'Incomplete evidence retained. Review the capture problems; calibration remains unapproved.';
  manifestLink.href = `/robot/api/${prefix}/manifest`; manifestLink.download = `calibration-observations-${capture.id}-manifest.json`; manifestLink.hidden = !manifest;
  if (renderedManifest?.key === key && manifest) return;
  previews.replaceChildren(); renderedManifest = manifest ? { key, value: manifest } : null;
  const downloads = element('details'), downloadList = element('ul');
  downloads.append(element('summary', 'Download original images and measured states'), downloadList);
  for (const file of manifest?.files || []) {
   if (!validName(file.name)) continue;
   const item = element('li'), link = element('a', file.name);
   link.href = `/robot/api/${prefix}/files/${encodeURIComponent(file.name)}`; link.download = file.name;
   item.append(link); downloadList.append(item);
  }
  if (downloadList.childElementCount) previews.append(downloads);
  for (const file of manifest?.files || []) {
   if (!validName(file.name) || !file.name.endsWith('.png')) continue;
   const figure = element('figure'), image = element('img'), caption = element('figcaption'), link = element('a', file.name);
   const url = `/robot/api/${prefix}/files/${encodeURIComponent(file.name)}`;
   link.href = url; link.target = '_blank'; link.rel = 'noopener'; image.src = url; image.alt = `Saved calibration observation ${file.name}`; image.loading = 'lazy';
   image.addEventListener('error', () => { image.hidden = true; caption.append(element('p', 'Preview unavailable; the original file link remains available.')); });
   caption.append(link, element('p', `${file.bytes} bytes`), element('code', file.sha256)); figure.append(image, caption); previews.append(figure);
  }
 }

 async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
   if (pendingJob) {
    const job = (await api('jobs')).find(item => item.id === pendingJob);
    if (job && ['completed', 'failed', 'stopped'].includes(job.state)) {
     pendingJob = null; button.disabled = false;
     lastOperationMessage = job.state === 'completed' ? null : `${job.state}: ${job.detail}`;
    } else if (Date.now() - pendingSince > 180000) {
     pendingJob = null; button.disabled = false;
     lastOperationMessage = 'The capture job is no longer being followed. Refresh evidence to inspect any saved result; original files remain retained.';
    } else if (job) message.textContent = `Capturing read-only observations: ${job.detail}`;
   }
   await show(await api('calibration-observations'));
   if (lastOperationMessage) message.textContent = lastOperationMessage;
  } finally { refreshing = false; }
 }

 form.addEventListener('submit', async event => {
  event.preventDefault();
  const samples = Number(form.elements.samples.value);
  if (![1, 2, 3].includes(samples)) { message.textContent = 'Choose one, two or three samples.'; return; }
  button.disabled = true; lastOperationMessage = null; message.textContent = 'Requesting a read-only calibration capture…';
  try {
   const job = await api('calibration-observations', { samples });
   if (!validId(job.id)) throw Error('The capture request returned an invalid job identifier.');
   pendingJob = job.id; pendingSince = Date.now(); message.textContent = job.detail || 'Capturing the current view and motor readings…';
   await refresh();
  } catch (error) { lastOperationMessage = error.message; message.textContent = error.message; if (!pendingJob) button.disabled = false; }
 });
 byId('calibration-observations-refresh').addEventListener('click', () => refresh().catch(error => { message.textContent = error.message; }));
 copyButton.addEventListener('click', async () => {
  if (!raw.value) { message.textContent = 'Capture observations before copying evidence.'; return; }
  try { await navigator.clipboard.writeText(raw.value); message.textContent = 'Calibration evidence JSON copied.'; }
  catch { byId('calibration-observations-raw-details').open = true; raw.focus(); raw.select(); message.textContent = 'Select and copy the evidence below.'; }
 });
 refresh().catch(error => { message.textContent = error.message; });
 setInterval(() => { if (!document.hidden) refresh().catch(error => { message.textContent = error.message; }); }, 3000);
})();
