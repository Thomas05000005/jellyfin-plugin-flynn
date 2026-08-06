/*
 * Flynn client runtime.
 *
 * PURE ASCII ONLY. Some Jellyfin client paths decode this file as latin1 rather than UTF-8, and a
 * single non-ASCII byte breaks the whole script silently: no error, the feature simply stops
 * existing. Use \uXXXX escapes for anything outside ASCII. A CI gate enforces this.
 *
 * Loaded on every page of the web UI, so it must stay cheap and must never throw at top level.
 */
(function () {
    'use strict';

    // Synchronous re-entry guard. Two delivery paths could both insert the tag, and a guard that
    // checks for a DOM element only works after that element exists, which is too late if the
    // second copy starts before the first has inserted anything.
    if (window.__flynnLoaded) {
        return;
    }
    window.__flynnLoaded = true;

    /*
     * Read from our own script tag rather than written here as a constant. The injected tag already
     * carries ?v=<assembly version> to bust the browser cache, so taking it from there cannot go
     * stale -- which the constant that used to sit here promptly did, claiming 0.1.0 for five
     * releases. A version reported by hand is a version that is eventually wrong.
     */
    function version() {
        var tag = document.getElementById('flynn-client');
        var match = tag && /[?&]v=([^&]+)/.exec(tag.getAttribute('src') || '');
        return match ? decodeURIComponent(match[1]) : 'unknown';
    }

    // Nothing is drawn yet: no module ships a client surface at this point. The runtime exists so
    // that delivery is proven end to end before anything depends on it.
    if (window.console && window.console.debug) {
        window.console.debug('[Flynn] client runtime ' + version() + ' loaded');
    }
})();
