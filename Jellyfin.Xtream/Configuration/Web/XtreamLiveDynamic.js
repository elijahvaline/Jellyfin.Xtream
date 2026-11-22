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

  // Search box with live suggestions. Picked categories become removable chips.
  // An empty selection means "all categories".
  const MAX_SUGGESTIONS = 8;
  const createCategorySearch = (categories, initial, onChange = () => {}) => {
    const selected = new Set(initial);
    const byId = new Map(categories.map((c) => [c.Id, c]));
    const index = categories.map((c) => ({ category: c, name: (c.Name ?? '').toLowerCase() }));

    const root = el('div', { className: 'xt-typeahead' });
    const input = el('input', { type: 'search', className: 'xt-ta-input', placeholder: 'Type to search categories…', autocomplete: 'off' });
    const results = el('ul', { className: 'xt-ta-results', hidden: true });
    const summary = el('div', { className: 'fieldDescription xt-ta-summary' });
    const chips = el('div', { className: 'xt-ms-chips' });
    let suggestions = [];
    let active = 0;

    const renderChips = () => {
      summary.textContent = selected.size === 0
        ? 'No categories selected: searching all categories.'
        : `Searching ${selected.size} selected ${selected.size === 1 ? 'category' : 'categories'}.`;
      onChange();
      chips.replaceChildren(...[...selected].map((id) => {
        const remove = el('button', { type: 'button', title: 'Remove' }, [icon('close')]);
        remove.onclick = () => { selected.delete(id); renderChips(); search(); };
        return el('span', { className: 'xt-ms-chip' }, [byId.get(id)?.Name ?? `#${id}`, remove]);
      }));
    };

    // Highlights the matched part of a name.
    const highlight = (name, q) => {
      const at = name.toLowerCase().indexOf(q);
      if (at < 0) return [name];
      return [name.slice(0, at), el('mark', {}, [name.slice(at, at + q.length)]), name.slice(at + q.length)];
    };

    const renderResults = (q) => {
      results.replaceChildren();
      if (!q) { results.hidden = true; return; }
      if (suggestions.length === 0) {
        results.appendChild(el('li', { className: 'xt-ta-empty' }, ['No categories found.']));
      }
      suggestions.forEach((c, n) => {
        const li = el('li', { className: n === active ? 'active' : '' }, highlight(c.Name ?? '', q));
        li.onmousedown = (e) => { e.preventDefault(); pick(c); };
        results.appendChild(li);
      });
      results.hidden = false;
    };

    const search = () => {
      const q = input.value.trim().toLowerCase();
      active = 0;
      if (!q) { suggestions = []; renderResults(q); return; }
      const starts = [];
      const contains = [];
      for (const entry of index) {
        if (selected.has(entry.category.Id)) continue;
        const at = entry.name.indexOf(q);
        if (at === 0) starts.push(entry.category);
        else if (at > 0) contains.push(entry.category);
        if (starts.length >= MAX_SUGGESTIONS) break;
      }
      suggestions = starts.concat(contains).slice(0, MAX_SUGGESTIONS);
      renderResults(q);
    };

    const pick = (category) => {
      selected.add(category.Id);
      input.value = '';
      renderChips();
      search();
      input.focus();
    };

    input.oninput = search;
    input.onkeydown = (e) => {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (suggestions.length === 0) return;
        e.preventDefault();
        active = (active + (e.key === 'ArrowDown' ? 1 : suggestions.length - 1)) % suggestions.length;
        renderResults(input.value.trim().toLowerCase());
      } else if (e.key === 'Enter') {
        e.preventDefault(); // never submit the page form from here
        if (suggestions[active]) pick(suggestions[active]);
      } else if (e.key === 'Escape' && input.value) {
        e.stopPropagation();
        input.value = '';
        search();
      }
    };
    input.onblur = () => { results.hidden = true; };
    input.onfocus = () => { if (input.value.trim()) search(); };

    root.append(input, results, summary, chips);
    renderChips();

    return {
      element: root,
      getSelected: () => [...selected],
      has: (id) => selected.has(id),
      add: (id) => { if (byId.has(id)) { selected.add(id); renderChips(); } },
      toggle: (id) => { if (selected.has(id)) selected.delete(id); else if (byId.has(id)) selected.add(id); renderChips(); },
      destroy: () => {},
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
      data: JSON.stringify(rules.map((r) => ({ Keyword: r.Keyword, Categories: r.Categories, Enabled: true, League: r.League, TeamId: r.TeamId }))),
      contentType: 'application/json',
      dataType: 'json',
    });

    Promise.all([
      ApiClient.getPluginConfiguration(pluginId),
      Xtream.fetchJson('Xtream/LiveCategories'),
      Xtream.fetchJson('Xtream/Sports/Leagues').catch(() => []),
    ]).then(([config, categories, leagues]) => {
      const categoryNames = new Map(categories.map((c) => [c.Id, c.Name]));
      const rules = (config.LiveTvDynamic ?? []).map((r) => ({
        Keyword: r.Keyword ?? '',
        Enabled: r.Enabled !== false,
        Favorite: r.Favorite !== false,
        Categories: r.Categories ?? [],
        ShowAsFolder: r.ShowAsFolder === true,
        FolderName: r.FolderName ?? '',
        League: r.League ?? '',
        TeamId: r.TeamId ?? '',
        TeamName: r.TeamName ?? '',
      }));
      const teamCache = new Map(); // league id -> Promise of teams

      // Time zone: default to the browser's own zone when not configured yet.
      const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
      const zoneSelect = $('#SportsTimeZone');
      const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [browserZone];
      const currentZone = config.SportsTimeZone || browserZone;
      zoneSelect.replaceChildren(...[...new Set([currentZone, ...zones])].sort().map((z) => el('option', { value: z, textContent: z })));
      zoneSelect.value = currentZone;
      zoneSelect.onchange = () => markDirty();
      if (!config.SportsTimeZone && rules.some((r) => r.TeamId)) {
        // Linked teams without a saved zone would use the server's (often UTC): nudge to save.
        setTimeout(() => markDirty(), 0);
      }

      const teamsFirst = $('#TeamLibrariesFirst');
      teamsFirst.checked = config.TeamLibrariesFirst !== false;
      teamsFirst.onchange = () => markDirty();

      // Smart guide refresh.
      const smartEnabled = $('#SmartRefreshEnabled');
      const smartBefore = $('#SmartRefreshBefore');
      const smartAfter = $('#SmartRefreshAfter');
      smartEnabled.checked = config.SmartRefreshEnabled !== false;
      smartBefore.value = config.SmartRefreshBeforeMinutes ?? 60;
      smartAfter.value = config.SmartRefreshAfterMinutes ?? 240;
      const updateSmart = () => { $('#SmartRefreshOffsets').hidden = !smartEnabled.checked; };
      smartEnabled.onchange = () => { updateSmart(); markDirty(); };
      smartBefore.oninput = smartAfter.oninput = () => markDirty();
      updateSmart();
      const showNextRun = () => ApiClient.fetch({ type: 'GET', url: ApiClient.getUrl('Xtream/Sports/SmartRefresh') })
        .then((response) => (response.status === 204 ? null : response.json()))
        .then((next) => {
          $('#SmartRefreshNext').textContent = next
            ? `Next smart refresh: ${new Date(next.TimeUtc.endsWith('Z') ? next.TimeUtc : `${next.TimeUtc}Z`).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })} (${next.Reason}).`
            : (smartEnabled.checked ? 'No smart refresh planned: link a rule to a team with upcoming games.' : '');
        })
        .catch(() => { $('#SmartRefreshNext').textContent = ''; });
      showNextRun();

      const loadTeams = (league) => {
        if (!teamCache.has(league)) {
          const promise = Xtream.fetchJson(`Xtream/Sports/Teams?league=${encodeURIComponent(league)}`);
          promise.catch(() => teamCache.delete(league));
          teamCache.set(league, promise);
        }
        return teamCache.get(league);
      };
      // Channels (by stream id) that dynamic rules must never add.
      const hidden = new Set(config.LiveTvDynamicHidden ?? []);
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
          const td = el('td', { colSpan: 7, className: 'dynamic-empty' }, ['No dynamic rules yet. Click + to add one.']);
          tbody.appendChild(el('tr', {}, [td]));
          return;
        }

        rules.forEach((rule, i) => {
          const enabled = el('input', { type: 'checkbox', checked: rule.Enabled, title: 'Enabled' });
          enabled.onchange = () => { rule.Enabled = enabled.checked; markDirty(); };
          const favorite = el('input', { type: 'checkbox', checked: rule.Favorite, title: 'Favorite matched channels' });
          favorite.onchange = () => { rule.Favorite = favorite.checked; markDirty(); };

          const all = matches[i];
          const found = Array.isArray(all) ? all.filter((c) => !hidden.has(c.Id)) : all;
          const hiddenCount = Array.isArray(all) ? all.length - found.length : 0;
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
            if (hiddenCount > 0) btn.title = `${hiddenCount} hidden`;
            matchCell.appendChild(btn);
            details.appendChild(el('td', { colSpan: 7 }, [
              el('ul', {}, found.map((c) => el('li', {}, [c.Name + (c.Game ? `  (${c.Game})` : '')]))),
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
            el('td', { className: 'dynamic-teamcell', title: rule.ShowAsFolder ? 'Shown as a team library on the home screen' : '' }, [
              ...(rule.ShowAsFolder ? [icon('folder')] : []),
              rule.TeamName || (rule.ShowAsFolder ? 'Folder' : '—'),
            ]),
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
        const rule = index >= 0 ? rules[index] : { Keyword: '', Enabled: true, Favorite: true, Categories: [], ShowAsFolder: false, FolderName: '', League: '', TeamId: '', TeamName: '' };
        $('#RuleEditorTitle').textContent = index >= 0 ? 'Edit rule' : 'New rule';
        $('#RuleKeyword').value = rule.Keyword;
        $('#RuleEnabled').checked = rule.Enabled;
        $('#RuleFavorite').checked = rule.Favorite;
        $('#RuleShowAsFolder').checked = rule.ShowAsFolder;
        $('#RuleFolderName').value = rule.FolderName;
        $('#RuleLeague').replaceChildren(
          el('option', { value: '', textContent: 'None' }),
          ...leagues.map((l) => el('option', { value: l.Id, textContent: l.Name })));
        $('#RuleLeague').value = rule.League;
        renderTeams(rule.League, rule.TeamId, rule.TeamName);
        $('#RulePreviewList').replaceChildren();
        // Keep the category tags in an open preview in sync with the selection, without re-running it.
        picker = createCategorySearch(categories, rule.Categories, () => {
          for (const tag of view.querySelectorAll('#RulePreviewList button.dynamic-preview-category')) {
            tag.classList.toggle('is-selected', picker?.has(Number(tag.dataset.categoryId)) === true);
          }
        });
        $('#RuleCategories').replaceChildren(picker.element);
        editor.hidden = false;
        editor.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        $('#RuleKeyword').focus();
        if (index >= 0) runPreview();
      };

      const draft = () => ({
        Keyword: $('#RuleKeyword').value.trim(),
        Enabled: $('#RuleEnabled').checked,
        Favorite: $('#RuleFavorite').checked,
        Categories: picker.getSelected(),
        ShowAsFolder: $('#RuleShowAsFolder').checked,
        FolderName: $('#RuleFolderName').value.trim(),
        League: $('#RuleTeam').value ? $('#RuleLeague').value : '',
        TeamId: $('#RuleTeam').value,
        TeamName: $('#RuleTeam').value ? ($('#RuleTeam').selectedOptions[0]?.textContent ?? '') : '',
      });

      // Fills the team list for a league and shows the selected team's logo and next games.
      let teamRequest = 0;
      const renderTeams = (league, teamId, teamName) => {
        const select = $('#RuleTeam');
        const request = ++teamRequest;
        select.replaceChildren(el('option', { value: '', textContent: league ? 'Loading…' : 'Pick a league first' }));
        select.disabled = true;
        showTeam(null);
        if (!league) return;
        loadTeams(league).then((teams) => {
          if (request !== teamRequest) return;
          select.replaceChildren(
            el('option', { value: '', textContent: 'None' }),
            ...teams.map((t) => el('option', { value: t.Id, textContent: t.Name })));
          if (teamId && !teams.some((t) => t.Id === teamId)) {
            select.appendChild(el('option', { value: teamId, textContent: teamName || `#${teamId}` }));
          }
          select.value = teamId ?? '';
          select.disabled = false;
          showTeam(teams.find((t) => t.Id === select.value) ?? null, league);
        }).catch(() => {
          if (request !== teamRequest) return;
          select.replaceChildren(el('option', { value: teamId ?? '', textContent: teamName || 'Failed to load teams from ESPN' }));
        });
      };
      const showTeam = (team, league) => {
        const logo = $('#RuleTeamLogo');
        const schedule = $('#RuleTeamSchedule');
        logo.src = team?.LogoUrl ?? '';
        schedule.replaceChildren();
        if (!team) return;
        Xtream.fetchJson(`Xtream/Sports/Schedule?league=${encodeURIComponent(league)}&teamId=${encodeURIComponent(team.Id)}`)
          .then((games) => {
            if ($('#RuleTeam').value !== team.Id) return;
            schedule.replaceChildren(...(games.length
              ? [el('li', { className: 'dynamic-preview-header', textContent: 'Next games:' }), ...games.map((g) => el('li', { textContent: g }))]
              : [el('li', { textContent: 'No upcoming games.' })]));
          })
          .catch(() => schedule.replaceChildren(el('li', { textContent: 'Could not load the schedule from ESPN.' })));
      };
      $('#RuleLeague').onchange = () => renderTeams($('#RuleLeague').value, '', '');
      $('#RuleTeam').onchange = () => {
        const league = $('#RuleLeague').value;
        loadTeams(league).then((teams) => showTeam(teams.find((t) => t.Id === $('#RuleTeam').value) ?? null, league));
      };

      $('#AddRule').onclick = () => openEditor(-1);
      $('#RuleCancel').onclick = closeEditor;
      $('#RuleApply').onclick = () => {
        const rule = draft();
        if (!rule.Keyword) {
          Dashboard.alert('Enter at least one keyword.');
          return;
        }
        if (editing >= 0) rules[editing] = rule;
        else rules.push(rule);
        closeEditor();
        markDirty();
        refreshMatches();
      };
      const runPreview = () => {
        const list = $('#RulePreviewList');
        const rule = draft();
        list.replaceChildren();
        if (!rule.Keyword) {
          list.appendChild(el('li', {}, ['Enter a keyword first.']));
          return;
        }
        Dashboard.showLoadingMsg();
        fetchMatches([rule]).then(([channels]) => {
          if (channels.length === 0) {
            list.appendChild(el('li', {}, ['No channels currently match.']));
            return;
          }
          const header = el('li', { className: 'dynamic-preview-header' });
          const updateHeader = () => {
            const n = channels.filter((c) => hidden.has(c.Id)).length;
            header.textContent = `${channels.length - n} of ${channels.length} channels will be added` + (n ? ` (${n} hidden)` : '') + '. Tick "Hide" to always leave a channel out.'
              + ' Click categories to limit the rule to them (click again to remove), then preview again to see the result.';
          };
          list.appendChild(header);
          for (const c of channels) {
            const box = el('input', { type: 'checkbox', checked: hidden.has(c.Id) });
            const li = el('li', { className: hidden.has(c.Id) ? 'is-hidden' : '' }, [
              el('label', { className: 'dynamic-hide' }, [box, el('span', {}, ['Hide'])]),
              el('span', { className: 'dynamic-preview-name' }, [c.Name]),
            ]);
            if (rule.TeamId) {
              li.appendChild(el('span', {
                className: 'dynamic-preview-game',
                title: c.ListedToday === false ? 'Listed in the team folder on game day' : '',
              }, [c.Game ?? 'No game matched: always listed']));
              if (c.ListedToday === false) li.classList.add('not-today');
            }
            if (c.CategoryId != null) {
              const name = categoryNames.get(c.CategoryId) ?? `#${c.CategoryId}`;
              const tag = el('button', { type: 'button', className: 'dynamic-preview-category', title: 'Limit this rule to this category (click again to remove)' }, [name]);
              tag.dataset.categoryId = String(c.CategoryId);
              tag.classList.toggle('is-selected', picker.has(c.CategoryId));
              tag.onclick = () => picker.toggle(c.CategoryId);
              li.appendChild(tag);
            }
            box.onchange = () => {
              box.checked ? hidden.add(c.Id) : hidden.delete(c.Id);
              li.classList.toggle('is-hidden', box.checked);
              updateHeader();
              markDirty();
              renderTable();
            };
            list.appendChild(li);
          }
          updateHeader();
        }).catch((error) => {
          console.error('Failed to preview dynamic rule:', error);
          list.appendChild(el('li', {}, ['Failed to load preview.']));
        }).finally(() => Dashboard.hideLoadingMsg());
      };
      $('#RulePreview').onclick = runPreview;

      $('#XtreamLiveDynamicForm').onsubmit = (e) => {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((latest) => {
          latest.LiveTvDynamic = rules.filter((r) => r.Keyword.trim().length > 0);
          latest.LiveTvDynamicHidden = [...hidden];
          latest.SportsTimeZone = zoneSelect.value;
          latest.SmartRefreshEnabled = smartEnabled.checked;
          latest.TeamLibrariesFirst = teamsFirst.checked;
          latest.SmartRefreshBeforeMinutes = Math.max(0, parseInt(smartBefore.value, 10) || 0);
          latest.SmartRefreshAfterMinutes = Math.max(0, parseInt(smartAfter.value, 10) || 0);
          return ApiClient.updatePluginConfiguration(pluginId, latest);
        }).then((result) => {
          unsaved.hidden = true;
          Dashboard.processPluginConfigurationUpdateResult(result);
          // Saving refreshes the guide, after which the next smart refresh is planned again.
          setTimeout(showNextRun, 60000);
        });
        return false;
      };

      Dashboard.hideLoadingMsg();
      refreshMatches();
    }).catch((error) => {
      console.error('Failed to load dynamic channel configuration:', error);
      Dashboard.hideLoadingMsg();
      const td = el('td', { colSpan: 7 }, [
        'Failed to load categories. Check that your Xtream credentials are set (Credentials tab) and the server is reachable.',
      ]);
      td.style.color = '#ff6b6b';
      tbody.replaceChildren(el('tr', {}, [td]));
    });
  }));
}
