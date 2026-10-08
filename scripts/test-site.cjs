'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');
const scriptPath = path.join(root, 'docs/assets/downloads.js');
const source = fs.readFileSync(scriptPath, 'utf8');
const { selectDownloadAsset } = require(scriptPath);
const base = 'https://github.com/eMacTh3Creator/Controllarr-Windows';

function release(version = '2.3.1') {
  return {
    tag_name: `v${version}`, draft: false, prerelease: false,
    assets: ['win-x64', 'win-arm64'].flatMap(runtime => ['.zip', '-Setup.exe'].map(suffix => ({
      name: `Controllarr-${version}-${runtime}${suffix}`,
      browser_download_url: `${base}/releases/download/v${version}/Controllarr-${version}-${runtime}${suffix}`
    })))
  };
}

async function resolveLinks(response, reject = false) {
  const links = ['win-x64', 'win-arm64'].map(runtime => ({
    href: `${base}/releases/download/v2.3.0/Controllarr-2.3.0-${runtime}-Setup.exe`,
    getAttribute: name => name === 'data-dl' ? runtime : 'setup'
  }));
  vm.runInNewContext(source, {
    document: { querySelectorAll: selector => { assert.equal(selector, 'a[data-dl]'); return links; } },
    fetch: () => reject ? Promise.reject(new Error('Offline')) : Promise.resolve({ ok: response !== null, json: () => Promise.resolve(response) }),
    AbortController, setTimeout, clearTimeout
  });
  await new Promise(setImmediate);
  return links.map(link => link.href);
}

async function main() {
  for (const runtime of ['win-x64', 'win-arm64']) {
    assert.ok(selectDownloadAsset(release(), runtime).name.endsWith(`${runtime}.zip`));
    assert.ok(selectDownloadAsset(release(), runtime, 'setup').name.endsWith(`${runtime}-Setup.exe`));
  }
  for (const invalid of [null, {}, { ...release(), draft: true }, { ...release(), prerelease: true },
    { ...release(), assets: [{ name: 'Controllarr.exe', browser_download_url: `${base}/releases/latest` }] }]) {
    assert.equal(selectDownloadAsset(invalid, 'win-x64'), null);
  }
  assert.equal(selectDownloadAsset(release(), 'win-x86'), null);
  const unsafe = release();
  unsafe.assets[0].browser_download_url = 'https://example.com/app.zip';
  assert.equal(selectDownloadAsset(unsafe, 'win-x64'), null);
  const missingArm = release();
  missingArm.assets = missingArm.assets.filter(asset => !asset.name.includes('arm64'));
  assert.equal(selectDownloadAsset(missingArm, 'win-arm64'), null);
  assert.ok((await resolveLinks(release())).every(url => url.includes('/v2.3.1/') && url.endsWith('-Setup.exe')));
  assert.ok((await resolveLinks(null)).every(url => url.includes('/v2.3.0/') && url.endsWith('-Setup.exe')));
  assert.ok((await resolveLinks(null, true)).every(url => url.includes('/v2.3.0/') && url.endsWith('-Setup.exe')));
  const mixed = await resolveLinks(missingArm);
  assert.ok(mixed[0].includes('/v2.3.1/') && mixed[1].includes('/v2.3.0/'));

  const html = fs.readFileSync(path.join(root, 'docs/index.html'), 'utf8');
  assert.ok(!html.includes('/blob/main/'));
  const downloads = [...html.matchAll(/data-format="(setup|zip)" data-dl="(win-x64|win-arm64)" href="([^"]+)"/g)];
  assert.equal(downloads.length, 6);
  for (const [, format, runtime, href] of downloads) {
    assert.equal(href, `${base}/releases/download/v2.3.0/Controllarr-2.3.0-${runtime}${format === 'setup' ? '-Setup.exe' : '.zip'}`);
  }
  for (const [, resource] of html.matchAll(/(?:src|href)="(assets\/[^"#]+)"/g)) {
    assert.ok(fs.existsSync(path.join(root, 'docs', resource)), `Missing site asset: ${resource}`);
  }
  for (const [, file] of html.matchAll(/Controllarr-Windows\/blob\/master\/([^"#]+)"/g)) {
    assert.ok(fs.existsSync(path.join(root, file)), `Missing documentation target: ${file}`);
  }
  console.log('PASS: architecture-specific direct downloads, API failure/no-JS fallbacks, trusted URLs and site asset/documentation links');
}

main().catch(error => { console.error(error); process.exitCode = 1; });
