const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

let slotDefinition;
let bundleRegistered = false;
let scripts = [];
let children = [];
const context = {
    Date,
    Promise,
    PVE: {},
    document: {
        createElement: () => ({ src: '', onload: null, onerror: null }),
        head: { appendChild: script => scripts.push(script) },
    },
    Ext: {
        define: (_name, definition) => { slotDefinition = definition; },
        ClassManager: { get: () => bundleRegistered ? {} : null },
        create: (_name, options) => {
            let child = {
                nodename: options.nodename, starts: 0, stops: 0,
                startUpdate() { this.starts++; },
                stopUpdate() { this.stops++; },
            };
            children.push(child);
            return child;
        },
    },
};
let source = fs.readFileSync(path.join(__dirname, '..', 'profiles/system-watch-bridge.js'), 'utf8');
vm.runInNewContext(source, context);
const slot = nodename => Object.assign({}, slotDefinition, {
    nodename, update(value) { this.html = value; },
    add(child) { this.attached = child; },
});
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };

(async () => {
    const first = slot('node-a');
    const second = slot('node-b');
    first.startUpdate();
    second.startUpdate();
    assert.equal(scripts.length, 1, 'one script shared across Summary instances');
    assert.ok(scripts[0].src.startsWith('/pve2/js/system-watch-ui.js?load='));
    first.stopUpdate();
    bundleRegistered = true;
    scripts[0].onload();
    await settle();
    assert.equal(children.length, 1, 'stopped Summary does not mount after delayed load');
    assert.equal(second.attached.nodename, 'node-b');
    assert.equal(second.attached.starts, 1);
    first.startUpdate();
    await settle();
    assert.equal(children.length, 2);
    assert.equal(first.attached.nodename, 'node-a');
    second.stopUpdate();
    second.stopUpdate();
    assert.equal(second.attached.stops, 2, 'underlying panel stop is idempotent');
    second.destroyed = true;
    console.log('shared loader, late navigation, and panel lifecycle: OK');
})().catch(error => { console.error(error); process.exitCode = 1; });
