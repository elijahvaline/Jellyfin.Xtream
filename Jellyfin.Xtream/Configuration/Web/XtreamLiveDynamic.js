export default function (view) {
  const el = (tag, props = {}, children = []) => {
    const e = document.createElement(tag);
    Object.assign(e, props);
    for (const c of children) {
      e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
    }
    return e;
  };
  const icon = (name) => el('span', { className: `material-icons ${name}`, ariaHidden: 'true' });

  // Finds an opaque background colour so the dropdown panel matches the active theme.
  const themeBackground = (node) => {
    for (let n = node; n && n.nodeType === 1; n = n.parentElement) {
      const bg = getComputedStyle(n).backgroundColor;
      if (bg && bg !== 'transparent' && !/rgba\(.*,\s*0\)$/.test(bg)) {
        return bg;
      }
    }
    return getComputedStyle(document.body).backgroundColor || '#202020';
  };

  // A searchable multi-select dropdown. An empty selection means "all categories".
  const createMultiSelect = (categories, initial) => {
    const selected = new Set(initial);
    const byId = new Map(categories.map((c) => [c.Id, c]));

    const root = el('div', { className: 'xt-multiselect' });
    const toggle = el('button', { type: 'button', className: 'xt-ms-toggle' });
    const chips = el('div', { className: 'xt-ms-chips' });
    const panel = el('div', { className: 'xt-ms-panel', hidden: true });
    const search = el('input', { type: 'search', className: 'xt-ms-search', placeholder: 'Search categories…' });
    const options = el('div', { className: 'xt-ms-options' });
    const clear = el('button', { type: 'button', className: 'xt-ms-clear' }, ['Clear selection (all categories)']);

    const render = () => {
      toggle.replaceChildren(
        el('span', {}, [selected.size === 0 ? 'All categories' : `${selected.size} selected`]),
        icon(panel.hidden ? 'expand_more' : 'expand_less'),
      );
      chips.replaceChildren(...[...selected].map((id) => {
        const remove = el('button', { type: 'button', title: 'Remove' }, [icon('close')]);
        remove.onclick = () => {
          selected.delete(id);
          const box = options.querySelector(`input[data-id="${id}"]`);
          if (box) box.checked = false;
          render();
        };
        return el('span', { className: 'xt-ms-chip' }, [byId.get(id)?.Name ?? `#${id}`, remove]);
      }));
    };

    for (const category of categories) {
      const box = el('input', { type: 'checkbox', checked: selected.has(category.Id) });
      box.dataset.id = category.Id;
      box.onchange = () => {
        box.checked ? selected.add(category.Id) : selected.delete(category.Id);
        render();
      };
      const label = el('label', {}, [box, el('span', {}, [category.Name])]);
      label.dataset.name = (category.Name ?? '').toLowerCase();
      options.appendChild(label);
    }

    const noResults = el('div', { className: 'xt-ms-empty', hidden: true }, ['No categories found.']);
    search.oninput = () => {
      const q = search.value.trim().toLowerCase();
      let shown = 0;
      for (const label of options.children) {
        const match = !q || label.dataset.name.includes(q);
        label.hidden = !match;
        shown += match ? 1 : 0;
      }
      noResults.hidden = shown > 0;
    };
    clear.onclick = () => {
      selected.clear();
      options.querySelectorAll('input').forEach((b) => b.checked = false);
      render();
    };

    const open = (value) => {
      panel.hidden = !value;
      if (value) {
        panel.style.backgroundColor = themeBackground(root);
        search.focus();
      }
      render();
    };
    toggle.onclick = () => open(panel.hidden);
    root.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && !panel.hidden) {
        e.stopPropagation();
        open(false);
        toggle.focus();
      }
    });
    // Close when clicking outside of the dropdown.
    const outside = (e) => {
      if (!panel.hidden && !root.contains(e.target)) open(false);
    };
    view.addEventListener('click', outside);

    panel.append(search, options, noResults, clear);
    root.append(toggle, panel, chips);
    render();

    return {
      element: root,
      getSelected: () => [...selected],
      destroy: () => view.removeEventListener('click', outside),
    };
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(3);

    const $ = (sel) => view.querySelector(sel);
    const tbody = $('#DynamicRules');
    const editor = $('#RuleEditor');
    const unsaved = $('#UnsavedNote');
    tbody.replaceChildren();
    editor.hidden = true;
    unsaved.hidden = true;
    Dashboard.showLoadingMsg();

    const fetchMatches = (rules) => ApiClient.fetch({
      type: 'POST',
      url: ApiClient.getUrl('Xtream/LiveTv/DynamicMatches'),
      data: JSON.stringify(rules.map((r) => ({ Keyword: r.Keyword, Categories: r.Categories, Enabled: true }))),
      contentType: 'application/json',
      dataType: 'json',
    });

    Promise.all([
      ApiClient.getPluginConfiguration(pluginId),
      Xtream.fetchJson('Xtream/LiveCategories'),
    ]).then(([config, categories]) => {
      const categoryNames = new Map(categories.map((c) => [c.Id, c.Name]));
      const rules = (config.LiveTvDynamic ?? []).map((r) => ({
        Keyword: r.Keyword ?? '',
        Enabled: r.Enabled !== false,
        Favorite: r.Favorite !== false,
        Categories: r.Categories ?? [],
      }));
      let matches = []; // matches[i] = channels for rules[i], or undefined while loading
      let editing = null; // index being edited, or -1 for a new rule
      let picker = null;

      const markDirty = () => unsaved.hidden = false;

      const categorySummary = (ids) => {
        if (ids.length === 0) return 'All categories';
        const names = ids.map((id) => categoryNames.get(id) ?? `#${id}`);
        return names.length <= 2 ? names.join(', ') : `${names.slice(0, 2).join(', ')} +${names.length - 2} more`;
      };

      const renderTable = () => {
        tbody.replaceChildren();
        if (rules.length === 0) {
          const td = el('td', { colSpan: 6, className: 'dynamic-empty' }, ['No dynamic rules yet. Click + to add one.']);
          tbody.appendChild(el('tr', {}, [td]));
          return;
        }

        rules.forEach((rule, i) => {
          const enabled = el('input', { type: 'checkbox', checked: rule.Enabled, title: 'Enabled' });
          enabled.onchange = () => { rule.Enabled = enabled.checked; markDirty(); };
          const favorite = el('input', { type: 'checkbox', checked: rule.Favorite, title: 'Favorite matched channels' });
          favorite.onchange = () => { rule.Favorite = favorite.checked; markDirty(); };

          const found = matches[i];
          const matchCell = el('td');
          const details = el('tr', { className: 'dynamic-details', hidden: true });
          if (found === undefined) {
            matchCell.textContent = '…';
          } else if (found === null) {
            matchCell.textContent = '?';
            matchCell.title = 'Failed to load matches';
          } else {
            const btn = el('button', { type: 'button', className: 'dynamic-count' }, [`${found.length}`, icon('expand_more')]);
            btn.disabled = found.length === 0;
            btn.onclick = () => {
              details.hidden = !details.hidden;
              btn.lastChild.className = `material-icons ${details.hidden ? 'expand_more' : 'expand_less'}`;
            };
            matchCell.appendChild(btn);
            details.appendChild(el('td', { colSpan: 6 }, [
              el('ul', {}, found.map((c) => el('li', {}, [c.Name]))),
            ]));
          }

          const edit = el('button', { type: 'button', className: 'paper-icon-button-light', title: 'Edit' }, [icon('edit')]);
          edit.onclick = () => openEditor(i);
          const remove = el('button', { type: 'button', className: 'paper-icon-button-light', title: 'Delete' }, [icon('delete')]);
          remove.onclick = () => {
            rules.splice(i, 1);
            matches.splice(i, 1);
            if (editing === i) closeEditor();
            else if (editing > i) editing--;
            markDirty();
            renderTable();
          };

          const row = el('tr', { className: rule.Enabled ? '' : 'dynamic-disabled' }, [
            el('td', {}, [enabled]),
            el('td', { className: 'dynamic-keyword' }, [rule.Keyword]),
            el('td', { className: 'dynamic-categories', title: rule.Categories.map((id) => categoryNames.get(id) ?? `#${id}`).join('\n') }, [categorySummary(rule.Categories)]),
            el('td', {}, [favorite]),
            matchCell,
            el('td', { className: 'dynamic-actions' }, [edit, remove]),
          ]);
          tbody.append(row, details);
        });
      };

      const refreshMatches = () => {
        matches = rules.map(() => undefined);
        renderTable();
        if (rules.length === 0) return;
        fetchMatches(rules).then((result) => {
          matches = result;
        }).catch((error) => {
          console.error('Failed to load dynamic matches:', error);
          matches = rules.map(() => null);
        }).finally(renderTable);
      };

      const closeEditor = () => {
        editing = null;
        editor.hidden = true;
        picker?.destroy();
        picker = null;
      };

      const openEditor = (index) => {
        closeEditor();
        editing = index;
        const rule = index >= 0 ? rules[index] : { Keyword: '', Enabled: true, Favorite: true, Categories: [] };
        $('#RuleEditorTitle').textContent = index >= 0 ? 'Edit rule' : 'New rule';
        $('#RuleKeyword').value = rule.Keyword;
        $('#RuleEnabled').checked = rule.Enabled;
        $('#RuleFavorite').checked = rule.Favorite;
        $('#RulePreviewList').replaceChildren();
        picker = createMultiSelect(categories, rule.Categories);
        $('#RuleCategories').replaceChildren(picker.element);
        editor.hidden = false;
        editor.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        $('#RuleKeyword').focus();
      };

      const draft = () => ({
        Keyword: $('#RuleKeyword').value.trim(),
        Enabled: $('#RuleEnabled').checked,
        Favorite: $('#RuleFavorite').checked,
        Categories: picker.getSelected(),
      });

      $('#AddRule').onclick = () => openEditor(-1);
      $('#RuleCancel').onclick = closeEditor;
      $('#RuleApply').onclick = () => {
        const rule = draft();
        if (!rule.Keyword) {
          Dashboard.alert('Enter a keyword.');
          return;
        }
        if (editing >= 0) rules[editing] = rule;
        else rules.push(rule);
        closeEditor();
        markDirty();
        refreshMatches();
      };
      $('#RulePreview').onclick = () => {
        const list = $('#RulePreviewList');
        const rule = draft();
        list.replaceChildren();
        if (!rule.Keyword) {
          list.appendChild(el('li', {}, ['Enter a keyword first.']));
          return;
        }
        Dashboard.showLoadingMsg();
        fetchMatches([rule]).then(([channels]) => {
          if (channels.length === 0) list.appendChild(el('li', {}, ['No channels currently match.']));
          channels.forEach((c) => list.appendChild(el('li', {}, [c.Name])));
        }).catch((error) => {
          console.error('Failed to preview dynamic rule:', error);
          list.appendChild(el('li', {}, ['Failed to load preview.']));
        }).finally(() => Dashboard.hideLoadingMsg());
      };

      $('#XtreamLiveDynamicForm').onsubmit = (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((latest) => {
          latest.LiveTvDynamic = rules.filter((r) => r.Keyword.trim().length > 0);
          return ApiClient.updatePluginConfiguration(pluginId, latest);
        }).then((result) => {
          unsaved.hidden = true;
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
        return false;
      };

      Dashboard.hideLoadingMsg();
      refreshMatches();
    }).catch((error) => {
      console.error('Failed to load dynamic channel configuration:', error);
      Dashboard.hideLoadingMsg();
      const td = el('td', { colSpan: 6 }, [
        'Failed to load categories. Check that your Xtream credentials are set (Credentials tab) and the server is reachable.',
      ]);
      td.style.color = '#ff6b6b';
      tbody.replaceChildren(el('tr', {}, [td]));
    });
  }));
}
