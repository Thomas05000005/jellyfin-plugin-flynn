/*
 * Flynn admin page.
 *
 * PURE ASCII ONLY, same rule as the injected runtime: use \uXXXX escapes. A CI gate enforces it.
 *
 * Everything on this page is rendered from what the server reports, never from a hard-coded list
 * of modules. Adding a module must not mean editing this file.
 */
(function () {
    'use strict';

    var API = '/Flynn';

    function request(path, options) {
        var opts = options || {};
        opts.headers = opts.headers || {};
        // ApiClient exists on the Jellyfin admin page and holds the session token; without it
        // every call here would come back 401.
        return window.ApiClient.ajax({
            type: opts.type || 'GET',
            url: window.ApiClient.getUrl(path.replace(/^\//, '')),
            dataType: opts.dataType === null ? undefined : 'json'
        });
    }

    function text(value) {
        var node = document.createElement('span');
        node.textContent = value == null ? '' : String(value);
        return node.innerHTML;
    }

    function relative(iso) {
        if (!iso) {
            return '';
        }
        var seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (seconds < 90) {
            return 'just now';
        }
        if (seconds < 5400) {
            return Math.round(seconds / 60) + ' min ago';
        }
        if (seconds < 172800) {
            return Math.round(seconds / 3600) + ' h ago';
        }
        return Math.round(seconds / 86400) + ' d ago';
    }

    function renderModules(page, data) {
        var host = page.querySelector('#flynnModuleCards');
        if (!host) {
            return;
        }

        if (!data.StorageReady) {
            host.innerHTML =
                '<div class="flynn-card flynn-failed">' +
                '<div class="flynn-card-head">Storage unavailable</div>' +
                '<div class="flynn-card-headline">' + text(data.StorageFailure) + '</div>' +
                '<div class="flynn-card-detail">Modules that need storage will report as unavailable. ' +
                'The rest of the server is unaffected.</div>' +
                '</div>';
            return;
        }

        if (!data.Modules || data.Modules.length === 0) {
            host.innerHTML =
                '<p class="fieldDescription">No modules are installed yet. ' +
                'Flynn is running and has nothing to show.</p>';
            return;
        }

        host.innerHTML = data.Modules.map(function (card) {
            return '<div class="flynn-card flynn-' + text(card.State.toLowerCase()) + '">' +
                '<div class="flynn-card-head">' + text(card.Name) +
                '<span class="flynn-state">' + text(card.State) + '</span></div>' +
                '<div class="flynn-card-headline">' + text(card.Headline) + '</div>' +
                (card.Detail ? '<div class="flynn-card-detail">' + text(card.Detail) + '</div>' : '') +
                '<div class="flynn-card-foot">' + text(card.Summary) +
                ' \u00B7 ' + text(relative(card.GeneratedAt)) + '</div>' +
                '</div>';
        }).join('');
    }

    function renderIssues(page, inbox) {
        var host = page.querySelector('#flynnIssues');
        if (!host) {
            return;
        }

        var withheld = [];
        // Dismissal is permanent, so the count is the only thing standing between a deliberate
        // hide and a blind spot. It is shown even when it is the only thing to show.
        if (inbox.Dismissed) {
            withheld.push(inbox.Dismissed + ' dismissed');
        }
        if (inbox.Snoozed) {
            withheld.push(inbox.Snoozed + ' snoozed');
        }
        if (inbox.Resolved) {
            withheld.push(inbox.Resolved + ' resolved');
        }
        var footer = withheld.length
            ? '<p class="fieldDescription flynn-withheld">' + text(withheld.join(' \u00B7 ')) + '</p>'
            : '';

        if (!inbox.Open || inbox.Open.length === 0) {
            host.innerHTML = '<p class="fieldDescription">Nothing needs your attention.</p>' + footer;
            return;
        }

        host.innerHTML = inbox.Open.map(function (issue) {
            return '<div class="flynn-issue flynn-' + text(issue.Severity.toLowerCase()) + '">' +
                '<div class="flynn-issue-title">' + text(issue.Title) + '</div>' +
                (issue.Detail ? '<div class="flynn-issue-detail">' + text(issue.Detail) + '</div>' : '') +
                '<div class="flynn-issue-foot">' + text(issue.ModuleId) +
                ' \u00B7 first seen ' + text(relative(issue.FirstSeen)) + '</div>' +
                '</div>';
        }).join('') + footer;
    }

    function load(page) {
        request(API + '/modules').then(function (data) {
            renderModules(page, data);
        }, function () {
            var host = page.querySelector('#flynnModuleCards');
            if (host) {
                host.innerHTML = '<p class="fieldDescription">Could not reach Flynn. ' +
                    'Check the server log.</p>';
            }
        });

        request(API + '/issues').then(function (inbox) {
            renderIssues(page, inbox);
        }, function () {
            // A 503 here is expected while storage is down; the module panel already explains it.
            var host = page.querySelector('#flynnIssues');
            if (host) {
                host.innerHTML = '';
            }
        });
    }

    document.addEventListener('pageshow', function (event) {
        var page = event.target;
        if (page && page.id === 'FlynnConfigPage') {
            load(page);
        }
    });
})();
