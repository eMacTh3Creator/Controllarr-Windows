(function () {
  'use strict';
  var repository = 'eMacTh3Creator/Controllarr-Windows';

  function selectDownloadAsset(release, runtime) {
    if (!release || release.draft || release.prerelease ||
        !/^v?\d+\.\d+\.\d+$/.test(release.tag_name) ||
        ['win-x64', 'win-arm64'].indexOf(runtime) === -1) return null;
    var name = 'Controllarr-' + release.tag_name.replace(/^v/, '') + '-' + runtime + '.zip';
    var expected = 'https://github.com/' + repository + '/releases/download/' +
      release.tag_name + '/' + name;
    var assets = Array.isArray(release.assets) ? release.assets : [];
    return assets.find(function (asset) {
      return asset.name === name && asset.browser_download_url === expected;
    }) || null;
  }

  if (typeof module !== 'undefined' && module.exports) module.exports = { selectDownloadAsset: selectDownloadAsset };
  if (typeof document === 'undefined' || typeof fetch !== 'function' || typeof AbortController !== 'function') return;

  // Links always work immediately, including offline/API-limited/no-JS visits.
  // Only upgrade their direct ZIP targets when a matching stable asset exists.
  var controller = new AbortController();
  var timeout = setTimeout(function () { controller.abort(); }, 8000);
  fetch('https://api.github.com/repos/' + repository + '/releases/latest', {
    headers: { Accept: 'application/vnd.github+json' }, signal: controller.signal
  })
    .then(function (response) { if (!response.ok) throw new Error('Release lookup failed'); return response.json(); })
    .then(function (release) {
      document.querySelectorAll('a[data-dl]').forEach(function (link) {
        var asset = selectDownloadAsset(release, link.getAttribute('data-dl'));
        if (asset) link.href = asset.browser_download_url;
      });
    })
    .catch(function () { /* Preserve the versioned direct-download fallback. */ })
    .finally(function () { clearTimeout(timeout); });
}());
