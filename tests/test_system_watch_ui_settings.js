const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..');
const fixture = JSON.parse(fs.readFileSync(path.join(root, 'tests/fixtures/snapshot.example.json'), 'utf8'));
const original = JSON.stringify(fixture);
const stored = new Map();
const requests = [];
let definition, blocked = false, form, windowConfig, validForm = true;
vm.runInNewContext(fs.readFileSync(path.join(root,
    'integration/proxmox/system-watch-ui.js'), 'utf8'), {
    localStorage: {
        getItem: key => { if (blocked) { throw Error('blocked'); } return stored.get(key) || null; },
        setItem: (key, value) => { if (blocked) { throw Error('blocked'); } stored.set(key, value); },
    },
    Ext: {
        define: (name, config) => { assert.equal(name, 'PVE.node.SystemWatchPanel'); definition = config; },
        htmlEncode: value => String(value).replace(/[&<>"']/g, ch =>
            ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]),
        suspendLayouts: () => {}, resumeLayouts: () => {},
        getBody: () => ({ getViewSize: () => ({ width: 1200, height: 900 }) }),
        create: (name, config) => {
            if (name === 'Ext.form.Panel') {
                const fields = new Map();
                const visit = items => items.forEach(item => {
                    let value = item.checked ?? item.value;
                    if (item.name || item.itemId) {
                        fields.set(item.name || item.itemId, {
                            getValue: () => value, setValue: next => { value = next; },
                        });
                    }
                    if (item.items) { visit(item.items); }
                });
                visit(config.items);
                form = {
                    getForm: () => ({ isValid: () => validForm }),
                    down: selector => fields.get(selector.startsWith('#')
                        ? selector.slice(1) : selector.match(/^\[name=(.*)\]$/)[1]),
                };
                return form;
            }
            assert.equal(name, 'Ext.window.Window');
            windowConfig = config;
            return { show: () => {}, close: () => config.listeners.destroy() };
        },
        TaskManager: { start: task => task, stop: () => {} },
    },
    Proxmox: { Utils: { API2Request: request => requests.push(request) } },
});

function makePanel(node = 'node-example') {
    const panel = Object.assign({}, definition, { nodename: node, callParent: () => {} });
    panel.initComponent();
    const children = panel.items.map(() => {
        const card = { open: true, getAttribute: () => 'fans' };
        const dom = { scrollTop: 45, querySelectorAll: () => [card] };
        return {
            body: { dom }, card,
            getHeader: () => ({ down: () => ({
                update: html => { children.find(child => child.body.dom === dom).headerHtml = html; },
            }) }),
            update: html => { dom.scrollTop = 0; card.open = false; children.find(child => child.body.dom === dom).html = html; },
            setHeight: height => { children.find(child => child.body.dom === dom).height = height; },
        };
    });
    panel.items = { getAt: index => children[index] };
    panel.setHeight = height => { panel.height = height; };
    panel.ownerCt = { setHeight: height => { panel.slotHeight = height; } };
    panel.children = children;
    return panel;
}

const panel = makePanel();
definition.listeners.afterrender(panel);
assert.equal(panel.height, 1024);
assert.equal(panel.slotHeight, 1024);
const rendered = panel.renderSnapshot(fixture);
assert.ok(!rendered.sensors.includes('Hottest component'), 'hottest reading moves out of the body');
assert.ok(rendered.hottest.includes('CPU') && rendered.hottest.includes('39.0 °C'));
assert.ok(!rendered.storage.includes('Storage overview') && !rendered.storage.includes('ZFS health'), 'ZFS health moves out of the body');
assert.ok(rendered.zfsHealth.includes('ZFS health:') && rendered.zfsHealth.includes('OK'));
for (const key of ['temp:platform', 'fans', 'sources']) {
    assert.ok(rendered.sensors.includes(`data-sw-key="${key}"`));
}
for (const key of ['temp:disks', 'pool:example-pool', 'outside:disk:example-b']) {
    assert.ok(rendered.storage.includes(`data-sw-key="${key}"`));
}
assert.equal(panel.renderSnapshot({}).storage, '', 'invalid snapshot keeps the two-panel result shape');

panel.lastSnapshot = fixture;
let prefs = panel.defaultPreferences();
prefs.sensorsHeight = 420;
prefs.storageHeight = 800;
for (const key of ['platform', 'fans', 'sources', 'zfs']) { prefs.visible[key] = false; }
assert.equal(panel.applyPreferences(prefs), true);
assert.equal(panel.height, 1232);
assert.equal(panel.slotHeight, 1232, 'parent slot resizes to avoid overlapping Proxmox charts');
assert.equal(panel.children[0].height, 420);
assert.equal(panel.children[1].height, 800);
for (const key of ['temp:platform', 'fans', 'sources']) {
    assert.ok(!panel.children[0].html.includes(`data-sw-key="${key}"`), 'hidden sections disappear immediately');
}
assert.ok(!panel.children[1].html.includes('pool:example-pool'));
assert.ok(panel.children[1].html.includes('outside:disk:example-b'), 'unrelated disk inventory remains');
assert.ok(panel.children[0].html.includes('System health'), 'snapshot health remains visible');
assert.ok(panel.children[0].headerHtml.includes('39.0 °C'), 'header updates from the same snapshot');
assert.ok(panel.children[1].headerHtml.includes('OK'), 'ZFS header remains visible with pools hidden');
panel.preferences.visible.overview = false;
assert.ok(panel.renderSnapshot(fixture).hottest.includes('39.0 °C'), 'hiding Overview does not hide header feedback');
panel.preferences.visible.overview = true;
assert.ok(panel.children.every(child => child.body.dom.scrollTop === 45 && child.card.open), 'refresh retains scroll and expansion');
assert.equal(requests.length, 0, 'display changes need no API request');
assert.equal(JSON.stringify(fixture), original, 'display settings never mutate snapshot data');

assert.equal(makePanel().preferences.visible.fans, false, 'preferences survive a new panel instance');
assert.equal(makePanel('pve-other').preferences.visible.fans, true, 'preferences are scoped per node');
let bounded = panel.normalizePreferences({ version: 1, sensorsHeight: -5, storageHeight: 99999, visible: { fans: 'false' } });
assert.equal(bounded.sensorsHeight, 160);
assert.equal(bounded.storageHeight, 1600);
assert.equal(bounded.visible.fans, true, 'invalid toggle values fall back to defaults');
stored.set(panel.preferenceKey(), '{broken json');
assert.equal(makePanel().preferences.sensorsHeight, 380);
stored.set(panel.preferenceKey(), '{"version":99,"sensorsHeight":777}');
assert.equal(makePanel().preferences.sensorsHeight, 380, 'unknown preference version uses defaults');
blocked = true;
assert.equal(makePanel().preferences.sensorsHeight, 380);
assert.equal(panel.applyPreferences(prefs), false, 'storage refusal still applies settings for this page');
blocked = false;

panel.openSettings();
const currentWindow = panel.settingsWindow;
panel.openSettings();
assert.equal(panel.settingsWindow, currentWindow, 'one settings window per panel');
form.down('[name=storageHeight]').setValue(900);
const apply = windowConfig.buttons.find(button => button.text === 'Apply');
validForm = false;
apply.handler();
assert.equal(panel.preferences.storageHeight, 800, 'invalid form refuses Apply');
validForm = true;
apply.handler();
assert.equal(panel.preferences.storageHeight, 900, 'Apply uses settings window values');
windowConfig.buttons.find(button => button.text === 'Restore defaults').handler();
assert.equal(panel.preferences.storageHeight, 900, 'Restore defaults waits for Apply');
apply.handler();
assert.equal(panel.preferences.storageHeight, 632);
assert.ok(Object.values(panel.preferences.visible).every(Boolean));
definition.listeners.beforedestroy(panel);
assert.equal(panel.settingsWindow, null, 'destroying the view also closes its settings window');

panel.startUpdate();
assert.equal(requests.length, 2);
assert.equal(requests[0].method, 'GET');
requests[0].success({ result: { data: fixture } });
assert.equal(panel.lastSnapshot, fixture);
assert.ok(requests[1].url.endsWith('/disks/list'));
assert.equal(requests[1].params.skipsmart, 1, 'inventory never triggers SMART');
requests[1].success({ result: { data: [
    { devpath: '/dev/sda', size: 1000204886016, serial: 'S5RRNF0WA19192P' },
    { devpath: '/dev/sdb', size: 8001563222016, serial: 'WWZAA8RY' },
    { devpath: '/dev/bad', size: 'bad' },
] } });
assert.equal(panel.diskSize({ path: '/dev/sda', serial: 'S5RRNF0WA19192P' }), '931.5 GiB');
assert.equal(panel.diskSize({ path: '/dev/sdb', serial: 'WWZAA8RY' }), '7.3 TiB');
assert.equal(panel.diskSize({ path: '/dev/sda', serial: 'another-drive' }), '—');
assert.equal(panel.diskSize({ path: '/dev/bad' }), '—');
assert.equal(panel.capacitySize(null), '—');
assert.equal(panel.capacitySize(Number.MAX_SAFE_INTEGER + 1), '—');
let capacityFixture = JSON.parse(JSON.stringify(fixture));
capacityFixture.zfs.pools[0].size_bytes = '8001563222016';
capacityFixture.devices.push({ id: 'capacity-disk', path: '/dev/sda', serial: 'S5RRNF0WA19192P', zfs_membership: 'nonmember', usage: 'system', power_state: 'active' });
let capacityHtml = panel.renderSnapshot(capacityFixture).storage;
assert.ok(capacityHtml.includes('7.3 TiB</strong> ·'), 'pool summary includes total capacity before usage');
assert.match(capacityHtml, /\/dev\/sda<\/strong> · <strong class="sw-capacity"[^>]*>931\.5 GiB<\/strong> · system/, 'outside disk capacity follows its path');
panel.refreshDiskSizes();
assert.equal(requests.length, 2, 'inventory is cached for sixty seconds');
panel.refresh();
requests[2].failure({ htmlStatus: 'offline' });
assert.equal(panel.lastSnapshot, null, 'failed refresh discards cached snapshot');
assert.ok(panel.children[0].html.includes('offline'));
assert.equal(panel.children[0].headerHtml, 'Hottest component: unavailable', 'API failure clears the old header reading');
assert.equal(panel.children[1].headerHtml, 'ZFS health: unavailable', 'API failure clears the old ZFS header health');
panel.applyPreferences(panel.defaultPreferences());
assert.ok(panel.children[0].html.includes('offline'), 'changing settings cannot resurrect stale readings after failure');
panel.diskSizesDue = 0;
panel.refreshDiskSizes();
requests[3].failure({ htmlStatus: 'permission denied' });
assert.equal(panel.diskSize({ path: '/dev/sda' }), '—', 'inventory failure leaves capacity unavailable');
assert.equal(panel.lastSnapshot, null, 'inventory callbacks cannot resurrect failed snapshot');
panel.preferences.visible.outside = false;
panel.diskSizesDue = 0;
panel.refreshDiskSizes();
assert.equal(requests.length, 4, 'hidden outside disks need no inventory request');
panel.preferences.visible.outside = true;
panel.refreshDiskSizes();
panel.stopUpdate();
requests[4].success({ result: { data: [{ devpath: '/dev/sda', size: 1000204886016 }] } });
assert.equal(panel.diskSize({ path: '/dev/sda' }), '—', 'stopped view ignores late inventory response');
console.log('PASS display settings, immediate hide/resize, node persistence, defaults, scroll and read-only polling');
