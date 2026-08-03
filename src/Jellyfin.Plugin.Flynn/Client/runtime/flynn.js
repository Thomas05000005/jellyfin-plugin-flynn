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

    var VERSION = '0.1.0';

    // Nothing is drawn yet: no module ships a client surface at this point. The runtime exists so
    // that delivery is proven end to end before anything depends on it.
    if (window.console && window.console.debug) {
        window.console.debug('[Flynn] client runtime ' + VERSION + ' loaded');
    }
})();
