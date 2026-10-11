(() => {
 'use strict';
 const $ = id => document.getElementById(id), form = $('emote-import');
 if (!form) return;
 const api = window.XurRobot.api, library = $('emote-file-library'), message = $('emote-import-status');
 function node(tag, text, className) {
  const item = document.createElement(tag); if (text) item.textContent = text; if (className) item.className = className; return item;
 }
 async function refresh() {
  const items = await api('emote-files'); library.replaceChildren();
  if (!items.length) library.append(node('p', 'No imported emotes yet.'));
  for (const item of items) {
   const m = item.manifest, card = node('article', '', 'emote-file-card');
   const video = node('video'); video.controls = true; video.preload = 'metadata'; video.playsInline = true;
   video.src = `/robot/api/emote-files/${encodeURIComponent(item.key)}/preview`;
   video.setAttribute('aria-label', `${m.name} video preview`);
   const state = node('p', 'Simulation recording · robot mapping required', 'emote-file-state');
   video.addEventListener('error', () => { state.textContent = 'Video preview is unavailable. Download the original file to inspect it.'; });
   const details = node('p', `${m.arm === 'left' ? 'Left' : 'Right'} arm · ${m.durationSeconds}s · ${item.frames} recorded frames`, 'emote-file-state');
   const download = node('a', 'Download emote file', 'button'); download.href = `/robot/api/emote-files/${encodeURIComponent(item.key)}/download`; download.download = `${m.id}.xuremot`;
   const source = node('a', 'Source model'); source.href = m.source.repository; source.rel = 'noreferrer';
   const actions = node('div', '', 'actions'); actions.append(download, source);
   card.append(node('h2', m.name), video, details, node('p', m.task), state, node('p', item.detail, 'emote-file-state'), actions); library.append(card);
  }
 }
 form.addEventListener('submit', async event => {
  event.preventDefault(); const files = [...$('emote-file').files]; if (!files.length) return;
  $('emote-import-button').disabled = true;
  let imported = 0;
  try {
   for (const file of files) {
    if (file.size > 16*1024*1024) throw Error(`${file.name}: choose a file no larger than 16 MiB.`);
    message.textContent = `Importing ${file.name}…`; await api('emote-files', file); imported++;
   }
   message.textContent = `${imported} emote${imported === 1 ? '' : 's'} imported. Watch the videos before preparing a robot test.`; form.reset();
  } catch (error) { message.textContent = `${imported ? `${imported} imported. ` : ''}${error.message}`; }
  finally { $('emote-import-button').disabled = false; try { await refresh(); } catch (error) { message.textContent = error.message; } }
 });
 refresh().catch(error => { message.textContent = error.message; });
})();
