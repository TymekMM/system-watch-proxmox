/* Fixed Summary bridge: VM experiment for pve-manager 9.2.20. */
Ext.define('PVE.node.SystemWatchSlot', {
    extend: 'Ext.container.Container',
    width: '100%',
    height: 640,
    minHeight: 170,
    margin: '0 0 12px 0',
    html: 'Loading System Watch…',

    startUpdate: function () {
        if (this.activePolling) { return; }
        this.activePolling = true;
        let token = this.loadToken = (this.loadToken || 0) + 1;
        if (this.childPanel) {
            this.childPanel.startUpdate();
            return;
        }
        let bundle = PVE.SystemWatchBundle || (PVE.SystemWatchBundle = {});
        if (!bundle.promise) {
            bundle.promise = new Promise((resolve, reject) => {
                let script = document.createElement('script');
                script.src = `/pve2/js/system-watch-ui.js?load=${Date.now()}`;
                script.onload = () => {
                    if (Ext.ClassManager.get('PVE.node.SystemWatchPanel')) {
                        resolve();
                    } else {
                        reject(new Error('System Watch UI did not register'));
                    }
                };
                script.onerror = () => reject(new Error('System Watch UI could not be loaded'));
                document.head.appendChild(script);
            }).catch(error => {
                bundle.promise = null; // allow retry after navigating away and back
                throw error;
            });
        }
        bundle.promise.then(() => {
            if (!this.activePolling || this.destroyed || token !== this.loadToken) { return; }
            this.update('');
            this.childPanel = Ext.create('PVE.node.SystemWatchPanel', { nodename: this.nodename });
            this.add(this.childPanel);
            this.childPanel.startUpdate();
        }).catch(() => {
            if (this.activePolling && !this.destroyed && token === this.loadToken) {
                this.update('System Watch UI unavailable');
            }
        });
    },

    stopUpdate: function () {
        this.activePolling = false;
        this.loadToken = (this.loadToken || 0) + 1;
        if (this.childPanel) { this.childPanel.stopUpdate(); }
    },
});
