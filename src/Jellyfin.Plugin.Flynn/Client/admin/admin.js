/*
 * Flynn admin page.
 *
 * PURE ASCII ONLY, same rule as the injected runtime: use \uXXXX escapes. A CI gate enforces it.
 *
 * Everything here is rendered from what the server reports, including its own labels. Adding a
 * module must not mean editing this file, and neither must adding a language.
 */
(function () {
    'use strict';

    var API = '/Flynn';
    var K = {};

    function call(path, method) {
        return window.ApiClient.ajax({
            type: method || 'GET',
            url: window.ApiClient.getUrl(path.replace(/^\//, '')),
            dataType: method === 'POST' ? undefined : 'json'
        });
    }

    function t(key, value) {
        var text = K[key] || '[' + key + ']';
        return value === undefined ? text : text.replace('{0}', value);
    }

    /*
     * Escapes for BOTH text content and attribute values, which is why the quotes are handled by
     * hand rather than left to the serialiser.
     *
     * Setting textContent and reading innerHTML back escapes & < > and nothing else -- the
     * serialiser has no reason to touch a quote inside a text node. That is fine until the same
     * function is used inside an attribute, which it is in five places here. A value containing a
     * double quote then closes the attribute early: the rest is parsed as further attributes, so
     * data-flynn-fp silently truncates and, with the right text, an event handler appears on the
     * element and actually runs.
     *
     * No module produces such a subject today -- a device id is major:minor or zfs:pool. But the
     * documented contract for a subject is "a device id, an album, a path", and an album called
     * "Heroes" is written with the quotes.
     */
    function esc(value) {
        var node = document.createElement('span');
        node.textContent = value == null ? '' : String(value);
        return node.innerHTML.replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function relative(iso) {
        if (!iso) {
            return '';
        }
        var seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (seconds < 90) {
            return t('ui.just-now');
        }
        if (seconds < 5400) {
            return t('ui.minutes-ago', Math.round(seconds / 60));
        }
        if (seconds < 172800) {
            return t('ui.hours-ago', Math.round(seconds / 3600));
        }
        return t('ui.days-ago', Math.round(seconds / 86400));
    }

    function stateLabel(state) {
        return t('ui.state.' + String(state).toLowerCase());
    }

    function card(module) {
        var state = String(module.State).toLowerCase();
        return '' +
            '<div class="flynn-card flynn-' + esc(state) + '" data-module="' + esc(module.Id) + '">' +
              '<div class="flynn-card-top">' +
                '<div>' +
                  '<div class="flynn-card-name">' + esc(module.Name) + '</div>' +
                  '<div class="flynn-card-summary">' + esc(module.Summary) + '</div>' +
                '</div>' +
                '<label class="flynn-switch" title="' + esc(stateLabel(module.State)) + '">' +
                  '<input type="checkbox"' + (module.Enabled ? ' checked' : '') + ' />' +
                  '<span class="flynn-slider"></span>' +
                '</label>' +
              '</div>' +
              '<div class="flynn-card-headline">' + esc(module.Headline) + '</div>' +
              (module.Detail ? '<div class="flynn-card-detail">' + esc(module.Detail) + '</div>' : '') +
              '<div class="flynn-card-foot">' +
                '<span class="flynn-dot"></span>' + esc(stateLabel(module.State)) +
                '<span class="flynn-sep"></span>' + esc(relative(module.GeneratedAt)) +
              '</div>' +
            '</div>';
    }

    function bindToggles(page) {
        page.querySelectorAll('.flynn-card[data-module] input[type=checkbox]').forEach(function (input) {
            input.addEventListener('change', function () {
                var id = input.closest('[data-module]').getAttribute('data-module');
                input.disabled = true;
                call(API + '/modules/' + encodeURIComponent(id) + '/enabled?enabled=' + input.checked, 'POST')
                    .then(function () {
                        // Re-read rather than patch the DOM: switching a module on changes its
                        // state, its headline and its timestamp, and guessing all three here is
                        // how the page starts disagreeing with the server.
                        loadModules(page);
                    }, function () {
                        input.checked = !input.checked;
                        input.disabled = false;
                        window.Dashboard && window.Dashboard.alert(t('ui.toggle-failed'));
                    });
            });
        });
    }

    function loadModules(page) {
        var host = page.querySelector('#flynnModuleCards');
        if (!host) {
            return;
        }

        call(API + '/modules').then(function (data) {
            if (!data.StorageReady) {
                host.innerHTML =
                    '<div class="flynn-card flynn-degraded">' +
                      '<div class="flynn-card-name">' + esc(t('ui.storage-down')) + '</div>' +
                      '<div class="flynn-card-headline">' + esc(data.StorageFailure) + '</div>' +
                      '<div class="flynn-card-detail">' + esc(t('ui.storage-down-detail')) + '</div>' +
                    '</div>';
                return;
            }

            if (!data.Modules || data.Modules.length === 0) {
                host.innerHTML = '<p class="fieldDescription">' + esc(t('ui.no-modules')) + '</p>';
                return;
            }

            // Grouped in a fixed order rather than in whatever order the server enumerated, so a
            // module appearing or disappearing never reshuffles the shelves around it.
            var ORDER = ['Operations', 'System', 'Library', 'Music', 'People'];
            var groups = {};
            data.Modules.forEach(function (m) {
                (groups[m.Category] = groups[m.Category] || []).push(m);
            });

            host.innerHTML = ORDER.filter(function (name) {
                return groups[name] && groups[name].length;
            }).map(function (name) {
                return '<section class="flynn-cat">' +
                    '<h4 class="flynn-cat-title">' + esc(t('ui.cat.' + name.toLowerCase())) +
                    '<span class="flynn-cat-count">' + groups[name].length + '</span></h4>' +
                    '<div class="flynn-grid">' + groups[name].map(card).join('') + '</div>' +
                    '</section>';
            }).join('');

            bindToggles(page);
        }, function () {
            host.innerHTML = '<p class="fieldDescription">' + esc(t('ui.unreachable')) + '</p>';
        });
    }

    /*
     * The fingerprint goes in the query string, never in the path. It is built as
     * module/kind/subject and the subject is whatever the module names -- a device id like
     * zfs:RAID-Z1, an album, a path -- so it contains slashes that no single route segment can
     * carry.
     */
    function issueAction(page, fingerprint, action, query) {
        var url = API + '/issues/' + action + '?fingerprint=' + encodeURIComponent(fingerprint) +
            (query || '');
        return call(url, 'POST').then(function () {
            loadIssues(page);
        }, function () {
            window.Dashboard && window.Dashboard.alert(t('ui.issue-action-failed'));
            loadIssues(page);
        });
    }

    function bindIssueActions(page) {
        page.querySelectorAll('#flynnIssues [data-flynn-act]').forEach(function (button) {
            button.addEventListener('click', function () {
                var act = button.getAttribute('data-flynn-act');
                var fingerprint = button.closest('[data-flynn-fp]').getAttribute('data-flynn-fp');

                // Dismissal never expires. Anything that cannot be undone by doing it again gets
                // asked about first; snooze does not, because waiting a week undoes it.
                if (act === 'dismiss' && !window.confirm(t('ui.dismiss-confirm'))) {
                    return;
                }

                page.querySelectorAll('#flynnIssues button').forEach(function (b) {
                    b.disabled = true;
                });
                issueAction(page, fingerprint, act, act === 'snooze' ? '&days=7' : '');
            });
        });
    }

    function issueActions() {
        return '<div class="flynn-issue-acts">' +
            '<button is="emby-button" type="button" class="raised flynn-issue-act"' +
              ' data-flynn-act="snooze">' + esc(t('ui.snooze-7')) + '</button>' +
            '<button is="emby-button" type="button" class="raised flynn-issue-act"' +
              ' data-flynn-act="dismiss">' + esc(t('ui.dismiss')) + '</button>' +
            '</div>';
    }

    function issueRow(issue, actions) {
        var withheldFor = issue.State === 'Open'
            ? ''
            : '<span class="flynn-sep"></span>' + esc(t('ui.state.' + String(issue.State).toLowerCase()));

        return '<div class="flynn-issue flynn-' + esc(String(issue.Severity).toLowerCase()) + '"' +
            ' data-flynn-fp="' + esc(issue.Fingerprint) + '">' +
            '<div class="flynn-issue-title">' + esc(issue.Title) + '</div>' +
            (issue.Detail ? '<div class="flynn-issue-detail">' + esc(issue.Detail) + '</div>' : '') +
            '<div class="flynn-issue-foot">' + esc(issue.ModuleId) +
            '<span class="flynn-sep"></span>' + esc(relative(issue.FirstSeen)) + withheldFor +
            '</div>' + actions +
            '</div>';
    }

    function loadIssues(page) {
        var host = page.querySelector('#flynnIssues');
        if (!host) {
            return;
        }

        call(API + '/issues').then(function (inbox) {
            var withheld = [];
            // Dismissal is permanent, so this count is the only thing between a deliberate hide
            // and a blind spot. Shown even when it is all there is to show.
            if (inbox.Dismissed) {
                withheld.push(t('ui.withheld.dismissed', inbox.Dismissed));
            }
            if (inbox.Snoozed) {
                withheld.push(t('ui.withheld.snoozed', inbox.Snoozed));
            }
            if (inbox.Resolved) {
                withheld.push(t('ui.withheld.resolved', inbox.Resolved));
            }
            // A count of what is hidden, with no way to see WHICH, is only a slightly better blind
            // spot. The summary opens onto the list, each row with a way back.
            var hidden = inbox.Withheld || [];
            var foot = '';
            if (withheld.length) {
                foot = '<details class="flynn-withheld">' +
                    '<summary>' + esc(withheld.join('  \u00B7  ')) + '</summary>' +
                    (hidden.length
                        ? hidden.map(function (issue) {
                            return issueRow(issue,
                                '<button is="emby-button" type="button" class="raised flynn-issue-act"' +
                                ' data-flynn-act="restore">' + esc(t('ui.restore')) + '</button>');
                        }).join('')
                        : '<p class="fieldDescription">' + esc(t('ui.withheld-resolved-only')) + '</p>') +
                    '</details>';
            }

            host.innerHTML = (!inbox.Open || inbox.Open.length === 0)
                ? '<p class="fieldDescription flynn-clear">' + esc(t('ui.nothing-to-do')) + '</p>' + foot
                : inbox.Open.map(function (issue) {
                    return issueRow(issue, issueActions());
                }).join('') + foot;

            bindIssueActions(page);
        }, function () {
            // 503 while storage is down is expected; the module panel already explains it.
            host.innerHTML = '';
        });
    }

    function load(page) {
        // Labels first: everything rendered afterwards needs them, and rendering before they
        // arrive is what puts [ui.just-now] on screen for a second.
        call(API + '/strings').then(function (strings) {
            K = strings || {};
            // Section titles live in the HTML so the page is not blank before the fetch returns;
            // they carry their key and get replaced once the catalogue is in.
            page.querySelectorAll('[data-flynn-label]').forEach(function (node) {
                node.textContent = t(node.getAttribute('data-flynn-label'));
            });
            loadModules(page);
            loadIssues(page);
        }, function () {
            loadModules(page);
            loadIssues(page);
        });
    }

    document.addEventListener('pageshow', function (event) {
        var page = event.target;
        if (page && page.id === 'FlynnConfigPage') {
            load(page);
        }
    });
})();
