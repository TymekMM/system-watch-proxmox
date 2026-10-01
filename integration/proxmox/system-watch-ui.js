/* System-Watch UI asset shared by the reviewed external Summary bridges. */
Ext.define('PVE.node.SystemWatchPanel', {
    extend: 'Ext.container.Container',
    alias: 'widget.pveSystemWatchPanel',
    width: '100%',
    height: 1024,
    minHeight: 170,
    layout: { type: 'vbox', align: 'stretch' },
    listeners: {
        afterrender: function (panel) {
            panel.resizePanels();
        },
        beforedestroy: function (panel) {
            if (panel.settingsWindow) { panel.settingsWindow.close(); }
        },
    },
    items: [
        {
            xtype: 'panel', title: 'Sensors & fans', height: 380, minHeight: 0,
            titlePosition: 0,
            header: { items: [{
                xtype: 'component', itemId: 'swHottest',
                html: 'Hottest component: —', maxWidth: 600, margin: '0 8 0 0',
                style: 'white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:13px;',
            }] },
            margin: '0 0 12px 0', bodyPadding: 12,
            bodyStyle: 'overflow-y:auto;overflow-x:hidden;',
            html: 'Waiting for hardware snapshot…',
            tools: [{ type: 'gear', tooltip: 'Display settings', callback: panel => panel.ownerCt.openSettings() }],
        },
        {
            xtype: 'panel', title: 'Storage & disks', height: 632, minHeight: 0,
            titlePosition: 0,
            header: { items: [{
                xtype: 'component', itemId: 'swZfsHealth',
                html: 'ZFS health: —', maxWidth: 600, margin: '0 8 0 0',
                style: 'white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:13px;',
            }] },
            bodyPadding: 12, bodyStyle: 'overflow-y:auto;overflow-x:hidden;',
            html: 'Waiting for hardware snapshot…',
            tools: [{ type: 'gear', tooltip: 'Display settings', callback: panel => panel.ownerCt.openSettings() }],
        },
    ],

    displayOptions: function () {
        return [
            ['overview', 'System overview'], ['platform', 'Platform temperatures'],
            ['cpu', 'CPU temperatures'], ['memory', 'Memory temperatures'],
            ['network', 'Network temperatures'], ['other', 'Other temperatures'],
            ['fans', 'Fans'], ['sources', 'Collection-source details'],
            ['hba', 'HBA temperatures'], ['nvme', 'NVMe temperatures'],
            ['disks', 'SATA / SAS temperatures'], ['zfs', 'ZFS pools'],
            ['outside', 'Disks outside ZFS'],
        ];
    },

    defaultPreferences: function () {
        let visible = {};
        this.displayOptions().forEach(([key]) => { visible[key] = true; });
        return { version: 1, sensorsHeight: 380, storageHeight: 632, visible: visible };
    },

    normalizePreferences: function (value) {
        let defaults = this.defaultPreferences();
        if (!value || value.version !== 1) { return defaults; }
        ['sensorsHeight', 'storageHeight'].forEach(key => {
            if (typeof value[key] === 'number' && Number.isFinite(value[key])) {
                defaults[key] = Math.max(160, Math.min(1600, Math.round(value[key])));
            }
        });
        this.displayOptions().forEach(([key]) => {
            if (value.visible && typeof value.visible[key] === 'boolean') {
                defaults.visible[key] = value.visible[key];
            }
        });
        return defaults;
    },

    preferenceKey: function () {
        return `system-watch:display:v1:${this.nodename}`;
    },

    readPreferences: function () {
        try {
            return this.normalizePreferences(JSON.parse(localStorage.getItem(this.preferenceKey())));
        } catch (_) {
            return this.defaultPreferences();
        }
    },

    initComponent: function () {
        this.preferences = this.readPreferences();
        this.diskSizes = Object.create(null);
        this.diskSizesDue = 0;
        this.items = this.items.map((item, index) => Object.assign({}, item, {
            height: index === 0 ? this.preferences.sensorsHeight : this.preferences.storageHeight,
        }));
        this.height = this.preferences.sensorsHeight + this.preferences.storageHeight + 12;
        this.callParent(arguments);
    },

    resizePanels: function () {
        let prefs = this.preferences || this.defaultPreferences();
        let total = prefs.sensorsHeight + prefs.storageHeight + 12;
        Ext.suspendLayouts();
        try {
            this.items.getAt(0).setHeight(prefs.sensorsHeight);
            this.items.getAt(1).setHeight(prefs.storageHeight);
            this.setHeight(total);
            if (this.ownerCt) { this.ownerCt.setHeight(total); }
        } finally {
            Ext.resumeLayouts(true);
        }
    },

    applyPreferences: function (value) {
        this.preferences = this.normalizePreferences(value);
        let saved = true;
        try {
            localStorage.setItem(this.preferenceKey(), JSON.stringify(this.preferences));
        } catch (_) { saved = false; }
        this.resizePanels();
        if (this.lastSnapshot) { this.updateSnapshot(this.renderSnapshot(this.lastSnapshot)); }
        this.refreshDiskSizes();
        return saved;
    },

    openSettings: function () {
        let me = this;
        if (me.settingsWindow) { me.settingsWindow.show(); return; }
        let prefs = me.preferences || me.defaultPreferences();
        let viewport = Ext.getBody().getViewSize();
        let heightField = (name, label) => ({
            xtype: 'numberfield', name: name, fieldLabel: label,
            value: prefs[name], minValue: 160, maxValue: 1600,
            allowBlank: false, allowDecimals: false, step: 20,
        });
        let checks = me.displayOptions().map(([key, label]) => ({
            xtype: 'checkboxfield', name: key, boxLabel: label, checked: prefs.visible[key],
        }));
        let form = Ext.create('Ext.form.Panel', {
            bodyPadding: 12, border: false, bodyStyle: 'overflow-y:auto;overflow-x:hidden;',
            defaults: { anchor: '100%', labelWidth: 160 },
            items: [
                { xtype: 'displayfield', value: 'Show/hide sections in this browser for this node. Collection continues; health includes all sources.' },
                { xtype: 'fieldset', title: 'Panel heights (pixels)', items: [
                    heightField('sensorsHeight', 'Sensors & fans'), heightField('storageHeight', 'Storage & disks'),
                ] },
                { xtype: 'fieldset', title: 'Show sections', items: checks },
                { xtype: 'displayfield', value: 'System Watch by TymekMM · AGPL-3.0 · No warranty. <a href="https://github.com/TymekMM/system-watch-proxmox" target="_blank" rel="noopener noreferrer">Source and license</a>' },
                { xtype: 'displayfield', value: '<a href="https://buymeacoffee.com/tymekmm" target="_blank" rel="noopener noreferrer">Buy me a coffee</a> (optional support)' },
                { xtype: 'displayfield', itemId: 'saveStatus', value: '' },
            ],
        });
        let win = Ext.create('Ext.window.Window', {
            title: 'System Watch — Display settings', modal: true, layout: 'fit',
            width: Math.min(620, viewport.width - 24), height: Math.min(760, viewport.height - 24),
            resizable: true, items: [form],
            buttons: [
                { text: 'Restore defaults', handler: () => {
                    let defaults = me.defaultPreferences();
                    ['sensorsHeight', 'storageHeight'].forEach(key => form.down(`[name=${key}]`).setValue(defaults[key]));
                    me.displayOptions().forEach(([key]) => form.down(`[name=${key}]`).setValue(true));
                    form.down('#saveStatus').setValue('Defaults selected. Click Apply to use them.');
                } },
                { text: 'Apply', handler: () => {
                    if (!form.getForm().isValid()) { return; }
                    let value = { version: 1, visible: {} };
                    ['sensorsHeight', 'storageHeight'].forEach(key => { value[key] = form.down(`[name=${key}]`).getValue(); });
                    me.displayOptions().forEach(([key]) => { value.visible[key] = form.down(`[name=${key}]`).getValue(); });
                    let saved = me.applyPreferences(value);
                    form.down('#saveStatus').setValue(saved ? 'Applied and saved in this browser.' : 'Applied for this page; browser storage is unavailable.');
                } },
                { text: 'Close', handler: () => win.close() },
            ],
            listeners: { destroy: () => { me.settingsWindow = null; } },
        });
        me.settingsWindow = win;
        win.show();
    },

    encode: function (value) {
        return Ext.htmlEncode(value === null || value === undefined ? '—' : String(value));
    },

    emphasize: function (value) {
        return `<strong style="font-weight:800!important;text-shadow:0.3px 0 currentColor">${this.encode(value)}</strong>`;
    },

    healthColor: function (state) {
        let allowed = { ok: '#328a40', warning: '#b07a00', critical: '#b22b33', unknown: '#777' };
        let key = Object.prototype.hasOwnProperty.call(allowed, state) ? state : 'unknown';
        return allowed[key];
    },

    badge: function (state, coverage) {
        let key = ['ok', 'warning', 'critical'].includes(state) ? state : 'unknown';
        let label = this.encode(key.toUpperCase() + (coverage && coverage !== 'complete' ? ` (${coverage})` : ''));
        return `<strong style="color:${this.healthColor(key)}">${label}</strong>`;
    },

    size: function (bytes) {
        if (bytes === null || bytes === undefined || !/^\d+$/.test(String(bytes))) {
            return '—';
        }
        // Large counters remain decimal strings in the API; convert only after scaling.
        let value = BigInt(bytes);
        let gib = 1024n * 1024n * 1024n;
        return `${(Number(value * 10n / gib) / 10).toFixed(1)} GiB`;
    },

    number: function (value, digits) {
        return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(digits) : '—';
    },

    temperature: function (item) {
        if (!item) { return '—'; }
        let reading = item.reading || {};
        let current = reading.freshness === 'fresh' && item.value_celsius !== null;
        let value = item.value_celsius === null ? '—' : `${this.number(item.value_celsius, 1)} °C`;
        let state = current ? item.health : 'unknown';
        return `${this.badge(state)} ${this.emphasize(value)}${current ? '' : ` <small>(${this.encode(reading.freshness || 'unavailable')})</small>`}`;
    },

    driveTemperature: function (disk, byTemp) {
        if (!disk) { return null; }
        let readings = (disk.temperature_ids || []).map(id => byTemp[id]).filter(t =>
            t && t.reading && t.reading.freshness === 'fresh' &&
            typeof t.value_celsius === 'number' && Number.isFinite(t.value_celsius));
        // NVMe has Composite and additional sensors; expose the hottest per drive.
        return readings.reduce((max, t) => !max || t.value_celsius > max.value_celsius ? t : max, null);
    },

    temperatureChip: function (item, disk) {
        let value = item ? item.value_celsius : null;
        let label = value === null ? '—' : this.number(value, 1);
        let device = disk && (disk.path || disk.model || disk.id) || 'Unmapped disk';
        let hint = `${device} · ${item ? item.label : 'temperature unavailable'}`;
        return `<strong style="font-weight:800!important;text-shadow:0.3px 0 currentColor;color:${this.healthColor(item ? item.health : 'unknown')}" title="${this.encode(hint)}">${this.encode(label)}</strong>`;
    },

    capacitySize: function (bytes) {
        if (typeof bytes === 'number' && (!Number.isSafeInteger(bytes) || bytes < 0)) { return '—'; }
        if (bytes === null || bytes === undefined || !/^\d+$/.test(String(bytes))) { return '—'; }
        let value = BigInt(bytes);
        let tib = 1024n ** 4n, gib = 1024n ** 3n;
        let unit = value >= tib ? tib : gib;
        let tenths = (value * 10n + unit / 2n) / unit;
        return `${tenths / 10n}.${tenths % 10n} ${value >= tib ? 'TiB' : 'GiB'}`;
    },

    capacityLabel: function (text) {
        let panel = this.items && this.items.getAt && this.items.getAt(1);
        let header = panel && panel.getHeader();
        let title = header && header.el && header.el.dom.querySelector('.x-title-text');
        let color = title ? getComputedStyle(title).color : '#157fcc';
        return `<strong class="sw-capacity" style="font-weight:800!important;color:${this.encode(color)}">${this.encode(text)}</strong>`;
    },

    diskSize: function (disk) {
        let record = (this.diskSizes || {})[disk.path];
        if (!record) { return '—'; }
        let serial = String(disk.serial || '').trim();
        if (serial && record.serial && serial !== record.serial) { return '—'; }
        return this.capacitySize(record.size);
    },

    // Inventory is separate from the two-second sensor refresh; never request SMART.
    refreshDiskSizes: function () {
        let me = this;
        if (!me.activePolling || me.destroyed || me.diskRequestInFlight ||
            !me.preferences.visible.outside || Date.now() < me.diskSizesDue) { return; }
        me.diskRequestInFlight = true;
        me.diskSizesDue = Date.now() + 60000;
        let epoch = me.pollEpoch;
        let finish = records => {
            if (epoch !== me.pollEpoch || me.destroyed) { return; }
            me.diskRequestInFlight = false;
            me.diskSizes = Object.create(null);
            if (Array.isArray(records)) {
                records.forEach(disk => {
                    if (disk && typeof disk.devpath === 'string' && me.capacitySize(disk.size) !== '—') {
                        me.diskSizes[disk.devpath] = { size: disk.size, serial: String(disk.serial || '').trim() };
                    }
                });
            }
            if (me.lastSnapshot) { me.updateSnapshot(me.renderSnapshot(me.lastSnapshot)); }
        };
        Proxmox.Utils.API2Request({
            url: `/nodes/${encodeURIComponent(me.nodename)}/disks/list`,
            method: 'GET',
            params: { skipsmart: 1 },
            success: response => finish(response.result && response.result.data),
            failure: () => finish(null),
        });
    },

    positiveCounter: function (value) {
        return (typeof value === 'number' && Number.isFinite(value) && value > 0) ||
            (typeof value === 'string' && /^\d+$/.test(value) && /[1-9]/.test(value));
    },

    disclosure: function (key, summary, content) {
        return `<details data-sw-key="${this.encode(key)}" style="margin:5px 0">` +
            `<summary style="cursor:pointer;padding:3px 0">${summary}</summary>` +
            `<div style="padding:4px 0 4px 16px">${content}</div></details>`;
    },

    sensorCard: function (key, summary, content) {
        return `<details class="sw-pool-card" data-sw-key="${this.encode(key)}">` +
            `<summary>${summary}</summary><div class="sw-pool-details">${content}</div></details>`;
    },

    // Each Ext panel owns its blue header and independently scrollable body.
    updateSnapshot: function (sections) {
        ['sensors', 'storage'].forEach((name, index) => {
            let panel = this.items.getAt(index);
            let body = panel.body && panel.body.dom;
            let opened = Object.create(null);
            let scroll = body ? body.scrollTop : 0;
            if (body) {
                body.querySelectorAll('details[data-sw-key]').forEach(node => {
                    if (node.open) { opened[node.getAttribute('data-sw-key')] = true; }
                });
            }
            panel.update(sections[name]);
            body = panel.body && panel.body.dom;
            if (body) {
                body.querySelectorAll('details[data-sw-key]').forEach(node => {
                    node.open = !!opened[node.getAttribute('data-sw-key')];
                });
                body.scrollTop = scroll;
            }
        });
        [['hottest', 'swHottest', 'Hottest component'], ['zfsHealth', 'swZfsHealth', 'ZFS health']]
            .forEach(([key, id, label], index) => {
                let header = this.items.getAt(index).getHeader();
                let feedback = header && header.down(`#${id}`);
                if (feedback) { feedback.update(sections[key] || `${label}: unavailable`); }
            });
    },

    renderSnapshot: function (d) {
        if (!d || d.schema_version !== '1.0.0-draft.2' || !d.snapshot) {
            return { sensors: '<strong>Unsupported snapshot contract</strong>', storage: '', hottest: 'Hottest component: unavailable', zfsHealth: 'ZFS health: unavailable' };
        }
        let visible = (this.preferences || this.defaultPreferences()).visible;
        // The authenticated API validates freshness against the node's clock.
        // Browser clocks may differ, so do not reject a valid API response here.
        let temps = Array.isArray(d.temperatures) ? d.temperatures : [];
        let devices = Array.isArray(d.devices) ? d.devices : [];
        let pools = d.zfs && Array.isArray(d.zfs.pools) ? d.zfs.pools : [];
        let fans = Array.isArray(d.fans) ? d.fans : [];
        let byTemp = Object.create(null);
        let byDevice = Object.create(null);
        temps.forEach(t => { byTemp[t.id] = t; });
        devices.forEach(dev => { byDevice[dev.id] = dev; });
        let sensorSections = [];
        let storageSections = [];
        let styles = `<style>
            .sw-overview-grid,.sw-pools-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr))}
            .sw-overview-grid{gap:4px 14px}
            .sw-overview-grid>div{min-width:0;overflow-wrap:anywhere}
            .sw-overview-grid>div span:last-child{text-align:right;min-width:0}
            .sw-pools-grid{gap:10px;align-items:start}
            .sw-pool-card{border:1px solid rgba(127,127,127,.5);border-radius:4px;padding:8px;min-width:0;margin:0}
            .sw-pool-card summary{cursor:pointer;overflow-wrap:anywhere}
            .sw-pool-details{padding:6px 0 0 8px;font-size:.96em;overflow-wrap:anywhere}
            .sw-pool-details span{min-width:0;overflow-wrap:anywhere}
            @media(max-width:800px){.sw-overview-grid{grid-template-columns:1fr}}
        </style>`;
        let section = (title, content) => `<section style="margin:9px 0"><h3 style="margin:0 0 5px">${this.encode(title)}</h3>${content}</section>`;
        let row = (name, detail) => `<div style="display:flex;gap:8px;justify-content:space-between;border-bottom:1px solid currentColor;padding:4px 0"><span>${name}</span><span>${detail}</span></div>`;
        let system = d.health && d.health.system || {};
        let zfsHealth = d.health && d.health.zfs || {};
        let hottest = byTemp[d.health && d.health.hottest_temperature_id];
        let loads = d.system && d.system.load_averages || [];
        let uptime = d.system && d.system.uptime_seconds;
        let overview = row('System health', this.badge(system.state, system.coverage)) +
            row('Uptime / load', `${uptime === null || uptime === undefined ? '—' : this.encode(Math.floor(uptime / 3600) + ' h')} · ${loads.map(x => this.encode(this.number(x, 2))).join(' / ')}`);
        if (visible.overview) { sensorSections.push(section('Overview', `<div class="sw-overview-grid">${overview}</div>`)); }

        let categories = [
            ['platform', 'Platform'], ['cpu', 'CPU'], ['memory', 'Memory'],
            ['network', 'Network'], ['hba', 'HBA'], ['nvme', 'NVMe'],
            ['disks', 'SATA / SAS disks'], ['other', 'Other sensors'],
        ];
        let sensorCards = [];
        let storageSensorCards = [];
        let storageCategories = new Set(['hba', 'nvme', 'disks']);
        categories.forEach(([category, title]) => {
            if (!visible[category]) { return; }
            let items = temps.filter(t => t.category === category);
            if (!items.length) { return; }
            let lines = items.map(t => {
                let dev = t.device_id && byDevice[t.device_id];
                let label = dev ? `${this.emphasize(dev.path || dev.model || dev.id)} · ${this.encode(t.label)}` : this.encode(t.label);
                return row(label, this.temperature(t));
            });
            let fresh = t => t.reading && t.reading.freshness === 'fresh' &&
                typeof t.value_celsius === 'number' && Number.isFinite(t.value_celsius);
            let unavailable = items.filter(t => !fresh(t)).length;
            let alert = items.filter(t => t.reading && t.reading.freshness === 'fresh' &&
                (t.health === 'warning' || t.health === 'critical'));
            let hottestReading = items.filter(fresh)
                .sort((a, b) => b.value_celsius - a.value_celsius)[0];
            let state = alert.some(t => t.health === 'critical') ? 'critical' : alert.length ? 'warning' :
                unavailable ? 'unknown' : 'ok';
            let chips = items.map(t => this.temperatureChip(fresh(t) ? t : null,
                t.device_id && byDevice[t.device_id])).join(', ');
            let summary = `${this.emphasize(title)} (${items.length}) · ${this.badge(state)}` +
                (hottestReading ? ` · max ${this.emphasize(`${this.number(hottestReading.value_celsius, 1)} °C`)}` : '') +
                `<span style="display:block;padding-left:16px">[${chips}] °C` +
                (unavailable ? ` · ${unavailable} unavailable` : '') + '</span>';
            (storageCategories.has(category) ? storageSensorCards : sensorCards)
                .push(this.sensorCard(`temp:${category}`, summary, lines.join('')));
        });

        let reporting = fans.filter(f => f.reading && f.reading.freshness === 'fresh' && f.rpm !== null).length;
        let fanAlerts = fans.filter(f => f.reading && f.reading.freshness === 'fresh' &&
            (f.health === 'warning' || f.health === 'critical'));
        let fanState = fanAlerts.some(f => f.health === 'critical') ? 'critical' : fanAlerts.length ? 'warning' :
            reporting === 0 ? 'unknown' : 'ok';
        let fanRows = fans.length ? fans.map(f => {
            let fresh = f.reading && f.reading.freshness === 'fresh';
            let rpm = !fresh || f.rpm === null ? '—' : `${this.number(f.rpm, 0)} RPM`;
            return row(this.encode(f.label), `${this.badge(fresh ? f.health : 'unknown')} ${this.encode(rpm)}`);
        }).join('') : 'No fan channels reported';
        let fanChips = fans.filter(f => f.reading && f.reading.freshness === 'fresh' &&
            typeof f.rpm === 'number' && Number.isFinite(f.rpm))
            .map(f => this.emphasize(this.number(f.rpm, 0))).join(', ');
        if (visible.fans) { sensorCards.push(this.sensorCard('fans',
            `${this.emphasize('Fans')} · ${reporting}/${fans.length} reporting · ${this.badge(fanState)}` +
            `<span style="display:block;padding-left:16px">[${fanChips || '—'}] RPM</span>`, fanRows)); }
        if (sensorCards.length) { sensorSections.push(section('Sensors & fans', `<div class="sw-pools-grid">${sensorCards.join('')}</div>`)); }
        if (storageSensorCards.length) {
            storageSections.push(section('Disk & HBA sensors', `<div class="sw-pools-grid">${storageSensorCards.join('')}</div>`));
        }

        let renderPool = p => {
            let capacity = typeof p.capacity_percent === 'number' && Number.isFinite(p.capacity_percent)
                ? Math.max(0, Math.min(100, p.capacity_percent)) : null;
            let vdevs = Array.isArray(p.vdevs) ? p.vdevs : [];
            let seen = Object.create(null);
            let poolDisks = vdevs.filter(v => v.kind === 'disk').filter(v => {
                let key = v.device_id || v.id;
                if (seen[key]) { return false; }
                seen[key] = true;
                return true;
            }).map(v => ({ disk: v.device_id && byDevice[v.device_id], vdev: v }));
            let diskReadings = poolDisks.map(({ disk }) => this.driveTemperature(disk, byTemp));
            let maxReading = diskReadings.reduce((max, t) =>
                t && (!max || t.value_celsius > max.value_celsius) ? t : max, null);
            let maxDisk = maxReading && poolDisks[diskReadings.indexOf(maxReading)].disk;
            let chips = poolDisks.map(({ disk }, index) => this.temperatureChip(diskReadings[index], disk)).join(', ');
            let missing = diskReadings.filter(t => !t).length;
            let permanent = p.permanent_errors || {};
            let scan = p.scan || {};
            let vdevErrors = vdevs.some(v => ['read', 'write', 'checksum'].some(key =>
                this.positiveCounter((v.errors || {})[key])));
            let notices = [];
            if (permanent.state === 'present' || this.positiveCounter(scan.errors)) {
                notices.push(`<strong style="color:${this.healthColor('critical')}">ZFS data errors</strong>`);
            } else if (vdevErrors) {
                notices.push(`<strong style="color:${this.healthColor('warning')}">ZFS I/O errors</strong>`);
            }
            let temperatureAlert = diskReadings.some(t => t && t.health === 'critical') ? 'critical' :
                diskReadings.some(t => t && t.health === 'warning') ? 'warning' : null;
            if (temperatureAlert) {
                notices.push(`<strong style="color:${this.healthColor(temperatureAlert)}">Drive temperature ${temperatureAlert}</strong>`);
            }
            if (missing) {
                notices.push(`<span style="color:${this.healthColor('unknown')}">${missing} temperature${missing === 1 ? '' : 's'} unavailable</span>`);
            }
            let summary = `${this.emphasize(p.name)} · ${this.badge(p.health)} · ${this.encode(p.state)}` +
                ` · ${this.capacityLabel(this.capacitySize(p.size_bytes))} · ${this.number(capacity, 1)}% used` +
                `<span style="display:block;padding-left:16px">max ${this.temperatureChip(maxReading, maxDisk)} °C · ` +
                `[${chips || '—'}] °C</span>` +
                (notices.length ? `<span style="display:block;padding-left:16px">${notices.join(' · ')}</span>` : '');
            let stats = `Used ${this.size(p.allocated_bytes)} / ${this.size(p.size_bytes)}` +
                ` · Capacity ${capacity === null ? '—' : this.number(capacity, 1) + '%'}` +
                ` · Fragmentation ${this.number(p.fragmentation_percent, 1)}%`;
            let bar = capacity === null ? '' : `<div role="progressbar" aria-valuenow="${capacity}" aria-valuemin="0" aria-valuemax="100" style="background:#777;height:7px;margin:6px 0"><div style="background:#4789c7;width:${capacity}%;height:7px"></div></div>`;
            let scanLabel = `${scan.type || 'unknown'}: ${scan.state || 'unknown'}`;
            let scanProgress = scan.state === 'running' && typeof scan.progress_percent === 'number' && Number.isFinite(scan.progress_percent)
                ? Math.max(0, Math.min(100, scan.progress_percent)) : null;
            if (scanProgress !== null) {
                scanLabel += ` · ${this.number(scanProgress, 1)}%`;
            }
            scanLabel += ` · repaired ${this.size(scan.repaired_bytes)}`;
            if (scan.errors !== null && scan.errors !== undefined) { scanLabel += ` · errors ${scan.errors}`; }
            let scanBar = scanProgress === null ? '' : `<div role="progressbar" aria-valuenow="${scanProgress}" aria-valuemin="0" aria-valuemax="100" style="background:#777;height:7px;margin:6px 0"><div style="background:#4789c7;width:${scanProgress}%;height:7px"></div></div>`;
            let ids = Object.create(null);
            vdevs.forEach(v => { ids[v.id] = v; });
            let depth = v => {
                let count = 0, visited = Object.create(null);
                while (v && v.parent_id && ids[v.parent_id] && count < 8 && !visited[v.id]) {
                    visited[v.id] = true;
                    v = ids[v.parent_id];
                    count++;
                }
                return count;
            };
            let leaves = vdevs.filter(v => v.kind !== 'root').map(v => {
                let disk = v.device_id && byDevice[v.device_id];
                let values = disk ? (disk.temperature_ids || []).map(id => byTemp[id]).filter(Boolean) : [];
                let errors = v.errors || {};
                let counters = ['read', 'write', 'checksum'].map(k => `${k}: ${this.encode(errors[k])}`).join(' · ');
                let label = disk ? disk.path || disk.model || v.name : v.name;
                return `<div style="margin-left:${Math.min(8, depth(v)) * 10}px;padding:4px 0;overflow-wrap:anywhere">` +
                    `<div>${this.emphasize(label)} · ${this.encode(v.state)}</div>` +
                    `<div style="padding-left:8px">${counters}${values.length ? '<br>' + values.map(t => `${this.encode(t.label)} ${this.temperature(t)}`).join(' · ') : ''}</div></div>`;
            }).join('');
            return `<details class="sw-pool-card" data-sw-key="${this.encode(`pool:${p.guid || p.name}`)}">` +
                `<summary>${summary}</summary><div class="sw-pool-details">` + `<div>${stats}</div>` + bar + `<div>Scan: ${this.encode(scanLabel)}</div>` + scanBar +
                `<div>Permanent errors: ${this.encode(permanent.state || 'unknown')} (${this.encode(permanent.count)})</div>` +
                (leaves || '<div>No topology available</div>') + '</div></details>';
        };
        if (visible.zfs) { storageSections.push(section(`ZFS storage (${pools.length})`, pools.length ?
            `<div class="sw-pools-grid">${pools.map(renderPool).join('')}</div>` : 'No pools available')); }
        let outside = devices.filter(dev => dev.zfs_membership === 'nonmember');
        outside.sort((a, b) => (b.usage === 'system') - (a.usage === 'system'));
        let renderOutside = dev => {
            let readings = (dev.temperature_ids || []).map(id => byTemp[id]).filter(Boolean);
            let hottestDisk = this.driveTemperature(dev, byTemp);
            let current = hottestDisk ? `${this.temperatureChip(hottestDisk, dev)} °C` : this.temperatureChip(null, dev);
            let summary = `${this.emphasize(dev.path || dev.model || dev.id)} · ${this.capacityLabel(this.diskSize(dev))} · ${this.encode(dev.usage)} · ${this.encode(dev.power_state)}` +
                `<span style="display:block;padding-left:16px">${readings.length > 1 ? 'max ' : 'temp '}${current}</span>`;
            let details = `<div>Model: ${this.emphasize(dev.model)} · ${this.encode(dev.transport)} · ${this.encode(dev.media)}</div>` +
                (readings.length ? readings.map(t => row(this.encode(t.label), this.temperature(t))).join('') :
                    '<div>No temperature channels reported</div>');
            return `<details class="sw-pool-card" data-sw-key="${this.encode(`outside:${dev.id}`)}">` +
                `<summary>${summary}</summary><div class="sw-pool-details">${details}</div></details>`;
        };
        if (visible.outside) { storageSections.push(section(`Outside ZFS (${outside.length})`, outside.length ?
            `<div class="sw-pools-grid">${outside.map(renderOutside).join('')}</div>` : 'No confirmed nonmember disks')); }
        let sources = Array.isArray(d.sources) ? d.sources : [];
        let sourceErrors = sources.filter(s => s.state !== 'ok').length;
        if (visible.sources) { sensorSections.push(this.disclosure('sources', `Collection sources · ${sources.length - sourceErrors}/${sources.length} ok` +
            (sourceErrors ? ` · ${this.badge('warning')}` : ''),
            sources.map(s => row(this.encode(s.id), this.badge(s.state === 'partial' ? 'warning' : s.state === 'ok' ? 'ok' : 'unknown') + ' ' + this.encode(s.state))).join(''))); }
        if (Object.values(visible).some(value => !value)) {
            sensorSections.push('<small>Some sections are hidden. Health includes all collection sources.</small>');
        }
        return {
            sensors: `${styles}<div style="line-height:1.45">${sensorSections.join('')}</div>`,
            storage: `${styles}<div style="line-height:1.45">${storageSections.join('') || '<small>No storage sections selected. Use Display settings to show them.</small>'}</div>`,
            hottest: 'Hottest component: ' + (hottest ? `${this.encode(hottest.label)} · ${this.temperature(hottest)}` : '—'),
            zfsHealth: `ZFS health: ${this.badge(zfsHealth.state, zfsHealth.coverage)}`,
        };
    },

    refresh: function () {
        let me = this;
        if (!me.activePolling || me.requestInFlight || me.destroyed) { return; }
        me.requestInFlight = true;
        let epoch = me.pollEpoch;
        Proxmox.Utils.API2Request({
            url: `/nodes/${encodeURIComponent(me.nodename)}/system-watch`,
            method: 'GET',
            success: response => {
                if (epoch !== me.pollEpoch || me.destroyed) { return; }
                me.requestInFlight = false;
                me.lastSnapshot = response.result.data;
                me.updateSnapshot(me.renderSnapshot(response.result.data));
            },
            failure: response => {
                if (epoch !== me.pollEpoch || me.destroyed) { return; }
                me.requestInFlight = false;
                me.lastSnapshot = null;
                me.updateSnapshot({
                    sensors: `<strong>Hardware snapshot unavailable: ${me.encode(response.htmlStatus || 'request failed')}</strong>`,
                    storage: '',
                    hottest: 'Hottest component: unavailable',
                    zfsHealth: 'ZFS health: unavailable',
                });
            },
        });
        me.refreshDiskSizes();
    },

    startUpdate: function () {
        if (this.activePolling) { return; }
        this.activePolling = true;
        this.pollEpoch = (this.pollEpoch || 0) + 1;
        this.refresh();
        this.pollTask = Ext.TaskManager.start({ run: this.refresh, scope: this, interval: 2000 });
    },

    stopUpdate: function () {
        this.activePolling = false;
        this.pollEpoch = (this.pollEpoch || 0) + 1;
        this.requestInFlight = false;
        this.diskRequestInFlight = false;
        this.diskSizes = Object.create(null);
        this.diskSizesDue = 0;
        if (this.pollTask) {
            Ext.TaskManager.stop(this.pollTask);
            this.pollTask = null;
        }
    },
});
