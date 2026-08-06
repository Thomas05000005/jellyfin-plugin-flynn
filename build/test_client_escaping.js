/*
 * Proves the admin page's esc() is safe in an attribute value, against the real shipped source.
 *
 * This exists because it was not. esc() set textContent and read innerHTML back, which escapes
 * & < > and nothing else -- the serialiser has no reason to touch a quote inside a text node. That
 * is correct for text content and wrong for the five places esc() is used inside an attribute: a
 * value containing a double quote closes the attribute early, the rest is parsed as further
 * attributes, and with the right text an event handler appears on the element and runs.
 *
 * An adversarial review reproduced it in a real browser. No module produces such a subject today,
 * but the documented contract for one is "a device id, an album, a path" -- and an album called
 * "Heroes" is written with the quotes, while a macOS mount point may contain them outright.
 *
 * The function source is lifted out of admin.js rather than copied here, so this cannot pass
 * against a version of esc() that no longer ships.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const ADMIN = path.join(
    __dirname, '..', 'src', 'Jellyfin.Plugin.Flynn', 'Client', 'admin', 'admin.js');

const source = fs.readFileSync(ADMIN, 'utf8');
const match = /function esc\(value\) \{[\s\S]*?\n {4}\}/.exec(source);
if (!match) {
    console.error('Could not find esc() in admin.js. If it was renamed, this gate needs updating.');
    process.exit(1);
}

// The smallest DOM that lets the real function run: textContent in, serialised HTML out.
const sandbox = {
    document: {
        createElement() {
            let text = '';
            return {
                set textContent(value) { text = value; },
                get innerHTML() {
                    return text
                        .replace(/&/g, '&amp;')
                        .replace(/</g, '&lt;')
                        .replace(/>/g, '&gt;');
                },
            };
        },
    },
};
vm.createContext(sandbox);
vm.runInContext(`${match[0]}; this.__esc = esc;`, sandbox);
const esc = sandbox.__esc;

const cases = [
    ['music/album-double/"Heroes"', 'an album whose title is written with quotes'],
    ['capacity/capacity//Volumes/Films "4K"', 'a macOS mount point holding a quote'],
    ['m/k/x" onmouseover="alert(1)" y="', 'an attempt to inject an event handler'],
    ["m/k/O'Brien", 'an apostrophe, which single-quoted attributes would end on'],
    ['a<b>&c', 'the characters that already worked, which must keep working'],
];

let failed = 0;
for (const [input, why] of cases) {
    const out = esc(input);
    if (/["']/.test(out)) {
        console.error(`FAIL  ${why}\n      ${input}\n  ->  ${out}`);
        failed += 1;
    } else {
        console.log(`  ok  ${why}`);
    }
}

if (esc('a<b>&c') !== 'a&lt;b&gt;&amp;c') {
    console.error(`FAIL  the original escaping regressed: ${esc('a<b>&c')}`);
    failed += 1;
}

if (failed > 0) {
    console.error(`\n${failed} case(s) leak a quote. Any of them can close an attribute early.`);
    process.exit(1);
}

console.log(`\n${cases.length} case(s): no quote survives, so no attribute can be closed early.`);
