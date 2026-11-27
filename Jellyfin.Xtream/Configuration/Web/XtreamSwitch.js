export default function (view) {
  view.addEventListener("viewshow", () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    let currentBaseUrl = '';
    ApiClient.getPluginConfiguration(pluginId).then((config) => currentBaseUrl = config.BaseUrl);

    const iptvEnabled = view.querySelector('#IptvEnabled');
    const iptvState = view.querySelector('#IptvConnectionState');
    const showConnection = (enabled) => {
      iptvEnabled.checked = enabled;
      iptvState.textContent = enabled
        ? 'Connected: the provider is in use.'
        : 'Switched off: the provider is not contacted.';
      view.querySelector('#IptvConnection').classList.toggle('xtream-connection-off', !enabled);
    };
    Xtream.fetchJson('Xtream/Connection').then((r) => showConnection(r.Enabled));

    iptvEnabled.onchange = () => {
      const enabled = iptvEnabled.checked;
      if (!enabled && !confirm('Switch off the IPTV connection? Playing streams stop and all provider items are removed until it is switched on again.')) {
        iptvEnabled.checked = true;
        return;
      }

      Dashboard.showLoadingMsg();
      ApiClient.fetch({
        dataType: 'json',
        type: 'POST',
        url: ApiClient.getUrl('Xtream/Connection', { enabled }),
      }).then((r) => {
        showConnection(r.Enabled);
        showSecurity(false);
        Dashboard.hideLoadingMsg();
        Dashboard.alert(r.Enabled
          ? 'IPTV connection switched on. The guide and channels are being rescanned.'
          : 'IPTV connection switched off.');
      }).catch(() => {
        Dashboard.hideLoadingMsg();
        Dashboard.alert('Failed to change the IPTV connection. Check the server logs.');
        Xtream.fetchJson('Xtream/Connection').then((r) => showConnection(r.Enabled));
      });
    };

    const securityHttps = view.querySelector('#SecurityHttps');
    const securityLan = view.querySelector('#SecurityLan');
    const showSecurity = (recheck) => {
      securityLan.textContent = recheck ? 'Checking…' : securityLan.textContent;
      return ApiClient.fetch({
        dataType: 'json',
        type: 'GET',
        url: ApiClient.getUrl('Xtream/Security', { recheck: !!recheck }),
      }).then((r) => {
        securityHttps.textContent = r.ProviderUsesHttps ? '✅ Encrypted (HTTPS)' : '⚠️ Not encrypted (HTTP)';
        securityLan.textContent = r.LanReachable === true ? '⚠️ Reachable'
          : r.LanReachable === false ? '✅ Blocked' : 'Unknown';
        securityLan.title = r.LanDetail || '';
      }).catch(() => {
        securityLan.textContent = 'Check failed';
      });
    };
    showSecurity(false);
    view.querySelector('#SecurityLanCheck').onclick = (e) => {
      e.preventDefault();
      showSecurity(true);
    };
    view.querySelector('#SecurityHttpsCheck').onclick = (e) => {
      e.preventDefault();
      const post = (apply) => ApiClient.fetch({
        dataType: 'json',
        type: 'POST',
        url: ApiClient.getUrl('Xtream/Security/Https', { apply }),
      });
      Dashboard.showLoadingMsg();
      post(false).then((r) => {
        Dashboard.hideLoadingMsg();
        if (!r.Available || r.Url === currentBaseUrl) {
          Dashboard.alert(r.Detail);
          return;
        }

        if (!confirm(r.Detail + '\n\nSwitch the base URL to ' + r.Url + '?')) {
          return;
        }

        Dashboard.showLoadingMsg();
        post(true).then((applied) => {
          Dashboard.hideLoadingMsg();
          currentBaseUrl = applied.Url || currentBaseUrl;
          showSecurity(false);
          Dashboard.alert(applied.Detail);
        });
      }).catch(() => {
        Dashboard.hideLoadingMsg();
        Dashboard.alert('The HTTPS check failed. Check the server logs.');
      });
    };

  }));
}
