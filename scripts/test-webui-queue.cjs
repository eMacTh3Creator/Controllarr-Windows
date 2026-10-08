'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/Controllarr.App/WebUI/app.js'), 'utf8');
const context = vm.createContext({});
for (const name of ['num', 'int', 'clampPort', 'normTorrent', 'normStats', 'normTracker', 'normSettings', 'settingsToPayload']) {
  const start = source.indexOf(`function ${name}(`);
  const end = source.indexOf('\n}', start) + 2;
  assert.ok(start >= 0 && end > start, `Missing function ${name}`);
  vm.runInContext(source.slice(start, end), context);
}
const original = {
  connection_limits: { global_max_connections: 500, max_connections_per_torrent: 30, global_max_upload_slots: 12, download_reserve_percent: 25 },
  torrent_queueing: { enabled: true, max_active_downloads: 3, max_active_seeds: 7, max_active_total: 10 },
  ui_preferences: { close_to_tray: false }, torrent_network: { encryption: 'Require' },
};
const draft = context.normSettings(original);
assert.equal(draft.globalMaxConnections, 500);
draft.globalMaxConnections = 600;
draft.downloadReservePercent = 0;
draft.maxActiveSeeds = 2;
const payload = JSON.parse(JSON.stringify(context.settingsToPayload(draft)));
assert.deepEqual(payload.connection_limits, { ...original.connection_limits, global_max_connections: 600, download_reserve_percent: 0 });
assert.deepEqual(payload.torrent_queueing, { ...original.torrent_queueing, max_active_seeds: 2 });
assert.deepEqual(payload.ui_preferences, original.ui_preferences);
assert.deepEqual(payload.torrent_network, original.torrent_network);
assert.equal(context.normSettings({}).downloadReservePercent, 25);
assert.equal(context.normTorrent({ status_reason: 'Full peer budget', priority: 2 }).statusReason, 'Full peer budget');
assert.equal(context.normTorrent({ priority: 2 }).queuePosition, 2);
assert.equal(context.normStats({ connected_peers: 200, connection_limit: 200 }).connectionLimit, 200);
assert.equal(context.normTracker({ HasScrapeInfo: false, NumSeeds: 0 }).hasScrapeInfo, false);
console.log('PASS: WebUI queue/connection settings round-trip, unrelated settings preservation, status reasons and unknown tracker counts');
