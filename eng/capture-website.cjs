// Render documentation fixtures locally. Only reviewed files are promoted to docs/assets.
const {chromium} = require('../.build/browser/node_modules/playwright');
const {execFileSync} = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');

const fixtures = path.resolve('.build/evidence/website-fixtures');
const captures = path.resolve('.build/evidence/website-captures');
const origin = 'https://screenshots.test';
const assetTypes = {'.css':'text/css', '.js':'text/javascript', '.ttf':'font/ttf', '.svg':'image/svg+xml'};

async function fixtureRoute(route) {
  const request = route.request();
  const url = new URL(request.url());
  if (url.origin !== origin) return route.abort();
  // The page normally posts a status query for each provider. Supply signed-out fixtures only.
  if (request.method() === 'POST' && url.pathname === '/api/t3-code/setup' && request.postDataJSON().action === 'status') {
    return route.fulfill({json:{state:'Signed out', message:'Ready to sign in.'}});
  }
  if (request.method() !== 'GET') return route.abort();
  switch (url.pathname) {
    case '/api/t3-code/status': return route.fulfill({json:{state:'Running', enabled:true, installed:true, workloadId:'example-t3'}});
    case '/api/t3-code/runtime': return route.fulfill({json:{mode:'Container', stage:'Ready'}});
    case '/api/t3-code/storage': return route.fulfill({json:{folders:[]}});
    case '/api/t3-code/models': return route.fulfill({json:{models:[]}});
    case '/api/workstations/w1/screenshot':
      // Stand-in for a desktop framebuffer. Never read a real workstation preview.
      return route.fulfill({path:'docs/assets/xur-header.png', contentType:'image/png'});
    case '/api/model-lab/targets':
      return route.fulfill({json:[{workload:{id:'example-model', name:'Local assistant', gpus:['GPU 3','GPU 4'], recipe:{name:'Example language model',engine:'vLLM'}}, instance:{instanceId:'example'}}]});
    case '/api/benchmarks': return route.fulfill({json:[]});
  }
  if (url.pathname.startsWith('/api/model-lab/')) return route.fulfill({json:[]});
  if (url.pathname.startsWith('/api/')) return route.fulfill({status:503, json:{error:'Offline screenshot fixture'}});
  const type = assetTypes[path.extname(url.pathname)];
  const file = type ? path.join('src/Xur.Control/wwwroot', url.pathname) : path.join(fixtures, url.pathname.slice(1) + '.html');
  if (!fs.existsSync(file)) return route.fulfill({status:404, body:''});
  if (type) return route.fulfill({path:file, contentType:type});
  const html = fs.readFileSync(file, 'utf8').replace(/(<form\b[^>]*>)/g, '$1<input type="hidden" name="__RequestVerificationToken" value="fixture-only">');
  return route.fulfill({body:html, contentType:'text/html'});
}

(async () => {
  fs.mkdirSync(captures, {recursive:true});
  const dotnet = process.env.XUR_DOTNET || path.join(process.env.HOME, '.local/share/xur-build/dotnet/dotnet');
  const localNames = [os.userInfo().username, os.hostname(), os.hostname().split('.')[0]].filter(Boolean);
  execFileSync(dotnet, ['run','--project','tests/Xur.Unit.Tests','-c','Release','--artifacts-path','.build/website-render','--','--website-render',fixtures], {
    stdio:'inherit'
  });
  const browser = await chromium.launch({headless:true});
  try {
    const page = await browser.newPage({viewport:{width:1200,height:800}, deviceScaleFactor:1, serviceWorkers:'block', timezoneId:'UTC', locale:'en-US'});
    // Keep the sample desktop preview timestamp reproducible.
    await page.clock.setFixedTime(new Date('2026-01-01T12:00:00Z'));
    await page.route('**/*', fixtureRoute);
    async function capture(route, name, {prepare, selector, navPath} = {}) {
      await page.goto(origin + '/' + route);
      await page.evaluate(() => document.fonts.ready);
      if (prepare) await prepare();
      // The static Razor renderer has one NavigationManager shared by all fixture pages.
      await page.evaluate(current => {
        document.activeElement?.blur();
        for (const link of document.querySelectorAll('nav a')) {
          const active = link.getAttribute('href') === current;
          link.classList.toggle('active', active);
          if (active) link.setAttribute('aria-current', 'page');
          else link.removeAttribute('aria-current');
        }
      }, navPath ?? '/' + route);
      const target = page.locator(selector || 'body');
      const visibleValues = await target.locator('input:visible, textarea:visible, select:visible').evaluateAll(fields => fields.map(field => field.value));
      const text = [await target.innerText(), ...visibleValues].join('\n');
      const words = new Set(text.toLowerCase().split(/[^a-z0-9_.-]+/));
      assert(!localNames.some(name => words.has(name.toLowerCase())), 'Local account or host in ' + name);
      assert(!/\b(?:10\.\d+|172\.(?:1[6-9]|2\d|3[01])|192\.168|100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7]))\.\d+\.\d+\b|BEGIN (?:PUBLIC|PRIVATE) KEY|[\w.+-]+@[\w.-]+\.\w+/i.test(text), 'Private data in ' + name);
      const shot = selector ? target : page;
      await shot.screenshot({path:path.join(captures, name + '.jpg'), type:'jpeg', quality:85});
    }
    await capture('home', 'control-panel', {navPath:'/'});
    await capture('workstations-populated', 'workstations', {prepare:() => page.locator('.station-preview img:visible').waitFor()});
    await capture('profile-edit', 'profile-editor', {navPath:'/profiles'});
    await capture('model-lab', 'model-lab', {prepare:() => page.locator('#bench-prompt').fill('')});
    await capture('settings', 'update-channel', {selector:'#update-channel', prepare:() => page.locator('#release-channel').selectOption('nightly')});
    async function captureServicePanels(suffix) {
      await capture('t3-code', 't3-connect' + suffix, {selector:'section[aria-labelledby="t3-connect-heading"]', prepare:() => page.locator('#t3-running:visible').waitFor()});
      await capture('t3-code', 't3-github' + suffix, {selector:'#t3-github', prepare:() => page.locator('#t3-github[data-state="Signed out"]').waitFor()});
      await capture('network-shares', 'network-shares' + suffix, {selector:'section[aria-labelledby="connections-heading"]'});
    }
    await captureServicePanels('');
    await page.setViewportSize({width:390,height:844});
    await captureServicePanels('-phone');
    await capture('login', 'setup-phone-sign-in');
    await capture('setup-account', 'setup-phone-account');
    await capture('profile-edit', 'setup-phone-profile', {navPath:'/profiles'});
    await capture('home', 'setup-phone-home', {navPath:'/'});
    console.log('Review the JPEGs in .build/evidence/website-captures before copying approved images into docs/assets.');
  } finally {
    await browser.close();
  }
})().catch(error => {console.error(error); process.exitCode=1;});
