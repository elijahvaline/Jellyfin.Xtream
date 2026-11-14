export default function (view) {
  const el = (tag, props = {}, children = []) => {
    const e = document.createElement(tag);
    Object.assign(e, props);
    for (const c of children) {
      e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
    }
    return e;
  };

  const createRuleCard = (Xtream, rule, categories, onDelete) => {
    rule.Categories ??= [];
    const selected = new Set(rule.Categories);
    const sync = () => rule.Categories = [...selected];

    const card = el('div', { className: 'dynamic-rule' });

    // Header: keyword, enabled, delete.
    const keyword = el('input', {
      type: 'text',
      placeholder: 'Keyword, e.g. vikings',
      value: rule.Keyword ?? '',
    });
    keyword.setAttribute('is', 'emby-input');
    keyword.oninput = () => rule.Keyword = keyword.value;

    const enabled = el('input', { type: 'checkbox', checked: rule.Enabled !== false });
    enabled.onchange = () => rule.Enabled = enabled.checked;

    const remove = el('button', { type: 'button', className: 'paper-icon-button-light', title: 'Delete rule' }, [
      el('span', { className: 'material-icons delete' }),
    ]);
    remove.onclick = () => onDelete(card);

    card.appendChild(el('div', { className: 'dynamic-rule-header' }, [
      el('div', { className: 'dynamic-rule-keyword' }, [keyword]),
      el('label', { className: 'dynamic-rule-enabled' }, [enabled, el('span', {}, ['Enabled'])]),
      remove,
    ]));

    // Category picker.
    const summary = el('div', { className: 'fieldDescription' });
    const updateSummary = () => {
      summary.textContent = selected.size === 0
        ? 'No categories selected: searching all categories.'
        : `Searching ${selected.size} selected ${selected.size === 1 ? 'category' : 'categories'}.`;
    };
    updateSummary();

    const filter = el('input', { type: 'search', placeholder: 'Filter categories…' });
    filter.setAttribute('is', 'emby-input');
    const list = el('div', { className: 'dynamic-rule-categories' });
    for (const category of categories) {
      const box = el('input', { type: 'checkbox', checked: selected.has(category.Id) });
      box.onchange = () => {
        box.checked ? selected.add(category.Id) : selected.delete(category.Id);
        sync();
        updateSummary();
      };
      const label = el('label', {}, [box, el('span', {}, [category.Name])]);
      label.dataset.name = (category.Name ?? '').toLowerCase();
      list.appendChild(label);
    }
    filter.oninput = () => {
      const q = filter.value.trim().toLowerCase();
      for (const label of list.children) {
        label.style.display = !q || label.dataset.name.includes(q) ? '' : 'none';
      }
    };
    card.appendChild(filter);
    card.appendChild(list);
    card.appendChild(summary);

    // Preview.
    const previewButton = el('button', { type: 'button', className: 'raised emby-button' }, [
      el('span', {}, ['Preview matches']),
    ]);
    previewButton.setAttribute('is', 'emby-button');
    const preview = el('ul', { className: 'dynamic-rule-preview' });
    previewButton.onclick = () => {
      preview.replaceChildren();
      if (!(rule.Keyword ?? '').trim()) {
        preview.appendChild(el('li', {}, ['Enter a keyword first.']));
        return;
      }
      Dashboard.showLoadingMsg();
      const params = new URLSearchParams({ keyword: rule.Keyword.trim(), categories: [...selected].join(',') });
      Xtream.fetchJson(`Xtream/LiveTv/DynamicPreview?${params}`).then((channels) => {
        if (channels.length === 0) {
          preview.appendChild(el('li', {}, ['No channels currently match.']));
        }
        for (const c of channels) {
          preview.appendChild(el('li', {}, [c.Name]));
        }
      }).catch((error) => {
        console.error('Failed to preview dynamic rule:', error);
        preview.appendChild(el('li', {}, ['Failed to load preview.']));
      }).finally(() => Dashboard.hideLoadingMsg());
    };
    card.appendChild(previewButton);
    card.appendChild(preview);

    return card;
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(3);

    const container = view.querySelector('#DynamicRules');
    container.replaceChildren();
    Dashboard.showLoadingMsg();

    Promise.all([
      ApiClient.getPluginConfiguration(pluginId),
      Xtream.fetchJson('Xtream/LiveCategories'),
    ]).then(([config, categories]) => {
      const rules = config.LiveTvDynamic ?? [];

      const addCard = (rule) => {
        const card = createRuleCard(Xtream, rule, categories, (c) => {
          const idx = rules.indexOf(rule);
          if (idx >= 0) {
            rules.splice(idx, 1);
          }
          container.removeChild(c);
        });
        container.appendChild(card);
        return card;
      };
      rules.forEach(addCard);

      view.querySelector('#AddRule').onclick = () => {
        const rule = { Keyword: '', Enabled: true, Categories: [] };
        rules.push(rule);
        addCard(rule).querySelector('input[type="text"]').focus();
      };

      view.querySelector('#XtreamLiveDynamicForm').onsubmit = (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((latest) => {
          latest.LiveTvDynamic = rules
            .map((r) => ({ Keyword: (r.Keyword ?? '').trim(), Enabled: r.Enabled !== false, Categories: r.Categories ?? [] }))
            .filter((r) => r.Keyword.length > 0);
          return ApiClient.updatePluginConfiguration(pluginId, latest);
        }).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
        return false;
      };

      Dashboard.hideLoadingMsg();
    }).catch((error) => {
      console.error('Failed to load dynamic channel configuration:', error);
      Dashboard.hideLoadingMsg();
      const msg = el('div', { className: 'fieldDescription' }, [
        'Failed to load categories. Check that your Xtream credentials are set (Credentials tab) and the server is reachable.',
      ]);
      msg.style.color = '#ff6b6b';
      container.appendChild(msg);
    });
  }));
}
