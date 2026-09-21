'use strict';
(() => {
  const panel = document.querySelector('[data-installer]');
  if (!panel) return;
  const status = document.getElementById('download-status');
  const meta = document.getElementById('download-meta');
  const download = document.getElementById('download-iso');
  const retry = document.getElementById('download-retry');
  const releaseLink = document.getElementById('download-release');
  const parts = document.getElementById('download-parts');
  const api = 'https://api.github.com/repos/DouglasCleghorn/Xur/releases';
  const base = 'https://github.com/DouglasCleghorn/Xur/releases/';
  let autoStart = new URLSearchParams(location.search).get('start') === '1';
  let busy = false;
  const fallback = {href: download.href, meta: meta.textContent, release: releaseLink.href};
  function releaseUrl(value, section) {
    try {
      const url = new URL(value);
      return url.href.startsWith(base + section + '/') && !url.search && !url.hash ? url.href : null;
    } catch { return null; }
  }
  function installerName(release) {
    const tag = release.tag_name || '';
    const match = /^(nightly-|v)([0-9]+(?:\.[0-9]+)+)$/.exec(tag);
    const name = match ? `xur-${match[1] === 'v' ? 'stable' : 'nightly'}-${match[2]}-x86_64.iso` : null;
    return release.assets.find(a => name && a.name === name) ||
      release.assets.find(a => a.name === 'xur-installer-x86_64.iso');
  }
  async function check() {
    if (busy) return;
    busy = true;
    retry.hidden = true; parts.hidden = true;
    download.href = fallback.href; meta.textContent = fallback.meta; releaseLink.href = fallback.release;

    status.textContent = 'Checking GitHub for the latest published installer…';
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 12000);
    try {
      // Stay static: only public release metadata is fetched; ISO bytes go directly from GitHub.
      const releases = [];
      for (let page = 1; page <= 3; page++) {
        const response = await fetch(`${api}?per_page=100&page=${page}`, {
          headers: {Accept: 'application/vnd.github+json'}, credentials: 'omit', signal: controller.signal
        });
        if (!response.ok) throw new Error('Release lookup unavailable');
        const batch = await response.json();
        if (!Array.isArray(batch)) throw new Error('Invalid release metadata');
        releases.push(...batch);
        if (batch.length < 100) break;
      }
      const candidates = releases.filter(r => !r.draft && r.published_at && Array.isArray(r.assets) &&
        installerName(r))
        .sort((a, b) => Date.parse(b.published_at) - Date.parse(a.published_at));
      const release = candidates[0];
      if (!release) {
        status.textContent = 'No newer installer was found. The published installer below is still available.';
        retry.hidden = false;
        return;
      }
      const url = releaseUrl(release.html_url, 'tag');
      if (!url) throw new Error('Unexpected release URL');
      const iso = installerName(release);
      const assetUrl = releaseUrl(iso.browser_download_url, 'download');
      if (!assetUrl || assetUrl !== base + 'download/' + encodeURIComponent(release.tag_name) + '/' + encodeURIComponent(iso.name)) throw new Error('Unexpected installer URL');
      releaseLink.href = url; releaseLink.textContent = 'Release notes and checksums';
      const label = release.prerelease ? 'Nightly / pre-release' : 'Stable release';
      meta.textContent = `${release.name || release.tag_name} · ${label} · Published ${new Date(release.published_at).toLocaleDateString()}`;
      download.href = assetUrl;
      download.hidden = false;
      if (Number.isFinite(iso.size) && iso.size > 0) meta.textContent += ` · ${(iso.size / 1024 ** 3).toFixed(2)} GiB`;
      status.textContent = 'Your online installer is ready. Select Download ISO to begin.';
      if (autoStart) {
        autoStart = false;
        // Keep instructions visible when GitHub serves the attachment; never buffer the ISO in memory.
        if (document.readyState !== 'complete') await new Promise(resolve => window.addEventListener('load', resolve, {once: true}));
        download.click();
      }
    } catch {
      status.textContent = 'Could not check GitHub for a newer installer. The published installer below is still available; try again to check for updates.';
      retry.hidden = false;
    } finally {
      clearTimeout(timeout); busy = false;
    }
  }
  download.addEventListener('click', () => {
    status.textContent = 'Download requested. If your browser did not start it, select Download ISO again.';
  });
  retry.addEventListener('click', check);
  check();
})();
