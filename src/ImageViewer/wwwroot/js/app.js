/* 图片查看器前端逻辑：相册/列表视图 + 全屏查看器（缩放/平移/旋转/切换/导出）。 */

(() => {
  'use strict';

  // ==================== 状态 ====================
  const state = {
    path: '',                 // 当前浏览目录
    root: '',                 // 当前相册根（根处 is_root，不可再回退）
    parent: null,
    isRoot: false,
    photos: [],               // 当前目录图片：[{name, path, size, modified, url, thumb_url}]
    albums: [],               // 相册：[{name, path, count, cover_thumb_url}]
    linkedAlbums: [],         // 已链接相册（外部文件夹直接链接）：[{name, path, count, cover_thumb_url}]
    view: 'album',            // album | list
    sortBy: 'name',           // 图片排序字段：name | modified | created | size
    sortOrder: 'asc',         // asc | desc
    inAlbum: false,           // true=正在看某个相册的图片页；false=相册页（只显示相册）
    tagPage: false,           // true=标签管理页
    filterTags: [],           // 标签页中选中的筛选标签（多选取交集）
    droppedUrl: null,         // 拖入图片的 blob URL
    droppedName: '',          // 拖入图片的文件名
    pendingName: '',          // 双击打开图片的文件名（相册加载完成前临时显示）
    // ---- 查看器 ----
    index: -1,
    scale: 1,
    translateX: 0,
    translateY: 0,
    rotate: 0,                // CSS 角度，每次 ±90
    isFitMode: true,
    dragging: false,
    dragLastX: 0,
    dragLastY: 0,
    bg: 0,                    // 查看器背景：0=默认灰 1=白 2=黑
  };

  // ==================== DOM 引用 ====================
  const $ = (id) => document.getElementById(id);
  const content = $('content');
  const viewer = $('viewer');
  const viewerImg = $('viewerImg');
  const viewerStage = $('viewerStage');
  const viewerTitle = $('viewerTitle');
  const zoomLabel = $('zoomLabel');

  const MIN_SCALE = 0.05;
  const MAX_SCALE = 20;
  const ZOOM_STEP = 1.25;

  // 国际化：简体中文（zh-CN）直接透传原文，其他语言查 I18N 字典
  const t = (key) => (window.I18N ? I18N.t(key) : key);

  /** 组合后端错误显示：error 为固定短语（t() 翻译）+ message 为动态详情（原文）。 */
  const apiErr = (d, fallback) => {
    if (!d) return fallback;
    const base = d.error ? t(d.error) : (fallback || '');
    if (!d.message) return base;
    return base ? base + '：' + d.message : d.message;
  };

  // ==================== 目录加载与渲染 ====================

  /** 加载并渲染指定目录。path 为空 = 当前相册根；请求携带 root 参数供后端判定「根处不可回退」。
   *  inAlbum：true=相册图片页（显示该相册图片）；false/缺省=相册页（只显示相册）。 */
  async function load(path, inAlbum) {
    if (typeof inAlbum !== 'undefined') state.inAlbum = !!inAlbum;
    state.tagPage = false;   // 导航离开标签页
    showLoading();
    const params = new URLSearchParams();
    if (path) params.set('path', path);
    if (state.root) params.set('root', state.root);
    const qs = params.toString();
    const resp = await fetch('/api/photos' + (qs ? '?' + qs : ''));
    if (!resp.ok) {
      hideLoading();
      const err = await resp.json().catch(() => ({ error: 'HTTP ' + resp.status }));
      showModal({ title: t('提示'), message: esc(t(err.error || '加载失败')), type: 'error' });
      return;
    }
    const data = await resp.json();
    // 首次加载：确定相册根（后端默认目录）
    if (!state.root) state.root = data.path;
    state.path = data.path;
    state.parent = data.parent;
    state.isRoot = data.is_root;
    state.photos = data.photos;
    state.albums = data.albums;
    state.displayName = data.display_name || state.path;
    if (state.inAlbum) await loadSort(state.path);   // 进入相册：恢复该相册记忆的排序方式
    render();
    hideLoading();
  }

  /** 显示/隐藏加载动画（目录/相册加载时）。 */
  function showLoading() { $('loading').classList.remove('hidden'); }
  function hideLoading() { $('loading').classList.add('hidden'); }

  /** 拉取已链接相册列表（GET /api/albums）。 */
  async function loadAlbums() {
    try {
      const resp = await fetch('/api/albums');
      if (!resp.ok) { state.linkedAlbums = []; return; }
      const data = await resp.json();
      state.linkedAlbums = data.albums || [];
    } catch { state.linkedAlbums = []; }
  }

  /** 浏览并导入相册文件夹（直接链接，不复制文件）。桌面版弹系统文件夹选择框，浏览器版用模态输入路径。 */
  async function addFolder() {
    const ch = getChrome();
    let picked = null;
    if (ch && ch.pick_folder) {
      try { picked = await ch.pick_folder(t('选择相册文件夹（直接链接，不复制文件）')); } catch { }
    }
    if (!picked) {
      picked = await showModal({
        title: t('添加相册文件夹'),
        message: t('输入相册文件夹路径（将直接链接，不复制文件）：'),
        type: 'prompt',
      });
    }
    if (!picked) return;
    // 提交链接到后端（校验目录存在并持久化）
    let ok = false;
    try {
      const resp = await fetch('/api/albums', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: picked }),
      });
      if (!resp.ok) {
        const err = await resp.json().catch(() => ({ error: t('添加失败') }));
        showModal({ title: t('提示'), message: esc(t(err.error || '添加失败')), type: 'error' });
        return;
      }
      ok = true;
    } catch {
      showModal({ title: t('提示'), message: t('添加失败：无法连接服务'), type: 'error' });
      return;
    }
    if (ok) await loadAlbums();
    // 添加后直接进入该相册的图片页
    state.root = picked;
    load(picked, true);
  }

  /** 移除相册链接（只移除链接，不删除文件夹本身）。 */
  async function removeAlbum(path) {
    const ok = await showModal({
      title: '移除相册链接',
      message: '移除该相册文件夹链接？<br>（不会删除文件夹本身）',
      type: 'confirm',
    });
    if (!ok) return;
    try { await fetch('/api/albums?path=' + encodeURIComponent(path), { method: 'DELETE' }); } catch { }
    await loadAlbums();
    render();
  }

  /** 回首页：相册页（只显示相册）。跳到第一个已链接相册；没有则回默认目录。 */
  async function goHome() {
    if (state.linkedAlbums.length > 0) {
      state.root = state.linkedAlbums[0].path;
      load(state.root, false);
    } else {
      load('', false);
    }
  }

  function render() {
    const st = $('winSubtitle');
    if (st) {
      // 标题栏文件夹名：相册页显示「相册」，标签页显示「标签管理」，图片页显示当前文件夹名
      if (state.tagPage) st.textContent = t('标签管理');
      else st.textContent = state.inAlbum ? (state.displayName || state.path) : t('相册');
    }
    // 上级按钮只在相册（图片页）内显示；标签页需要返回也显示；相册页（根）隐藏
    $('btnBack').style.display = (state.inAlbum || state.tagPage) ? '' : 'none';
    content.innerHTML = '';
    disposeVg();
    cancelListChunk();
    // 常驻路径条：无论相册根界面还是相册/子相册图片页，顶部始终显示当前目录完整路径
    if (!state.tagPage) renderPathBar();
    if (state.tagPage) {
      // 标签管理页
      $('viewToggle').style.display = 'none';
      renderTagPage();
      return;
    }
    if (state.inAlbum) {
      // 图片页：平铺/列表切换生效 + 排序
      $('viewToggle').style.display = '';
      sortPhotos();
      if (state.view === 'album') renderAlbum(); else renderList();
    } else {
      // 相册页：始终以相册（平铺卡片）显示，视图切换隐藏
      $('viewToggle').style.display = 'none';
      renderMyAlbums();
      // 根目录已是「我的相册」里的已链接相册时：其子相册可在相册图片页的「子相册」区浏览，
      // 这里不再重复列出（修复从子相册点「上级」回到相册根界面时，下方又出现子相册列表）；
      // 非链接根（如默认目录）仍显示子相册网格，便于浏览
      const rootIsLinked = state.linkedAlbums.some(
        (l) => l.path.toLowerCase() === state.path.toLowerCase());
      const subAlbums = rootIsLinked ? [] : state.albums.filter((a) =>
        !state.linkedAlbums.some((l) => l.path.toLowerCase() === a.path.toLowerCase()));
      if (subAlbums.length > 0) {
        renderAlbumGrid(subAlbums, t('相册'));
      } else if (state.linkedAlbums.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty-hint';
        empty.textContent = t('还没有相册，点击「＋ 添加文件夹」导入你的图片文件夹');
        content.appendChild(empty);
      }
    }
  }

  /** 按当前排序方式（state.sortBy/sortOrder）对 state.photos 就地排序。 */
  function sortPhotos() {
    const by = state.sortBy;
    const dir = state.sortOrder === 'desc' ? -1 : 1;
    state.photos.sort((a, b) => {
      let r = 0;
      if (by === 'name') {
        r = String(a.name).localeCompare(String(b.name), 'zh', { numeric: true, sensitivity: 'base' });
      } else if (by === 'type') {
        // 按文件后缀排序（同类型再按名称）
        const ea = extOf(a.name), eb = extOf(b.name);
        r = ea.localeCompare(eb, 'zh', { sensitivity: 'base' })
          || String(a.name).localeCompare(String(b.name), 'zh', { numeric: true, sensitivity: 'base' });
      } else if (by === 'modified') {
        r = (new Date(a.modified)).getTime() - (new Date(b.modified)).getTime();
      } else if (by === 'created') {
        r = (new Date(a.created)).getTime() - (new Date(b.created)).getTime();
      } else if (by === 'size') {
        r = a.size - b.size;
      }
      return r * dir;
    });
  }

  /** 拉取当前相册的排序设置（每个相册单独保存；未设置用默认名称升序）。 */
  async function loadSort(path) {
    try {
      const resp = await fetch('/api/sort?path=' + encodeURIComponent(path));
      const d = await resp.json();
      if (d.by) { state.sortBy = d.by; state.sortOrder = d.order || 'asc'; }
    } catch { }
    syncViewToggle();
  }

  /** 保存当前相册的排序设置（仅图片页）。 */
  function saveSort() {
    if (!state.inAlbum || !state.path) return;
    fetch('/api/sort', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: state.path, by: state.sortBy, order: state.sortOrder }),
    }).catch(() => {});
  }

  // ==================== 标签管理页 ====================

  /** 打开标签管理页。 */
  function openTagPage() {
    state.tagPage = true;
    render();
  }

  /** 渲染标签管理页：标签管理 + 多选筛选（交集）+ 匹配图片。 */
  async function renderTagPage() {
    content.innerHTML = '';
    let tags = [];
    try {
      const resp = await fetch('/api/tags');
      const data = await resp.json();
      tags = data.tags || [];
    } catch { }

    const head = document.createElement('div');
    head.className = 'tag-page-head';
    const title = document.createElement('span');
    title.className = 'section-title';
    title.textContent = t('标签管理');
    const addBtn = document.createElement('button');
    addBtn.className = 'tool-btn';
    addBtn.textContent = '＋ ' + t('新建标签');
    addBtn.addEventListener('click', createTag);
    head.appendChild(title);
    head.appendChild(addBtn);
    content.appendChild(head);

    if (tags.length === 0) {
      const hint = document.createElement('div');
      hint.className = 'tag-filter-hint';
      hint.textContent = t('还没有标签。右键任意图片 → 添加标签，或点「＋ 新建标签」。');
      content.appendChild(hint);
    } else {
      const chips = document.createElement('div');
      chips.className = 'tag-chips';
      for (const t of tags) {
        const active = state.filterTags.includes(t);
        const chip = document.createElement('span');
        chip.className = 'tag-chip' + (active ? ' active' : '');
        chip.title = active ? t('点击取消筛选') : t('点击加入筛选');
        chip.textContent = t;
        const del = document.createElement('span');
        del.className = 'tag-chip-del';
        del.textContent = '✕';
        del.title = t('删除标签');
        del.addEventListener('click', (e) => { e.stopPropagation(); deleteTag(t); });
        chip.appendChild(del);
        chip.addEventListener('click', () => toggleFilterTag(t));
        chips.appendChild(chip);
      }
      content.appendChild(chips);
    }

    const resultTitle = document.createElement('div');
    resultTitle.className = 'section-title';
    resultTitle.textContent = state.filterTags.length
      ? t('筛选：') + state.filterTags.map(esc).join(' ∩ ')
      : t('筛选匹配的图片');
    content.appendChild(resultTitle);

    if (state.filterTags.length > 0) {
      await loadFilteredPhotos();
      if (state.photos.length > 0) {
        renderPhotoGrid(state.photos);
      } else {
        const hint = document.createElement('div');
        hint.className = 'tag-filter-hint';
        hint.textContent = t('没有同时包含所选标签的图片');
        content.appendChild(hint);
      }
    } else {
      const hint = document.createElement('div');
      hint.className = 'tag-filter-hint';
      hint.textContent = t('点击上方标签进行筛选（多选取交集）');
      content.appendChild(hint);
    }
  }

  /** 按选中的标签（交集）拉取匹配图片。 */
  async function loadFilteredPhotos() {
    try {
      const resp = await fetch('/api/tags/filter?tags=' + encodeURIComponent(state.filterTags.join(',')));
      const data = await resp.json();
      state.photos = data.photos || [];
    } catch { state.photos = []; }
  }

  /** 切换标签是否参与筛选。 */
  function toggleFilterTag(tag) {
    const i = state.filterTags.indexOf(tag);
    if (i >= 0) state.filterTags.splice(i, 1);
    else state.filterTags.push(tag);
    renderTagPage();
  }

  /** 删除标签（从所有图片移除）。 */
  async function deleteTag(tag) {
    const ok = await showModal({
      title: t('删除标签'),
      message: t('删除标签') + `「${esc(tag)}」？<br>` + t('（会从所有图片上移除）'),
      type: 'confirm',
    });
    if (!ok) return;
    try { await fetch('/api/tags?name=' + encodeURIComponent(tag), { method: 'DELETE' }); } catch { }
    state.filterTags = state.filterTags.filter(t => t !== tag);
    renderTagPage();
  }

  /** 新建标签。 */
  async function createTag() {
    const name = await showModal({ title: t('新建标签'), message: t('输入标签名称：'), type: 'prompt' });
    if (!name) return;
    try {
      await fetch('/api/tags', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
    } catch { }
    renderTagPage();
  }

  /** 我的相册区：已链接的外部文件夹（直接链接，不复制）。空时给出导入提示。 */
  function renderMyAlbums() {
    const section = document.createElement('div');

    const head = document.createElement('div');
    head.className = 'my-albums-head';
    const title = document.createElement('span');
    title.className = 'section-title';
    title.textContent = t('我的相册');
    const addBtn = document.createElement('button');
    addBtn.className = 'tool-btn';
    addBtn.textContent = '＋ ' + t('添加文件夹');
    addBtn.addEventListener('click', addFolder);
    head.appendChild(title);
    head.appendChild(addBtn);
    section.appendChild(head);

    if (state.linkedAlbums.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'my-albums-empty';
      empty.innerHTML = t('还没有添加相册文件夹') + '<br>' + t('点击「＋ 添加文件夹」浏览并导入你的图片文件夹（直接链接，不复制文件）');
      section.appendChild(empty);
    } else {
      const grid = document.createElement('div');
      grid.className = 'album-grid';
      for (const a of state.linkedAlbums) {
        const card = document.createElement('div');
        card.className = 'album-card';
        card.title = a.path;
        card.innerHTML =
          '<div class="album-cover">' +
          (a.cover_thumb_url ? `<img src="${a.cover_thumb_url}" loading="lazy" alt="">` : '') +
          '</div>' +
          `<div class="album-meta"><span class="name">${esc(a.name)}</span><span class="count">${a.count} ${t('张')}</span></div>` +
          '<span class="album-remove" title="' + t('移除链接') + '">✕</span>';
        card.addEventListener('click', () => {
          state.root = a.path;
          load(a.path, true);
        });
        card.addEventListener('contextmenu', (e) => {
          e.preventDefault();
          showAlbumMenu(e.clientX, e.clientY, a.path);
        });
        card.querySelector('.album-remove').addEventListener('click', (e) => {
          e.stopPropagation();
          removeAlbum(a.path);
        });
        grid.appendChild(card);
      }
      section.appendChild(grid);
    }
    content.appendChild(section);
  }

  /** 区块标题（路径已由顶部常驻路径条统一显示）。 */
  function makeSectionHead(titleText) {
    const title = document.createElement('div');
    title.className = 'section-title';
    title.textContent = titleText;
    return title;
  }

  /** 常驻路径条：内容区顶部显示当前目录完整路径（完整显示换行，悬停可看全）。 */
  function renderPathBar() {
    const bar = document.createElement('div');
    bar.className = 'page-path-bar';
    const icon = document.createElement('span');
    icon.className = 'page-path-icon';
    const text = document.createElement('span');
    text.className = 'page-path-text';
    if (state.inAlbum) {
      // 图片页：显示当前目录完整路径
      icon.textContent = '📂';
      text.textContent = state.path || t('未选择目录');
      text.title = state.path || '';
    } else {
      // 相册页（根）：显示「相册展示」而不是路径
      icon.textContent = '🏠';
      text.textContent = t('相册展示');
      text.title = '';
    }
    bar.appendChild(icon);
    bar.appendChild(text);
    content.appendChild(bar);
  }

  /** 平铺视图（仅在图片页使用）：子相册(可继续下钻) + 该相册图片。 */
  function renderAlbum() {
    if (state.albums.length > 0) {
      renderAlbumGrid(state.albums, t('子相册'));
    }
    if (state.photos.length > 0) {
      content.appendChild(makeSectionHead(t('图片') + '（' + state.photos.length + ' ' + t('张') + '）'));
      renderPhotoGrid(state.photos);
    }
    if (state.photos.length === 0 && state.albums.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'empty-hint';
      empty.textContent = t('此相册没有图片');
      content.appendChild(empty);
    }
  }

  /** 渲染相册卡片网格。点击卡片 → 进入该相册的图片页。 */
  function renderAlbumGrid(albums, sectionTitle) {
    content.appendChild(makeSectionHead(sectionTitle));
    const grid = document.createElement('div');
    grid.className = 'album-grid';
    for (const a of albums) {
      const card = document.createElement('div');
      card.className = 'album-card';
      card.title = a.path;
      card.innerHTML =
        '<div class="album-cover">' +
        (a.cover_thumb_url ? `<img src="${a.cover_thumb_url}" loading="lazy" alt="">` : '') +
        '</div>' +
        `<div class="album-meta"><span class="name">${esc(a.name)}</span><span class="count">${a.count} ${t('张')}</span></div>`;
      card.addEventListener('click', () => load(a.path, true));
      card.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        showAlbumMenu(e.clientX, e.clientY, a.path);
      });
      grid.appendChild(card);
    }
    content.appendChild(grid);
  }

  // ---- 虚拟化照片网格：只渲染视口附近单元格，滚动时增删，避免一次渲染上万节点爆内存 ----
  let _vg = null;

  function disposeVg() {
    if (_vg) { _vg.dispose(); _vg = null; }
  }

  /** 渲染图片网格（虚拟化）。点击图片 → 打开查看器。 */
  function renderPhotoGrid(photos) {
    disposeVg();
    const grid = document.createElement('div');
    grid.className = 'photo-grid vg';
    content.appendChild(grid);
    const spacer = document.createElement('div');
    spacer.className = 'vg-spacer';
    grid.appendChild(spacer);

    const vg = {
      photos, grid, spacer,
      gap: 14, nameH: 26, baseCellW: 160,
      cols: 1, cellW: 160, cellH: 186, rowH: 200, totalH: 0,
      cells: new Map(),     // 绝对索引 -> 单元格元素
      disposed: false,
      _raf: 0,
      _resizeTimer: 0,
      dispose() {
        this.disposed = true;
        if (this.onScroll) { content.removeEventListener('scroll', this.onScroll); this.onScroll = null; }
        window.removeEventListener('resize', this.onResize);
        if (this._raf) cancelAnimationFrame(this._raf);
        this.grid.remove();
      },
    };

    // 布局：根据网格宽度算列数与单元格尺寸（铺满行宽），撑起滚动高度
    const layout = () => {
      const cw = grid.clientWidth;
      if (cw <= 0) { vg._raf = requestAnimationFrame(layout); return; }
      vg.cols = Math.max(1, Math.floor((cw + vg.gap) / (vg.baseCellW + vg.gap)));
      vg.cellW = (cw - vg.gap * (vg.cols - 1)) / vg.cols;
      vg.cellH = vg.cellW + vg.nameH;
      vg.rowH = vg.cellH + vg.gap;
      vg.totalH = Math.ceil(vg.photos.length / vg.cols) * vg.rowH;
      spacer.style.height = vg.totalH + 'px';
      spacer.style.width = '1px';
      // 列数变了 → 清空重建
      if (vg.cells.size > 0 && vg.rebuiltCols !== vg.cols) {
        for (const el of vg.cells.values()) el.remove();
        vg.cells.clear();
      }
      vg.rebuiltCols = vg.cols;
      renderWindow();
    };

    // 计算网格在 content 滚动区内的顶部偏移
    const gridTop = () => grid.offsetTop - content.offsetTop;

    const renderWindow = () => {
      vg._raf = 0;
      if (vg.disposed) return;
      const scrollTop = content.scrollTop;
      const viewH = content.clientHeight;
      const gTop = gridTop();
      const firstRow = Math.max(0, Math.floor((scrollTop - gTop) / vg.rowH) - 2);   // 上方缓冲 2 行
      const totalRows = Math.ceil(vg.photos.length / vg.cols);
      const lastRow = Math.min(totalRows, Math.ceil((scrollTop - gTop + viewH) / vg.rowH) + 2);   // 下方缓冲 2 行

      // 移除滚出范围的单元格
      for (const [idx, el] of vg.cells) {
        const r = Math.floor(idx / vg.cols);
        if (r < firstRow || r >= lastRow) { el.remove(); vg.cells.delete(idx); }
      }
      // 补充进入范围的单元格（优先渲染视口可见部分 + 附近）
      for (let r = firstRow; r < lastRow; r++) {
        const start = r * vg.cols;
        const end = Math.min(start + vg.cols, vg.photos.length);
        for (let i = start; i < end; i++) {
          if (vg.cells.has(i)) continue;
          const p = vg.photos[i];
          const cell = document.createElement('div');
          cell.className = 'photo-cell';
          cell.dataset.index = i;
          cell.style.position = 'absolute';
          cell.style.left = ((i - start) * (vg.cellW + vg.gap)) + 'px';
          cell.style.top = (r * vg.rowH) + 'px';
          cell.style.width = vg.cellW + 'px';
          cell.style.height = vg.cellH + 'px';
          cell.innerHTML =
            `<img src="${p.thumb_url}" loading="lazy" alt="" style="width:100%;height:${vg.cellW}px">` +
            `<span class="photo-name">${esc(p.name)}</span>`;
          cell.addEventListener('click', () => openViewer(i));
          vg.cells.set(i, cell);
          grid.appendChild(cell);
        }
      }
    };

    vg.onScroll = () => {
      if (!vg._raf && !vg.disposed) vg._raf = requestAnimationFrame(renderWindow);
    };
    content.addEventListener('scroll', vg.onScroll, { passive: true });

    vg.onResize = () => {
      // 窗口尺寸变化 → 重新布局（防抖）
      clearTimeout(vg._resizeTimer);
      vg._resizeTimer = setTimeout(() => { if (!vg.disposed) layout(); }, 150);
    };
    window.addEventListener('resize', vg.onResize);

    _vg = vg;
    requestAnimationFrame(layout);
  }

  // ---- 列表分块渲染 ----
  let _listChunkTimer = null;
  function cancelListChunk() { if (_listChunkTimer) { clearTimeout(_listChunkTimer); _listChunkTimer = null; } }

  /** 列表视图。相册页（inAlbum=false）：只列相册；图片页：相册 + 该相册图片（分块渲染防卡顿）。 */
  function renderList() {
    cancelListChunk();
    const table = document.createElement('table');
    table.className = 'list-table';
    content.appendChild(table);
    const thead = document.createElement('thead');
    thead.innerHTML = '<tr><th></th><th>名称</th><th>大小</th><th>修改时间</th></tr>';
    table.appendChild(thead);
    const tbody = document.createElement('tbody');
    table.appendChild(tbody);

    // 相册行（数量少，直接渲染）
    for (const a of state.albums) {
      const tr = document.createElement('tr');
      tr.className = 'album-row';
      tr.dataset.path = a.path;
      tr.innerHTML = '<td class="thumb-cell">📁</td>' +
        `<td>${esc(a.name)} <span class="album-count">${a.count} 张</span></td>` +
        '<td class="size-cell"></td><td class="date-cell"></td>';
      tr.addEventListener('click', () => load(a.path, true));
      tbody.appendChild(tr);
    }

    // 图片行分块渲染：先渲染首屏，其余每 16ms 补 300 行（图片 loading=lazy，内存可控）
    if (state.inAlbum) {
      const rows = state.photos;
      const chunk = 300;
      const build = (from, to) => {
        for (let i = from; i < to && i < rows.length; i++) {
          const p = rows[i];
          const tr = document.createElement('tr');
          tr.className = 'photo-row';
          tr.dataset.index = i;
          tr.innerHTML = `<td class="thumb-cell"><img class="thumb" src="${p.thumb_url}" loading="lazy" alt=""></td>` +
            `<td>${esc(p.name)}</td>` +
            `<td class="size-cell">${formatSize(p.size)}</td>` +
            `<td class="date-cell">${formatTime(p.modified)}</td>`;
          tr.addEventListener('click', () => openViewer(i));
          tbody.appendChild(tr);
        }
      };
      build(0, chunk);
      let from = chunk;
      const appendMore = () => {
        _listChunkTimer = null;
        if (from >= rows.length) return;
        build(from, from + chunk);
        from += chunk;
        _listChunkTimer = setTimeout(appendMore, 16);
      };
      if (from < rows.length) _listChunkTimer = setTimeout(appendMore, 16);
    }

    const emptyNeeded = state.inAlbum
      ? state.photos.length === 0 && state.albums.length === 0
      : state.albums.length === 0;
    if (emptyNeeded) {
      const empty = document.createElement('div');
      empty.className = 'empty-hint';
      empty.textContent = state.inAlbum ? '此相册没有图片' : '此目录没有相册';
      content.appendChild(empty);
    }
  }

  // ==================== 查看器 ====================

  function openViewer(index) {
    if (state.photos.length === 0) return;
    cancelZoomAnim();
    state.index = ((index % state.photos.length) + state.photos.length) % state.photos.length;
    state.scale = 1;
    state.translateX = 0;
    state.translateY = 0;
    state.rotate = 0;
    state.isFitMode = true;
    viewer.classList.remove('hidden');
    applyViewerBg();
    updateViewerImage();
  }

  function closeViewer() {
    viewer.classList.add('hidden');
    viewerImg.onload = null;
    state.dragging = false;              // 右键关闭时清理拖拽状态，避免下次打开残留平移
    viewerStage.classList.remove('dragging');
    if (state.index < 0 && state.droppedUrl) {
      URL.revokeObjectURL(state.droppedUrl);
      state.droppedUrl = null;
      state.droppedName = '';
    }
  }

  /** 查看器背景色：0=默认灰 1=白 2=黑。改 .viewer 的背景（图片周围环境色），打开查看器/切换图片时保持上次选择。 */
  const VIEWER_BGS = ['rgba(0, 0, 0, 0.94)', '#ffffff', '#000000'];
  function applyViewerBg() {
    viewer.style.background = VIEWER_BGS[state.bg] || VIEWER_BGS[0];
  }

  /** 拖入图片：直接在查看器显示（blob URL，非相册列表）。 */
  function showDroppedImage(file) {
    if (!file) return;
    if (state.droppedUrl) URL.revokeObjectURL(state.droppedUrl);
    state.droppedUrl = URL.createObjectURL(file);
    state.droppedName = file.name;
    state.index = -1;   // 不属于当前相册列表
    cancelZoomAnim();
    state.scale = 1;
    state.translateX = 0;
    state.translateY = 0;
    state.rotate = 0;
    state.isFitMode = true;
    viewer.classList.remove('hidden');
    applyViewerBg();
    viewerImg.onload = () => { fitToWindow(); updateTitle(); };
    viewerImg.src = state.droppedUrl;
    updateTitle();
  }

  /** 预览指定路径的图片（识别结果右键等场景），复用主查看器（可缩放/平移/旋转）。 */
  function previewFromPath(path, name) {
    state.index = -1;
    state.droppedUrl = '/api/photo?path=' + encodeURIComponent(path);
    state.droppedName = name || '';
    cancelZoomAnim();
    state.scale = 1;
    state.translateX = 0;
    state.translateY = 0;
    state.rotate = 0;
    state.isFitMode = true;
    viewer.classList.remove('hidden');
    applyViewerBg();
    viewerImg.onload = () => { fitToWindow(); updateTitle(); };
    viewerImg.src = state.droppedUrl;
    updateTitle();
    updateZoomLabel();
  }

  /** 加载当前图片并进入 fit 模式（图片加载完成后适应窗口）。 */
  function updateViewerImage() {
    const p = state.photos[state.index];
    viewerImg.onload = () => {
      fitToWindow();
      updateTitle();
    };
    viewerImg.src = p.url;
    updateTitle();
    updateZoomLabel();
  }

  function updateTitle() {
    if (state.index < 0) {
      if (state.droppedName) viewerTitle.textContent = state.droppedName + t('（拖入）');
      else if (state.pendingName) viewerTitle.textContent = state.pendingName;
      else viewerTitle.textContent = t('图片');
      return;
    }
    const p = state.photos[state.index];
    const size = p.size ? ' · ' + formatSize(p.size) : '';
    viewerTitle.textContent =
      `${p.name}  (${state.index + 1} / ${state.photos.length})` + size +
      `  [${viewerImg.naturalWidth || '-'}×${viewerImg.naturalHeight || '-'}]`;
  }

  /** 计算让图片适应窗口的缩放比（旋转 90/270 时宽高互换）。 */
  function fitToWindow() {
    cancelZoomAnim();
    if (viewerImg.naturalWidth === 0) return;
    const stageW = viewerStage.clientWidth;
    const stageH = viewerStage.clientHeight;
    let w = viewerImg.naturalWidth;
    let h = viewerImg.naturalHeight;
    if (Math.abs(state.rotate) % 180 !== 0) { const t = w; w = h; h = t; }
    const s = Math.min(stageW / w, stageH / h) * 0.9;
    state.scale = s;
    state.translateX = 0;
    state.translateY = 0;
    applyTransform();
  }

  function applyTransform() {
    const r = ((state.rotate % 360) + 360) % 360;
    viewerImg.style.transform =
      `translate(-50%, -50%) translate(${state.translateX}px, ${state.translateY}px) ` +
      `scale(${state.scale}) rotate(${r}deg)`;
    updateZoomLabel();
  }

  function updateZoomLabel() {
    zoomLabel.textContent = Math.round(state.scale * 100) + '%';
  }

  // ---- 连续平滑缩放（常驻阻尼逼近，避免逐事件重启动画造成回弹/脉冲） ----
  let _zoomTarget = null;   // 当前缩放目标 { scale, tx, ty }
  let _zoomLoop = 0;        // 常驻动画循环 id

  function cancelZoomAnim() {
    _zoomTarget = null;
    if (_zoomLoop) { cancelAnimationFrame(_zoomLoop); _zoomLoop = 0; }
  }

  /** 图片当前渲染尺寸是否完全在查看器内（任一边未超出界面）。 */
  function imageFitsViewport() {
    if (viewerImg.naturalWidth === 0) return true;
    let w = viewerImg.naturalWidth * state.scale;
    let h = viewerImg.naturalHeight * state.scale;
    if (Math.abs(state.rotate) % 180 !== 0) { const t = w; w = h; h = t; }
    return w <= viewerStage.clientWidth && h <= viewerStage.clientHeight;
  }

  /**
   * 更新缩放目标并启动（或复用）常驻逼近循环。
   * 滚轮连续触发时只更新目标，循环按指数阻尼平滑逼近，不重启、不超调、无回弹。
   * mode: 'center'=向中心缩放（translate 归零）；'anchor'=保持鼠标锚点不动；'proportional'=保持当前视点比例。
   */
  function animateZoomTo(targetScale, mode, anchorX, anchorY) {
    if (Math.abs(targetScale - state.scale) < 0.0005) return;
    state.isFitMode = false;
    const ratio = targetScale / state.scale;
    let tTx, tTy;
    if (mode === 'center') {
      tTx = 0;
      tTy = 0;
    } else {
      tTx = state.translateX * ratio;
      tTy = state.translateY * ratio;
      if (mode === 'anchor' && anchorX !== undefined && anchorY !== undefined) {
        tTx += anchorX * (1 - ratio);
        tTy += anchorY * (1 - ratio);
      }
    }
    _zoomTarget = { scale: targetScale, tx: tTx, ty: tTy };
    if (!_zoomLoop) _zoomLoop = requestAnimationFrame(zoomStep);
  }

  /** 每帧向目标逼近固定百分比（指数阻尼，渐近不超调），接近后吸附到目标。 */
  function zoomStep() {
    _zoomLoop = 0;
    const t = _zoomTarget;
    if (!t) return;
    const k = 0.32;   // 每帧逼近比例（越大越快）
    state.scale += (t.scale - state.scale) * k;
    state.translateX += (t.tx - state.translateX) * k;
    state.translateY += (t.ty - state.translateY) * k;
    applyTransform();
    const done = Math.abs(t.scale - state.scale) < 0.0004
      && Math.abs(t.tx - state.translateX) < 0.05
      && Math.abs(t.ty - state.translateY) < 0.05;
    if (done) {
      state.scale = t.scale;
      state.translateX = t.tx;
      state.translateY = t.ty;
      applyTransform();
      _zoomTarget = null;
      return;
    }
    _zoomLoop = requestAnimationFrame(zoomStep);
  }

  /** 缩放。factor>1 放大。带鼠标锚点：图片未超出界面→中心缩放；已超出→以鼠标为中心缩放。 */
  function zoomBy(factor, anchorX, anchorY) {
    const newScale = clamp(state.scale * factor, MIN_SCALE, MAX_SCALE);
    if (newScale === state.scale) return;
    state.isFitMode = false;
    let mode = 'proportional';
    if (anchorX !== undefined && anchorY !== undefined) {
      mode = imageFitsViewport() ? 'center' : 'anchor';
    }
    animateZoomTo(newScale, mode, anchorX, anchorY);
  }

  function setActualSize() {
    cancelZoomAnim();
    state.scale = 1;
    state.translateX = 0;
    state.translateY = 0;
    state.isFitMode = false;
    applyTransform();
  }

  function rotateBy(delta) {
    state.rotate += delta;
    applyTransform();
    if (state.isFitMode) fitToWindow();   // 旋转后保持图片在窗口内
  }

  function resetRotate() {
    state.rotate = 0;
    applyTransform();
    if (state.isFitMode) fitToWindow();
  }

  function prev() {
    if (state.index < 0) return;   // 拖入的图片无前后切换
    state.index = (state.index - 1 + state.photos.length) % state.photos.length;
    updateViewerImage();
  }

  function next() {
    if (state.index < 0) return;
    state.index = (state.index + 1) % state.photos.length;
    updateViewerImage();
  }

  /** 导出旋转后的图片（后端 ImageSharp 生成并下载；拖入图片无后端路径，跳过）。 */
  function exportRotated() {
    if (state.index < 0) return;
    const p = state.photos[state.index];
    const deg = ((state.rotate % 360) + 360) % 360;
    const url = '/api/photo/export?path=' + encodeURIComponent(p.path) + '&rotate=' + deg;
    const a = document.createElement('a');
    a.href = url;
    a.download = 'rotated_' + p.name;
    document.body.appendChild(a);
    a.click();
    a.remove();
  }

  // ==================== 事件绑定 ====================

  // ==================== 桌面版窗口控制（自绘标题栏） ====================
  const getChrome = () => (typeof window.chromeHost !== 'undefined') ? window.chromeHost : null;

  function initWindowControls() {
    const tb = $('winTitlebar');
    const hasChrome = !!getChrome();
    tb.style.display = hasChrome ? 'flex' : 'none';   // 浏览器版隐藏标题栏
    // 查看器不覆盖标题栏+工具栏：CSS 变量 --tb-h 决定其顶部起始位置（窗口仍可拖动）
    document.documentElement.style.setProperty('--tb-h', hasChrome ? '40px' : '0px');
    if (!hasChrome) return;

    const isClickable = (e) => !!e.target.closest('button, input, .win-controls');

    // 拖动窗口：标题栏空白区域按下
    tb.addEventListener('mousedown', (e) => {
      if (isClickable(e)) return;
      try { getChrome().start_drag(); } catch { }
    });
    // 双击标题栏切换最大化 / 还原
    tb.addEventListener('dblclick', (e) => {
      if (isClickable(e)) return;
      toggleMaximize();
    });
    $('winMinBtn').addEventListener('click', () => { try { getChrome().minimize(); } catch { } });
    $('winMaxBtn').addEventListener('click', toggleMaximize);
    $('winCloseBtn').addEventListener('click', () => { try { getChrome().close(); } catch { } });
    // 初始同步全屏按钮图标
    try { Promise.resolve(getChrome().is_maximized()).then(setMaxIcon).catch(() => { }); } catch { }
    // 拖动退出全屏等场景桥接无法主动通知，轮询保持图标与全屏状态一致
    setInterval(async () => {
      const ch = getChrome();
      if (!ch || !ch.is_maximized) return;
      try { setMaxIcon(await Promise.resolve(ch.is_maximized())); } catch { }
    }, 1000);
  }

  function toggleMaximize() {
    const ch = getChrome();
    if (!ch || !ch.toggle_maximize) return;
    try { Promise.resolve(ch.toggle_maximize()).then(setMaxIcon).catch(() => { }); } catch { }
  }

  /** 全屏按钮图标：非全屏 = □（进入全屏），全屏 = ❐（退出全屏）。用系统字体可靠的符号，避免冷门字符渲染成豆腐块。 */
  function setMaxIcon(isMax) {
    const btn = $('winMaxBtn');
    btn.textContent = isMax ? '❐' : '□';
    btn.title = isMax ? '退出全屏' : '全屏';
  }

  // ==================== 右键菜单（图片标签） ====================
  let _ctxPath = '';
  let _ctxIndex = -1;
  let _ctxImageTags = [];   // 当前右键图片已有的标签（子菜单高亮用）
  let _ctxAllTags = [];     // 子菜单全量标签（搜索过滤用）
  let _ctxQuery = '';       // 子菜单标签搜索关键词
  let _subHideTimer = 0;

  function bindContextMenu() {
    // 右键图片 → 弹自定义菜单（打开 / 添加标签 ▸ / 复制路径）
    document.addEventListener('contextmenu', (e) => {
      const cell = e.target.closest('.photo-cell, .photo-row');
      if (!cell || typeof cell.dataset.index === 'undefined') return;
      e.preventDefault();
      _ctxIndex = Number(cell.dataset.index);
      _ctxPath = state.photos[_ctxIndex] ? state.photos[_ctxIndex].path : '';
      showCtxMenu(e.clientX, e.clientY);
    });

    // 主菜单项点击
    $('ctxMenu').addEventListener('click', (e) => {
      const item = e.target.closest('.ctx-item');
      if (!item) return;
      const act = item.dataset.act;
      if (act === 'open') { closeCtxMenu(); if (_ctxIndex >= 0) openViewer(_ctxIndex); }
      else if (act === 'recognize') { closeCtxMenu(); openAiPanel(_ctxPath); }
      else if (act === 'copy') { copyPath(_ctxPath); closeCtxMenu(); }
      // act === 'tag'：由右侧子菜单处理
    });

    // 悬停「添加标签」→ 右侧显示标签子菜单
    $('ctxMenu').addEventListener('mouseover', (e) => {
      if (e.target.closest('[data-act="tag"]')) { clearTimeout(_subHideTimer); showSubmenu(); }
    });
    $('ctxMenu').addEventListener('mouseleave', (e) => {
      if (!e.relatedTarget || !e.relatedTarget.closest('#ctxSubmenu')) hideSubmenuLater();
    });
    $('ctxSubmenu').addEventListener('mouseenter', () => clearTimeout(_subHideTimer));
    $('ctxSubmenu').addEventListener('mouseleave', hideSubmenuLater);

    // 点击菜单外部 / 滚动 / Esc 关闭
    document.addEventListener('mousedown', (e) => {
      if (!e.target.closest('.ctx-menu, .ctx-submenu')) closeCtxMenu();
      if (_albumMenu && !e.target.closest('.album-ctx-menu')) closeAlbumMenu();
    });
    // 主内容滚动（翻页等）时关闭菜单；子菜单（标签列表）内部滚动不关闭——否则滚轮滚标签列表会把菜单关掉
    document.addEventListener('scroll', (e) => {
      const t = e.target;
      if (t && t.closest && t.closest('#ctxSubmenu')) return;
      closeCtxMenu();
    }, true);
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeCtxMenu(); });
  }

  function showCtxMenu(x, y) {
    const menu = $('ctxMenu');
    hideSubmenuLater();
    menu.style.left = Math.min(x, window.innerWidth - 190) + 'px';
    menu.style.top = Math.min(y, window.innerHeight - 130) + 'px';
    menu.classList.remove('hidden');
    loadCtxSubmenu();   // 预加载标签列表
  }

  async function loadCtxSubmenu() {
    const sub = $('ctxSubmenu');
    sub.innerHTML = '<div class="ctx-sub-item" style="color:var(--text-dim)">加载中…</div>';
    let tags = [];
    let imageTags = [];
    try {
      const [tRes, iRes] = await Promise.all([
        fetch('/api/tags'),
        fetch('/api/tags/image?path=' + encodeURIComponent(_ctxPath)),
      ]);
      tags = (await tRes.json()).tags || [];
      imageTags = (await iRes.json()).tags || [];
    } catch { }
    _ctxAllTags = tags;
    _ctxImageTags = imageTags;
    sub.innerHTML = '';

    // 顶部搜索框：输入时按关键词过滤标签列表（标签列表单独容器重建，不丢焦点）
    const search = document.createElement('input');
    search.type = 'text';
    search.className = 'ctx-sub-search';
    search.placeholder = '搜索标签…';
    search.spellcheck = false;
    search.autocomplete = 'off';
    search.value = _ctxQuery;
    search.addEventListener('input', () => { _ctxQuery = search.value.trim(); renderCtxTags(); });
    search.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeCtxMenu(); else e.stopPropagation(); });
    sub.appendChild(search);

    renderCtxTags();
  }

  /** 按当前搜索词渲染标签列表（搜索框保留在顶部，输入时不丢焦点）。 */
  function renderCtxTags() {
    const sub = $('ctxSubmenu');
    let list = sub.querySelector('.ctx-sub-list');
    if (!list) {
      list = document.createElement('div');
      list.className = 'ctx-sub-list';
      sub.appendChild(list);
    }
    list.innerHTML = '';
    const q = _ctxQuery.toLowerCase();
    const filtered = _ctxAllTags.filter(t => t.toLowerCase().includes(q));
    for (const t of filtered) {
      const has = _ctxImageTags.includes(t);
      const item = document.createElement('div');
      item.className = 'ctx-sub-item' + (has ? ' active' : '');
      item.textContent = '🏷 ' + t;
      item.title = has ? t('点击移除标签') : t('点击添加标签');
      item.addEventListener('click', () => toggleCtxTag(t));
      list.appendChild(item);
    }
    if (!filtered.length) {
      const empty = document.createElement('div');
      empty.className = 'ctx-sub-empty';
      empty.textContent = q ? '没有匹配的标签' : '还没有标签';
      list.appendChild(empty);
    }
    const addNew = document.createElement('div');
    addNew.className = 'ctx-sub-item ctx-sub-new';
    addNew.textContent = '＋ 新建标签…';
    addNew.addEventListener('click', () => { closeCtxMenu(); createTagThenAdd(); });
    list.appendChild(addNew);
  }

  /** 子菜单标签点击：已有→移除，没有→添加；刷新高亮。 */
  async function toggleCtxTag(tag) {
    const has = _ctxImageTags.includes(tag);
    try {
      if (has) {
        await fetch('/api/tags/image?path=' + encodeURIComponent(_ctxPath) + '&tag=' + encodeURIComponent(tag), { method: 'DELETE' });
      } else {
        await fetch('/api/tags/image', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ path: _ctxPath, tag }),
        });
      }
    } catch { }
    await loadCtxSubmenu();   // 刷新高亮（保持菜单打开，可连续操作）
  }

  function showSubmenu() {
    clearTimeout(_subHideTimer);
    const menu = $('ctxMenu');
    const tagItem = menu.querySelector('[data-act="tag"]');
    if (!tagItem) return;
    const r = tagItem.getBoundingClientRect();
    const sub = $('ctxSubmenu');
    sub.style.left = Math.min(r.right + 2, window.innerWidth - 190) + 'px';
    sub.style.top = r.top + 'px';
    sub.classList.remove('hidden');
    // 自动聚焦搜索框（可直接输入筛选标签）
    const search = sub.querySelector('.ctx-sub-search');
    if (search) { search.focus(); search.select(); }
  }

  function hideSubmenuLater() {
    clearTimeout(_subHideTimer);
    _subHideTimer = setTimeout(() => $('ctxSubmenu').classList.add('hidden'), 180);
  }

  function closeCtxMenu() {
    $('ctxMenu').classList.add('hidden');
    $('ctxSubmenu').classList.add('hidden');
    clearTimeout(_subHideTimer);
  }

  /** 给当前右键的图片加标签。 */
  async function addTagToImage(tag) {
    try {
      await fetch('/api/tags/image', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: _ctxPath, tag }),
      });
    } catch { }
    closeCtxMenu();
  }

  /** 新建标签并加到当前图片。 */
  async function createTagThenAdd() {
    const name = await showModal({ title: t('新建标签'), message: t('输入标签名称：'), type: 'prompt' });
    if (!name) return;
    try {
      await fetch('/api/tags/image', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: _ctxPath, tag: name }),
      });
    } catch { }
    if (!$('ctxMenu').classList.contains('hidden')) await loadCtxSubmenu();
  }

  /** 复制图片路径到剪贴板。 */
  async function copyPath(path) {
    try {
      await navigator.clipboard.writeText(path);
    } catch {
      const ta = document.createElement('textarea');
      ta.value = path;
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); } catch { }
      ta.remove();
    }
  }

  // ==================== 角色识别（AI，对接用户配置的模型 API） ====================
  let _aiPath = '';        // 当前识别面板对应的图片路径

  // ==================== 一键识别相册（批量） ====================
  let _batchStop = false;       // 用户取消识别
  let _batchAbort = null;       // 当前识别请求的 AbortController（取消时立即中断挂起的请求）

  /** 上报任务栏进度（HTTP 中转，异步 fire-and-forget，不阻塞 JS 主线程；浏览器版无宿主则忽略）。 */
  function setTaskbarProgress(value, state) {
    try {
      fetch('/api/taskbar-progress', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value, state }),
      }).catch(() => { });
    } catch { }
  }
  let _batchResults = [];       // [{path, name, thumbUrl, tag}] tag 为空串 = 识别失败
  let _brSelTag = '';           // 预览界面当前选中的标签（'__fail__' = 失败组）
  let _brEditPath = '';         // 正在修改标签的图片路径

  /** 打开识别面板并自动识别。path 来自右键菜单或查看器当前图片；拖入的临时图（blob）无法识别。 */
  async function openAiPanel(path) {
    if (!path || path.startsWith('blob:')) {
      showModal({ title: t('识别角色'), message: t('拖入的临时图片无法识别，请在相册中打开图片后再识别。') });
      return;
    }
    _aiPath = path;
    $('aiFile').textContent = t('图片：') + path.split(/[\\/]/).pop();
    $('aiResults').innerHTML = '<div class="ai-hint">' + t('识别中…') + '</div>';
    $('aiModal').classList.remove('hidden');
    runRecognition();
  }

  /** 调用后端代理 /api/ai/recognize 识别当前图片，渲染 top 候选。 */
  async function runRecognition() {
    if (!_aiPath) return;
    const results = $('aiResults');
    results.innerHTML = '<div class="ai-hint">' + t('识别中…') + '</div>';
    try {
      const res = await fetch('/api/ai/recognize?path=' + encodeURIComponent(_aiPath), { method: 'POST' });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        const err = apiErr(data, t('识别失败') + '（HTTP ' + res.status + '）');
        results.innerHTML = '<div class="ai-error">' + err + '</div>';
        return;
      }
      renderAiResults(data);
    } catch {
      results.innerHTML = '<div class="ai-error">无法连接识别服务</div>';
    }
  }

  /** 渲染识别结果：top 候选（角色名 + 置信度条）+ 每个候选可一键加为标签。 */
  function renderAiResults(data) {
    const results = $('aiResults');
    results.innerHTML = '';
    const top = data.top || [];
    if (!top.length) {
      results.innerHTML = '<div class="ai-hint">未识别到角色</div>';
      return;
    }
    for (const r of top) {
      const row = document.createElement('div');
      row.className = 'ai-result-row';
      const name = document.createElement('span');
      name.className = 'ai-role-name';
      name.textContent = r.class || '未知';
      const bar = document.createElement('div');
      bar.className = 'ai-conf-bar';
      const fill = document.createElement('div');
      fill.className = 'ai-conf-fill';
      fill.style.width = Math.max(2, Math.round((r.confidence || 0) * 100)) + '%';
      bar.appendChild(fill);
      const pct = document.createElement('span');
      pct.className = 'ai-conf-pct';
      pct.textContent = Math.round((r.confidence || 0) * 100) + '%';
      const tagBtn = document.createElement('button');
      tagBtn.className = 'tool-btn ai-tag-btn';
      tagBtn.textContent = '＋ 标签';
      tagBtn.title = '把「' + (r.class || '') + '」添加为这张图片的标签';
      tagBtn.addEventListener('click', () => addAiTag(r.class, tagBtn));
      row.append(name, bar, pct, tagBtn);
      results.appendChild(row);
    }
    const hint = document.createElement('div');
    hint.className = 'ai-hint';
    hint.textContent = t('点击「＋ 标签」把角色添加为该图标签');
    results.appendChild(hint);
  }

  /** 把识别出的角色作为标签加到当前图片。 */
  async function addAiTag(tag, btn) {
    if (!tag) return;
    try {
      await fetch('/api/tags/image', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: _aiPath, tag }),
      });
      btn.textContent = '✓ 已添加';
      btn.disabled = true;
    } catch { }
  }

  function closeAiPanel() {
    $('aiModal').classList.add('hidden');
  }

  // ==================== 一键识别相册（批量识别 → 核对/修改 → 确定才写入标签） ====================

  /** 逐张识别当前相册全部图片，弹出进度框；完成后进入结果预览。 */
  async function openBatchRecognize() {
    const photos = state.photos;
    if (!photos.length) {
      showModal({ title: '一键识别', message: '此相册没有图片。' });
      return;
    }
    // 立即显示检测进度框，避免检测期间无反馈像卡住
    const prog = $('batchProgress');
    $('bpCount').textContent = t('检测连接…');
    $('bpBar').parentElement.style.display = 'none';   // 检测阶段隐藏整个进度条轨道
    $('bpBar').style.width = '0%';
    $('bpFile').textContent = t('正在检测角色识别 API…');
    $('bpPath').textContent = '';
    prog.classList.remove('hidden');
    const sleep = (ms) => new Promise(r => setTimeout(r, ms));
    await sleep(30);   // 先让检测界面渲染出来

    // 1) 确认角色识别 API 已配置
    let cfg = null;
    try {
      const cfgRes = await fetch('/api/ai/config');
      cfg = await cfgRes.json().catch(() => ({}));
    } catch { }
    if (!cfg || !cfg.api_url) {
      prog.classList.add('hidden');
      showModal({ title: '一键识别', message: '尚未配置角色识别 API。请到 设置 → 🔍 识别功能 中填写 API 地址。' });
      return;
    }
    // 2) 检测 API 服务是否在线：未连接则提示失败，不进入识别流程（避免进度条空转）
    try {
      const pingRes = await fetch('/api/ai/ping');
      const ping = await pingRes.json().catch(() => ({}));
      if (!ping.ok) {
        prog.classList.add('hidden');
        showModal({ title: t('一键识别'), message: t('角色识别 API 连接失败：') + esc(t(ping.error || '服务未启动或地址不可达')) + t('。请确认识别服务已启动。'), type: 'error' });
        return;
      }
    } catch {
      prog.classList.add('hidden');
      showModal({ title: t('一键识别'), message: t('无法检测角色识别 API 连接，请稍后重试。'), type: 'error' });
      return;
    }

    // 3) 连接正常：进入逐张识别
    _batchStop = false;
    _batchResults = [];
    _brSelTag = '';
    $('bpCount').textContent = '0 / ' + photos.length;
    $('bpBar').parentElement.style.display = '';   // 识别阶段恢复进度条轨道
    $('bpBar').style.width = '0%';
    $('bpFile').textContent = t('准备识别…');
    $('bpPath').textContent = '';
    setTaskbarProgress(0, 1);   // 任务栏绿色进度条开始（HTTP 异步上报）

    for (let i = 0; i < photos.length; i++) {
      if (_batchStop) break;
      const p = photos[i];
      $('bpCount').textContent = (i + 1) + ' / ' + photos.length;
      $('bpBar').style.width = Math.round((i / photos.length) * 100) + '%';
      setTaskbarProgress((i + 1) / photos.length, 1);   // 任务栏绿色进度推进
      $('bpFile').textContent = p.name;
      $('bpPath').textContent = p.path;
      // 每张识别请求带 AbortController：取消/超时都能立即中断，避免进度条挂死
      const controller = new AbortController();
      _batchAbort = controller;
      const timer = setTimeout(() => controller.abort(), 30000);   // 单张 30s 超时
      try {
        const res = await fetch('/api/ai/recognize?path=' + encodeURIComponent(p.path),
          { method: 'POST', signal: controller.signal });
        const data = await res.json().catch(() => ({}));
        if (res.ok && data.top && data.top.length) {
          _batchResults.push({ path: p.path, name: p.name, thumbUrl: p.thumb_url, tag: data.top[0].class });
        } else {
          _batchResults.push({ path: p.path, name: p.name, thumbUrl: p.thumb_url, tag: '', err: apiErr(data, t('识别失败')) });
        }
      } catch (err) {
        // 用户取消或单张超时（AbortError）不记为失败；只有真正网络错误才记为失败
        if (!(err && err.name === 'AbortError') && !_batchStop) {
          _batchResults.push({ path: p.path, name: p.name, thumbUrl: p.thumb_url, tag: '', err: t('无法连接识别服务') });
        }
      } finally {
        clearTimeout(timer);
        _batchAbort = null;
      }
      await sleep(0);   // 让进度条渲染
    }
    prog.classList.add('hidden');
    setTaskbarProgress(0, 0);   // 识别结束/停止：清除任务栏进度
    if (!_batchResults.length) return;
    openBatchReview();
  }

  /** 识别完毕：打开结果预览（标签单选 → 点标签看图片 → 点图可修改标签）。 */
  function openBatchReview() {
    $('batchReview').classList.remove('hidden');
    const total = _batchResults.length;
    const okCount = _batchResults.filter(r => r.tag).length;
    const failCount = total - okCount;
    $('brHint').textContent =
      t('共识别') + ' ' + total + ' ' + t('张，成功') + ' ' + okCount + ' ' + t('张') +
      (failCount ? '，' + failCount + ' ' + t('张失败') : '') +
      '。' + t('点标签查看图片，点图片可修改标签；「确定写入」才会保存。');
    const first = _batchResults.find(r => r.tag);
    _brSelTag = first ? first.tag : '__fail__';
    renderBatchTags();
    renderBatchGrid();
  }

  /** 渲染预览顶部标签 chips（单选）。 */
  function renderBatchTags() {
    const box = $('brTags');
    box.innerHTML = '';
    const tagMap = {};
    let failCount = 0;
    for (const r of _batchResults) {
      if (!r.tag) { failCount++; continue; }
      tagMap[r.tag] = (tagMap[r.tag] || 0) + 1;
    }
    const tags = Object.keys(tagMap).sort();
    for (const t of tags) {
      const chip = document.createElement('button');
      chip.className = 'br-chip' + (t === _brSelTag ? ' active' : '');
      chip.textContent = t + ' (' + tagMap[t] + ')';
      chip.addEventListener('click', () => { _brSelTag = t; renderBatchTags(); renderBatchGrid(); });
      box.appendChild(chip);
    }
    if (failCount > 0) {
      const chip = document.createElement('button');
      chip.className = 'br-chip fail' + (_brSelTag === '__fail__' ? ' active' : '');
      chip.textContent = '⚠ ' + t('识别失败') + ' (' + failCount + ')';
      chip.addEventListener('click', () => { _brSelTag = '__fail__'; renderBatchTags(); renderBatchGrid(); });
      box.appendChild(chip);
    }
    if (!tags.length && !failCount) {
      box.innerHTML = '<div class="review-empty">' + t('没有识别结果') + '</div>';
    }
  }

  /** 渲染当前选中标签下的图片缩略图网格。 */
  function renderBatchGrid() {
    const grid = $('brGrid');
    grid.innerHTML = '';
    const items = _batchResults.filter(r => _brSelTag === '__fail__' ? !r.tag : r.tag === _brSelTag);
    if (!items.length) {
      grid.innerHTML = '<div class="review-empty">此分组没有图片</div>';
      return;
    }
    for (const it of items) {
      const card = document.createElement('div');
      card.className = 'br-card';
      card.title = it.path;
      const img = document.createElement('img');
      img.src = it.thumbUrl;
      img.loading = 'lazy';
      img.alt = '';
      const label = document.createElement('div');
      label.className = 'br-card-label' + (it.tag ? '' : ' fail');
      label.textContent = it.tag || (it.err || t('识别失败'));
      card.appendChild(img);
      card.appendChild(label);
      card.addEventListener('click', () => openBrEdit(it.path));
      // 右键图片：打开查看器预览（可缩放/平移/旋转）
      card.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        previewFromPath(it.path, it.name);
      });
      grid.appendChild(card);
    }
  }

  /** 修改某张图片的标签（下拉选已有标签 或 输入新标签名）。 */
  function openBrEdit(path) {
    const item = _batchResults.find(r => r.path === path);
    if (!item) return;
    _brEditPath = path;
    $('brEditName').textContent = item.name;
    $('brEditPath').textContent = item.path;
    const sel = $('brEditSelect');
    sel.innerHTML = '';
    const tagSet = {};
    for (const r of _batchResults) if (r.tag) tagSet[r.tag] = true;
    const tags = Object.keys(tagSet).sort();
    for (const t of tags) {
      const opt = document.createElement('option');
      opt.value = t;
      opt.textContent = t;
      if (t === item.tag) opt.selected = true;
      sel.appendChild(opt);
    }
    if (!tags.length) {
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = '（无标签）';
      sel.appendChild(opt);
    }
    $('brEditInput').value = '';
    $('brEdit').classList.remove('hidden');
    $('brEditInput').focus();
  }

  /** 保存单张图片的标签修改并刷新预览。 */
  function saveBrEdit() {
    const item = _batchResults.find(r => r.path === _brEditPath);
    if (item) {
      const fromSel = $('brEditSelect').value;
      const fromInput = $('brEditInput').value.trim();
      item.tag = fromInput || fromSel;
      if (item.tag) item.err = '';
    }
    $('brEdit').classList.add('hidden');
    // 若当前选中的标签因修改而消失，切到第一个有结果的标签
    if (_brSelTag !== '__fail__' && !_batchResults.some(r => r.tag === _brSelTag)) {
      const first = _batchResults.find(r => r.tag);
      _brSelTag = first ? first.tag : '__fail__';
    }
    renderBatchTags();
    renderBatchGrid();
  }

  /** 确定写入：逐张把预测标签写到图片（已含该标签跳过，没有则创建）。 */
  async function confirmBatch() {
    const okBtn = $('brOkBtn');
    okBtn.disabled = true;
    okBtn.textContent = '写入中…';
    let added = 0, skipped = 0;
    for (const r of _batchResults) {
      if (!r.tag) continue;   // 识别失败的不写
      try {
        const tagsRes = await fetch('/api/tags/image?path=' + encodeURIComponent(r.path));
        const tagsData = await tagsRes.json().catch(() => ({}));
        if ((tagsData.tags || []).includes(r.tag)) { skipped++; continue; }
        const w = await fetch('/api/tags/image', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ path: r.path, tag: r.tag }),
        });
        if (w.ok) added++; else skipped++;
      } catch { skipped++; }
    }
    okBtn.disabled = false;
    okBtn.textContent = '确定写入';
    $('batchReview').classList.add('hidden');
    showModal({ title: '一键识别', message: '写入完成：新增 ' + added + ' 条标签，跳过 ' + skipped + ' 条（已存在或失败）。' });
  }

  /** 取消预览（不写入标签）。 */
  function cancelBatchReview() {
    $('batchReview').classList.add('hidden');
  }

  function bindEvents() {
    initWindowControls();
    bindContextMenu();
    // 隐藏网页特征：禁用右键默认菜单（图片上另弹自定义菜单）、图片/文本拖拽
    document.addEventListener('contextmenu', (e) => e.preventDefault());
    document.addEventListener('dragstart', (e) => e.preventDefault());

    // 拖入图片：松开后直接在查看器显示（blob URL，不依赖后端路径）
    let dragDepth = 0;
    document.addEventListener('dragenter', (e) => { e.preventDefault(); dragDepth++; $('dropOverlay').classList.remove('hidden'); });
    document.addEventListener('dragover', (e) => e.preventDefault());
    document.addEventListener('dragleave', (e) => {
      dragDepth--;
      if (dragDepth <= 0) { dragDepth = 0; $('dropOverlay').classList.add('hidden'); }
    });
    document.addEventListener('drop', (e) => {
      e.preventDefault();
      dragDepth = 0;
      $('dropOverlay').classList.add('hidden');
      const files = e.dataTransfer && e.dataTransfer.files;
      if (!files || files.length === 0) return;
      const img = Array.from(files).find((f) => f.type && f.type.startsWith('image/'));
      if (img) showDroppedImage(img);
    });
    // 顶部工具栏
    $('btnHome').addEventListener('click', () => {
      if (!viewer.classList.contains('hidden')) return;   // 查看器打开时忽略，防误触
      goHome();
    });
    $('btnTags').addEventListener('click', openTagPage);
    $('btnAddFolder').addEventListener('click', addFolder);
    $('btnBack').addEventListener('click', async () => {
      if (!viewer.classList.contains('hidden')) return;   // 查看器打开时忽略，防误触
      // 标签页：退出回上一页
      if (state.tagPage) { state.tagPage = false; render(); return; }
      // 图片页：无条件回父目录的图片页（只退一级，无论父目录是否有图片）
      if (state.parent) { load(state.parent, true); return; }
      // 相册根图片页（无父目录）：回相册页
      if (state.inAlbum) { await loadAlbums(); load(state.root, false); }
    });
    $('viewAlbum').addEventListener('click', () => { state.view = 'album'; syncViewToggle(); render(); });
    $('viewList').addEventListener('click', () => { state.view = 'list'; syncViewToggle(); render(); });

    // 排序（仅图片页显示；随 viewToggle 显隐；每个相册单独保存）
    $('sortSelect').addEventListener('change', () => { state.sortBy = $('sortSelect').value; syncViewToggle(); saveSort(); render(); });
    $('sortOrderBtn').addEventListener('click', () => {
      state.sortOrder = state.sortOrder === 'asc' ? 'desc' : 'asc';
      syncViewToggle();
      saveSort();
      render();
    });

    // 设置：文件关联
    $('btnSettings').addEventListener('click', openSettings);
    $('settingsCloseBtn').addEventListener('click', closeSettings);
    $('assocApplyBtn').addEventListener('click', applyAssoc);
    $('settingsModal').addEventListener('mousedown', (e) => { if (e.target === $('settingsModal')) closeSettings(); });
    // 设置：语言切换 —— 点按钮弹出语言模态（每项带国旗），选择即保存 + 立即应用
    $('langBtn').addEventListener('click', openLangModal);
    $('langModal').addEventListener('mousedown', (e) => { if (e.target === $('langModal')) closeLangModal(); });
    // 设置：左侧板块切换
    document.querySelectorAll('.settings-nav-item').forEach((btn) => {
      btn.addEventListener('click', () => switchSettingsPanel(btn.dataset.panel));
    });
    // 设置：极速查看器开关（勾选即保存）
    $('fastViewerCb')?.addEventListener('change', async (e) => {
      try {
        await fetch('/api/fastviewer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ fast_viewer: e.target.checked }),
        });
      } catch { }
    });
    // 设置：显示详细错误信息（勾选即保存）
    $('showDetailCb').addEventListener('change', (e) => {
      fetch('/api/prefs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ show_detail_error: e.target.checked }),
      }).catch(() => { });
    });
    // 工具栏：主题切换（浅色 / 深色）——点击即切换并保存
    $('btnTheme').addEventListener('click', toggleTheme);
    // 设置：关闭模式（最小化到托盘 / 退出程序）——勾选即保存
    document.querySelectorAll('#panel-tray input[name="closeMode"]').forEach((r) => {
      r.addEventListener('change', () => {
        fetch('/api/prefs', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ close_to_tray: r.value === 'tray' }),
        }).catch(() => { });
      });
    });
    // 设置：关于——启用更新检查开关（即时保存；关闭后不检查也不显示 chip）
    $('updateCheckCb')?.addEventListener('change', (e) => {
      _updateCheckEnabled = e.target.checked;
      if (_updateCheckEnabled) checkUpdateOnStart();
      else applyUpdateChip(null);
      const btn = $('aboutUpdateBtn');
      if (btn) btn.disabled = !_updateCheckEnabled;
      fetch('/api/prefs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ update_check_enabled: e.target.checked }),
      }).catch(() => { });
    });
    // 设置：关于——打开 GitHub 项目 / 检查更新
    $('aboutGithubBtn')?.addEventListener('click', () => {
      fetch('/api/open-external?url=' + encodeURIComponent('https://github.com/HopingStar/Hoping-Image-Viewer'), { method: 'POST' })
        .catch(() => showModal({ title: t('提示'), message: t('无法打开浏览器'), type: 'error' }));
    });
    // 标题栏橙色更新 chip 点击 → 用默认浏览器打开最新版 Release 页面
    // 阻止 mousedown 冒泡到标题栏：否则按下会触发 start_drag（拖动窗口）把 click 吞掉
    $('updateChip')?.addEventListener('mousedown', (e) => e.stopPropagation());
    $('updateChip')?.addEventListener('click', () => {
      if (!_updateHasNew) return;
      fetch('/api/open-external?url=' + encodeURIComponent('https://github.com/HopingStar/Hoping-Image-Viewer/releases/latest'), { method: 'POST' })
        .catch(() => showModal({ title: t('提示'), message: t('无法打开浏览器'), type: 'error' }));
    });
    $('aboutUpdateBtn')?.addEventListener('click', async () => {
      const res = $('aboutUpdateResult');
      res.textContent = t('正在检查更新…');
      res.className = 'about-update-result';
      try {
        const resp = await fetch('/api/check-update');
        const d = await resp.json();
        if (!d.ok || d.error) {
          res.textContent = t('检查失败：') + t(d.error || '未知错误');
          res.className = 'about-update-result about-update-error';
          return;
        }
        if (d.has_update) {
          res.innerHTML = '发现新版本：v' + d.latest + '（当前 v' + d.current + '）<br>请前往 GitHub Releases 下载更新。';
          res.className = 'about-update-result about-update-new';
        } else {
          res.textContent = t('当前已是最新版本') + '（v' + d.current + '）';
          res.className = 'about-update-result';
        }
      } catch {
        res.textContent = t('检查失败：无法连接服务');
        res.className = 'about-update-result about-update-error';
      }
    });
    // 设置：查看器右键关闭开关——勾选即保存
    $('rccCb')?.addEventListener('change', (e) => {
      _rightClickClose = e.target.checked;
      fetch('/api/prefs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ right_click_close_viewer: e.target.checked }),
      }).catch(() => { });
    });
    // 设置：识别功能开关——勾选即保存并立即显隐所有识别入口
    $('aiEnabledCb')?.addEventListener('change', (e) => {
      _aiEnabled = e.target.checked;
      applyAiVisibility();
      fetch('/api/prefs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ai_enabled: e.target.checked }),
      }).catch(() => { });
    });
    // 设置：图片编辑器路径（查看器「✏️ 编辑」用哪个软件打开图片；空 = 自动探测画图 / Paint.NET）
    $('editorBrowseBtn')?.addEventListener('click', async () => {
      try {
        const resp = await fetch('/api/edit/pick?title=' + encodeURIComponent(t('选择图片编辑器（exe）')));
        const d = await resp.json();
        if (d && d.path && $('editorPathInput')) $('editorPathInput').value = d.path;
      } catch {
        showModal({ title: t('提示'), message: t('无法打开文件选择框'), type: 'error' });
      }
    });
    loadPrefs().then(checkUpdateOnStart);   // 加载应用偏好并按识别开关显隐入口；启动自动检查更新

    // 查看器按钮
    // 查看器内右键 → 直接关闭（不用移到右上角关闭按钮）；设置禁用后恢复默认（右键可拖拽）
    viewer.addEventListener('contextmenu', (e) => {
      if (!_rightClickClose) return;
      e.preventDefault();
      closeViewer();
    });
    $('viewerClose').addEventListener('click', closeViewer);
    $('btnZoomIn').addEventListener('click', () => zoomBy(ZOOM_STEP));
    $('btnZoomOut').addEventListener('click', () => zoomBy(1 / ZOOM_STEP));
    $('btnFit').addEventListener('click', () => { state.isFitMode = true; fitToWindow(); });
    $('btnActual').addEventListener('click', setActualSize);
    $('btnRotateL').addEventListener('click', () => rotateBy(-90));
    $('btnRotateR').addEventListener('click', () => rotateBy(90));
    $('btnReset').addEventListener('click', resetRotate);
    $('btnExport').addEventListener('click', exportRotated);
    $('btnRecognize').addEventListener('click', () => {
      if (state.index >= 0 && state.photos[state.index]) openAiPanel(state.photos[state.index].path);
      else showModal({ title: t('识别角色'), message: t('拖入的临时图片无法识别，请在相册中打开图片后再识别。') });
    });
    // 查看器背景：默认灰 → 白 → 黑 循环切换（切换即持久化，下次启动保持）
    $('btnBg').addEventListener('click', () => {
      state.bg = (state.bg + 1) % 3;
      applyViewerBg();
      fetch('/api/prefs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ viewer_bg: state.bg }),
      }).catch(() => { });
    });
    // 查看器「编辑」：用配置的 / 系统默认图片编辑器打开当前图片；
    // 未配置且未探测到编辑器时，打开设置「图片编辑」面板引导用户指定（不弹系统打开方式，避免误改文件关联）
    $('btnEdit').addEventListener('click', () => {
      const p = state.index >= 0 && state.photos[state.index] ? state.photos[state.index] : null;
      if (!p) {
        showModal({ title: t('编辑图片'), message: t('拖入的临时图片无法在外部编辑器中打开，请在相册中打开图片。'), type: 'error' });
        return;
      }
      fetch('/api/edit?path=' + encodeURIComponent(p.path), { method: 'POST' })
        .then(async (r) => {
          const d = await r.json().catch(() => ({}));
          if (d.need_config) {
            // 未配置编辑器：直接打开设置「图片编辑」面板引导配置（不弹报错弹窗）
            openSettings();
            switchSettingsPanel('editor');
            return;
          }
          if (!r.ok || !d.ok) {
            showModal({ title: t('提示'), message: esc(t(d.error || '无法打开外部编辑器')), type: 'error' });
          }
        })
        .catch(() => showModal({ title: t('提示'), message: t('无法打开外部编辑器'), type: 'error' }));
    });
    $('prevBtn').addEventListener('click', prev);
    $('nextBtn').addEventListener('click', next);

    // 角色识别面板
    $('aiReRunBtn').addEventListener('click', runRecognition);
    $('aiCloseBtn').addEventListener('click', closeAiPanel);
    $('aiModal').addEventListener('mousedown', (e) => { if (e.target === $('aiModal')) closeAiPanel(); });

    // 一键识别相册
    $('btnBatch').addEventListener('click', openBatchRecognize);
    $('bpCancelBtn').addEventListener('click', () => {
      _batchStop = true;
      _batchAbort?.abort();   // 立即中断当前挂起的识别请求，取消即时生效
    });
    $('brCancelBtn').addEventListener('click', cancelBatchReview);
    $('brOkBtn').addEventListener('click', confirmBatch);
    $('brEditCancelBtn').addEventListener('click', () => { $('brEdit').classList.add('hidden'); });
    $('brEditOkBtn').addEventListener('click', saveBrEdit);
    $('brEditInput').addEventListener('keydown', (e) => { if (e.key === 'Enter') saveBrEdit(); });
    $('brEdit').addEventListener('mousedown', (e) => { if (e.target === $('brEdit')) $('brEdit').classList.add('hidden'); });
    // 更换封面弹窗：取消 / 点击外部关闭
    $('coverCancelBtn').addEventListener('click', () => { $('coverModal').classList.add('hidden'); });
    $('coverModal').addEventListener('mousedown', (e) => { if (e.target === $('coverModal')) $('coverModal').classList.add('hidden'); });

    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      if (_albumMenu) { closeAlbumMenu(); return; }
      if (!$('coverModal').classList.contains('hidden')) { $('coverModal').classList.add('hidden'); return; }
      if (!$('aiModal').classList.contains('hidden')) { closeAiPanel(); return; }
      if (!$('brEdit').classList.contains('hidden')) { $('brEdit').classList.add('hidden'); return; }
      if (!$('batchReview').classList.contains('hidden')) { cancelBatchReview(); return; }
      if (!$('batchProgress').classList.contains('hidden')) { _batchStop = true; }
    });

    // 滚轮缩放（灵敏度 /110；图片未超出界面→中心缩放，超出→以鼠标为中心）
    // 锚点取「鼠标相对查看器中心」的偏移（缩放公式要求，否则会绕右下角缩放）
    viewerStage.addEventListener('wheel', (e) => {
      e.preventDefault();
      const rect = viewerStage.getBoundingClientRect();
      zoomBy(
        Math.pow(ZOOM_STEP, -e.deltaY / 110),
        e.clientX - rect.left - rect.width / 2,
        e.clientY - rect.top - rect.height / 2);
    }, { passive: false });

    // 拖拽平移（按下时取消缩放动画，避免残留目标把画面拉回）
    viewerStage.addEventListener('mousedown', (e) => {
      cancelZoomAnim();
      state.dragging = true;
      state.dragLastX = e.clientX;
      state.dragLastY = e.clientY;
      viewerStage.classList.add('dragging');
    });
    window.addEventListener('mousemove', (e) => {
      if (!state.dragging) return;
      state.translateX += e.clientX - state.dragLastX;
      state.translateY += e.clientY - state.dragLastY;
      state.dragLastX = e.clientX;
      state.dragLastY = e.clientY;
      applyTransform();
    });
    window.addEventListener('mouseup', () => {
      state.dragging = false;
      viewerStage.classList.remove('dragging');
    });

    // 双击：适应窗口 ↔ 2 倍
    viewerStage.addEventListener('dblclick', () => {
      if (state.isFitMode) { state.scale = 2; state.translateX = 0; state.translateY = 0; state.isFitMode = false; applyTransform(); }
      else { state.isFitMode = true; fitToWindow(); }
    });

    // 键盘快捷键（查看器打开时）
    document.addEventListener('keydown', (e) => {
      if (viewer.classList.contains('hidden')) return;
      switch (e.key) {
        case 'Escape': closeViewer(); break;
        case 'ArrowLeft': prev(); break;
        case 'ArrowRight': next(); break;
        case '+': case '=': zoomBy(ZOOM_STEP); break;
        case '-': case '_': zoomBy(1 / ZOOM_STEP); break;
        case '0': setActualSize(); break;
        case 'r': case 'R': resetRotate(); break;
      }
    });
  }

  function syncViewToggle() {
    $('viewAlbum').classList.toggle('active', state.view === 'album');
    $('viewList').classList.toggle('active', state.view === 'list');
    // 排序控件同步（字段下拉 + 方向按钮）
    $('sortSelect').value = state.sortBy;
    $('sortOrderBtn').textContent = state.sortOrder === 'desc' ? '↓' : '↑';
    $('sortOrderBtn').title = state.sortOrder === 'desc' ? t('当前降序，点击切换升序') : t('当前升序，点击切换降序');
  }

  // ==================== 模态弹窗（替代 alert/confirm/prompt） ====================

  /**
   * 应用内模态弹窗，返回 Promise。
   * type: 'alert' → resolve(undefined)；'confirm' → resolve(true/false)；'prompt' → resolve(字符串或 null)。
   */
  function showModal({ title, message, type = 'alert', defaultValue = '' }) {
    return new Promise((resolve) => {
      const overlay = $('modalOverlay');
      const titleEl = $('modalTitle');
      const msgEl = $('modalMessage');
      const input = $('modalInput');
      const cancelBtn = $('modalCancelBtn');
      const okBtn = $('modalOkBtn');

      titleEl.textContent = title || '';
      msgEl.innerHTML = message || '';
      // 错误类型（type:'error'）：消息红色字体
      msgEl.classList.toggle('modal-error', type === 'error');
      const isPrompt = type === 'prompt';
      const hasCancel = type === 'confirm' || type === 'prompt';
      input.style.display = isPrompt ? 'block' : 'none';
      input.value = defaultValue;
      cancelBtn.style.display = hasCancel ? 'inline-block' : 'none';

      overlay.classList.remove('hidden');
      if (isPrompt) { input.focus(); input.select(); }

      const cleanup = () => {
        overlay.classList.add('hidden');
        okBtn.onclick = null;
        cancelBtn.onclick = null;
        overlay.onclick = null;
        document.removeEventListener('keydown', onKey);
      };
      const finish = (val) => { cleanup(); resolve(val); };
      const onKey = (e) => {
        if (e.key === 'Escape') finish(hasCancel ? false : undefined);
        if (e.key === 'Enter' && isPrompt) {
          const v = input.value.trim();
          finish(v === '' ? null : v);
        }
      };
      document.addEventListener('keydown', onKey);

      okBtn.onclick = () => {
        if (isPrompt) {
          const v = input.value.trim();
          finish(v === '' ? null : v);
        } else {
          finish(true);
        }
      };
      cancelBtn.onclick = () => finish(false);
      overlay.onclick = (e) => { if (e.target === overlay && !hasCancel) finish(undefined); };
    });
  }

  // ==================== 设置：应用偏好（关闭模式 / 识别功能开关） ====================

  let _aiEnabled = true;   // 是否启用角色识别功能（关闭时隐藏所有识别入口）
  let _rightClickClose = true;   // 查看器内右键是否直接关闭（关闭后右键可拖拽）
  let _updateCheckEnabled = true;   // 启动时自动检查更新（关闭后不检查也不显示标题栏 chip）
  let _updateHasNew = false;        // 是否有可用更新（橙色 chip 点击打开 Release 页面）
  let _langPending = null;          // 语言暂存选择（点语言项仅暂存；点设置「应用」才保存并刷新整页）

  /** 启动时加载应用偏好：识别入口显隐 + 恢复主查看器背景（0=灰 1=白 2=黑）。 */
  async function loadPrefs() {
    try {
      const resp = await fetch('/api/prefs');
      const d = await resp.json();
      _aiEnabled = d.ai_enabled !== false;
      _rightClickClose = d.right_click_close_viewer !== false;
      _updateCheckEnabled = d.update_check_enabled !== false;
      applyAiVisibility();
      if (typeof d.viewer_bg === 'number') {
        state.bg = d.viewer_bg;
        applyViewerBg();
      }
    } catch { }
  }

  /** 根据「启用识别功能」开关显隐所有识别入口。 */
  function applyAiVisibility() {
    const show = _aiEnabled;
    const batch = $('btnBatch'); if (batch) batch.style.display = show ? '' : 'none';
    const rec = $('btnRecognize'); if (rec) rec.style.display = show ? '' : 'none';
    const item = document.querySelector('#ctxMenu [data-act="recognize"]');
    if (item) item.style.display = show ? '' : 'none';
  }

  /** 打开设置时回填「系统托盘」关闭模式单选 +「识别功能」开关。 */
  async function renderPrefs() {
    try {
      const resp = await fetch('/api/prefs');
      const d = await resp.json();
      const tray = document.querySelector('#panel-tray input[value="tray"]');
      const exit = document.querySelector('#panel-tray input[value="exit"]');
      if (d.close_to_tray !== false) { if (tray) tray.checked = true; }
      else { if (exit) exit.checked = true; }
      const ai = $('aiEnabledCb');
      if (ai) ai.checked = d.ai_enabled !== false;
      const ed = $('editorPathInput');
      if (ed) ed.value = d.editor_path || '';
      const rcc = $('rccCb');
      if (rcc) rcc.checked = d.right_click_close_viewer !== false;
      const sd = $('showDetailCb');
      if (sd) sd.checked = !!d.show_detail_error;
      updateLangBtn(d.lang || '');
    } catch { }
    // 回填角色识别 API 地址（存于 /api/ai/config）
    try {
      const res = await fetch('/api/ai/config');
      const dc = await res.json();
      const aiUrl = $('aiUrlCfgInput');
      if (aiUrl) aiUrl.value = dc.api_url || '';
    } catch { }
  }

  // ==================== 设置：文件关联 ====================

  /** 打开设置弹窗时的初始关联格式集合（点「应用」时据此判断是否真的修改了关联配置）。 */
  let assocInitial = new Set();

  /** 打开设置弹窗并拉取当前支持的格式与关联状态。 */
  /** 语言项 HTML（返回用于 innerHTML）：system=🌐 跟随系统，其他=SVG 国旗 + 母语名。 */
  function langLabel(code) {
    const it = I18N.LANG_LIST.find((x) => x.code === code);
    if (code === 'system' || !code) return '🌐 ' + t('跟随系统 / Auto');
    const flag = it ? it.flag : '🌐';
    return flag + ' ' + (it ? it.label : '');
  }

  /** 刷新设置里「语言」按钮显示的当前语言。 */
  function updateLangBtn(saved) {
    const btn = $('langBtn');
    if (!btn) return;
    const code = (saved && saved !== I18N.SYSTEM) ? saved : I18N.SYSTEM;
    btn.innerHTML = langLabel(code === I18N.SYSTEM ? 'system' : code);
  }

  let _theme = 'dark';   // 主界面主题：dark / light（持久化到设置）

  /** 应用主题到主界面（浅色 / 深色）；Flash 原生窗口不参与。 */
  function applyTheme(theme) {
    _theme = theme === 'light' ? 'light' : 'dark';
    document.body.classList.toggle('theme-light', _theme === 'light');
    const btn = $('btnTheme');
    if (btn) { btn.textContent = _theme === 'light' ? '☀️' : '🌙'; btn.title = t('切换主题（浅色 / 深色）'); }
  }

  /** 切换主题并保存到设置。 */
  function toggleTheme() {
    const next = _theme === 'light' ? 'dark' : 'light';
    applyTheme(next);
    fetch('/api/prefs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ theme: next }),
    }).catch(() => { });
  }

  function openLangModal() {
    const m = $('langModal');
    if (m) m.classList.remove('hidden');
    renderLangList();
  }

  function closeLangModal() {
    const m = $('langModal');
    if (m) m.classList.add('hidden');
  }

  /** 生成语言列表（每项带国旗 + 母语名；高亮 pending 暂存选择或当前语言）。 */
  function renderLangList() {
    const list = $('langList');
    if (!list) return;
    const saved = _langPending !== null ? _langPending : (I18N.savedLang() || '');
    const cur = saved || 'system';
    list.innerHTML = '';
    I18N.LANG_LIST.forEach((it) => {
      const off = document.createElement('div');
      off.className = 'lang-item' + (it.code === cur ? ' active' : '');
      off.innerHTML = langLabel(it.code === 'system' ? 'system' : it.code);
      off.addEventListener('click', () => setLang(it.code));
      list.appendChild(off);
    });
  }

  /** 选择语言：保存到后端 + 立即应用新语言到界面 + 关闭弹窗。 */
  /** 点语言项：仅暂存选择（按钮预览），不立即生效；点设置弹窗「应用」才保存并整页刷新。 */
  function setLang(code) {
    _langPending = code === 'system' ? '' : code;
    closeLangModal();
    updateLangBtn(code === 'system' ? 'system' : code);
  }

  async function openSettings() {
    $('settingsModal').classList.remove('hidden');
    switchSettingsPanel('fastviewer');
    await renderFastViewer();
    await renderPrefs();
    await renderAssocList();
    await renderAbout();
  }

  /** 关于板块：显示当前版本号 + 更新开关回填 + 检查按钮可用状态。 */
  async function renderAbout() {
    try {
      const resp = await fetch('/api/version');
      const d = await resp.json();
      const el = $('aboutVersion');
      if (el) el.textContent = '版本：v' + (d.version || '?');
    } catch { }
    const cb = $('updateCheckCb');
    if (cb) cb.checked = _updateCheckEnabled;
    const btn = $('aboutUpdateBtn');
    if (btn) btn.disabled = !_updateCheckEnabled;
  }

  /** 启动/手动检查更新并按结果显示标题栏 chip。 */
  async function checkUpdateOnStart() {
    if (!_updateCheckEnabled) { applyUpdateChip(null); return; }
    try {
      const resp = await fetch('/api/check-update');
      const d = await resp.json();
      applyUpdateChip(d);
    } catch {
      applyUpdateChip({ ok: false, offline: false, error: t('无法连接服务') });
    }
  }

  /** 标题栏更新 chip：橙=有新版本（可点击），红=获取失败，灰=离线；禁用更新/无更新 = 隐藏。 */
  function applyUpdateChip(d) {
    const chip = $('updateChip');
    if (!chip) return;
    _updateHasNew = !!(d && d.ok && d.has_update);
    if (!_updateCheckEnabled || !d) { chip.className = 'update-chip hidden'; chip.textContent = ''; return; }
    if (d.ok) {
      if (d.has_update) {
        chip.textContent = 'v' + d.latest + ' ' + t('可更新');
        chip.className = 'update-chip update-chip-new';
        chip.title = t('发现新版本') + ' v' + d.latest + '（' + t('当前') + ' v' + d.current + '），' + t('点击打开 Release 页面');
      } else {
        chip.className = 'update-chip hidden';
        chip.textContent = '';
      }
    } else if (d.offline) {
      chip.textContent = t('离线状态');
      chip.className = 'update-chip update-chip-offline';
      chip.title = t('未连接到网络，无法检查更新');
    } else {
      chip.textContent = t('无法获取更新信息');
      chip.className = 'update-chip update-chip-error';
      chip.title = t('检查更新失败') + '：' + (d.error ? t(d.error) : '');
    }
  }

  /** 设置：切换左侧板块（高亮左侧项 + 显示右侧对应面板）。 */
  function switchSettingsPanel(panel) {
    document.querySelectorAll('.settings-nav-item').forEach((b) => {
      b.classList.toggle('active', b.dataset.panel === panel);
    });
    document.querySelectorAll('.settings-panel').forEach((p) => {
      p.classList.toggle('hidden', p.id !== 'panel-' + panel);
    });
  }

  function closeSettings() {
    $('settingsModal').classList.add('hidden');
  }

  /** 极速查看器开关：拉取当前状态回填勾选框。 */
  async function renderFastViewer() {
    const cb = $('fastViewerCb');
    if (!cb) return;
    try {
      const resp = await fetch('/api/fastviewer');
      const data = await resp.json();
      cb.checked = !!data.fast_viewer;
    } catch { }
  }

  /** 渲染文件关联勾选列表（勾选状态 = 当前注册表中的关联状态）。 */
  async function renderAssocList() {
    const list = $('assocList');
    list.innerHTML = '';
    let data = null;
    try {
      const resp = await fetch('/api/settings');
      data = await resp.json();
    } catch { }
    if (!data || !data.desktop) {
      const hint = document.createElement('div');
      hint.className = 'settings-hint';
      hint.textContent = '文件关联仅在桌面版可用（浏览器版无法关联）。';
      list.appendChild(hint);
      $('assocApplyBtn').style.display = 'none';
      return;
    }
    $('assocApplyBtn').style.display = '';
    assocInitial = new Set();
    for (const f of data.formats || []) {
      if (f.associated) assocInitial.add(f.ext);
      const row = document.createElement('label');
      row.className = 'assoc-row';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.value = f.ext;
      cb.checked = !!f.associated;
      const label = document.createElement('span');
      label.textContent = f.ext.toUpperCase() + ' · ' + f.name;
      row.appendChild(cb);
      row.appendChild(label);
      list.appendChild(row);
    }
  }

  /** 应用文件关联：勾选的格式建立关联，未勾选的解除。
   * 若勾选结果与打开设置时完全一致（未修改关联配置）：不刷新系统设置，只提示「设置应用成功」；
   * 仅当关联配置真的变化时才调用后端刷新系统关联并提示「文件关联已更新」。 */
  async function applyAssoc() {
    // 「应用」统一保存图片编辑器路径（含清空 = 恢复自动探测画图 / Paint.NET）
    try {
      await fetch('/api/prefs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ editor_path: ($('editorPathInput')?.value || '').trim() }),
      });
    } catch { }
    // 「应用」统一保存角色识别 API 地址（含清空）
    try {
      await fetch('/api/ai/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ api_url: ($('aiUrlCfgInput')?.value || '').trim() }),
      });
    } catch { }
    // 语言：若暂存了选择，点「应用」保存并整页刷新（语言需 reload 才全界面生效）
    if (_langPending !== null) {
      try {
        await fetch('/api/prefs', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ lang: _langPending }),
        });
      } catch { }
      _langPending = null;
      location.reload();
      return;
    }
    const checked = Array.from(document.querySelectorAll('#assocList input[type="checkbox"]:checked'))
      .map((c) => c.value);
    // 检测：勾选集合是否与初始关联集合一致（未修改配置）
    const unchanged = checked.length === assocInitial.size && checked.every((e) => assocInitial.has(e));
    if (unchanged) {
      closeSettings();
      showModal({ title: t('提示'), message: t('设置应用成功'), type: 'alert' });
      return;
    }
    try {
      const resp = await fetch('/api/settings/fileassoc', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ extensions: checked }),
      });
      if (!resp.ok) {
        const err = await resp.json().catch(() => ({ error: t('应用失败') }));
        closeSettings();
        showModal({ title: t('提示'), message: esc(t(err.error || '应用失败')), type: 'error' });
        return;
      }
      closeSettings();
      showModal({ title: t('提示'),
        message: t('文件关联已更新，已加入打开方式。<br>要设为双击默认打开：右键任意图片 → 打开方式 → Hoping Image Viewer → 勾选「始终使用此应用」。'),
        type: 'alert' });
    } catch {
      closeSettings();
      showModal({ title: t('提示'), message: t('应用失败：无法连接服务'), type: 'error' });
    }
  }

  // ==================== 相册右键菜单：更换封面 ====================

  let _albumMenu = null;
  let _albumMenuPath = '';

  /** 相册卡右键 → 弹出浮动菜单（更换封面）。 */
  function showAlbumMenu(x, y, albumPath) {
    closeAlbumMenu();
    _albumMenuPath = albumPath;
    const menu = document.createElement('div');
    menu.className = 'album-ctx-menu';
    menu.style.left = Math.min(x, window.innerWidth - 160) + 'px';
    menu.style.top = Math.min(y, window.innerHeight - 60) + 'px';
    const item = document.createElement('div');
    item.className = 'album-ctx-item';
    item.textContent = '🖼️ 更换封面';
    item.addEventListener('click', () => { closeAlbumMenu(); openCoverPicker(_albumMenuPath); });
    menu.appendChild(item);
    document.body.appendChild(menu);
    _albumMenu = menu;
  }

  function closeAlbumMenu() {
    if (_albumMenu) { _albumMenu.remove(); _albumMenu = null; }
  }

  /** 打开更换封面界面：列出该相册（直接层）全部图片，点选设为封面。 */
  async function openCoverPicker(albumPath) {
    const name = albumPath.split(/[\\/]/).pop() || albumPath;
    $('coverName').textContent = '相册：' + name;
    const grid = $('coverGrid');
    grid.innerHTML = '<div class="cover-hint">加载中…</div>';
    $('coverModal').classList.remove('hidden');
    try {
      const resp = await fetch('/api/photos?path=' + encodeURIComponent(albumPath));
      const data = await resp.json();
      const photos = data.photos || [];
      grid.innerHTML = '';
      if (!photos.length) {
        grid.innerHTML = '<div class="cover-hint">此相册没有可直接选择的图片<br>请在相册内（含各子相册）分别设置</div>';
        return;
      }
      for (const p of photos) {
        const cell = document.createElement('div');
        cell.className = 'cover-cell';
        const img = document.createElement('img');
        img.src = p.thumb_url;
        img.loading = 'lazy';
        img.title = p.name;
        cell.appendChild(img);
        cell.addEventListener('click', () => setAlbumCover(albumPath, p.path));
        grid.appendChild(cell);
      }
    } catch {
      grid.innerHTML = '<div class="cover-hint">加载失败</div>';
    }
  }

  /** 设置相册封面并刷新当前界面。 */
  async function setAlbumCover(albumPath, coverPath) {
    try {
      const resp = await fetch('/api/covers', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ album_path: albumPath, cover_path: coverPath }),
      });
      if (!resp.ok) {
        const d = await resp.json().catch(() => ({}));
        showModal({ title: t('提示'), message: esc(t(d.error || '设置失败')), type: 'error' });
        return;
      }
      $('coverModal').classList.add('hidden');
      render();   // 刷新当前页，相册封面更新
    } catch {
      showModal({ title: t('提示'), message: t('设置失败：无法连接服务'), type: 'error' });
    }
  }

  // ==================== 工具函数 ====================

  function esc(s) {
    return String(s).replace(/[&<>"']/g, (c) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  /** 文件后缀（小写，无点；无后缀返回 ''）。用于「按类型」排序。 */
  function extOf(name) {
    const s = String(name);
    const i = s.lastIndexOf('.');
    return i > 0 ? s.slice(i + 1).toLowerCase() : '';
  }

  function formatSize(bytes) {
    if (bytes == null) return '';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
    return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
  }

  function formatTime(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const pad = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  function clamp(v, min, max) { return Math.min(max, Math.max(min, v)); }

  /** 拉取版本号并显示在标题栏（如 v1.0.1）。 */
  async function loadVersion() {
    try {
      const resp = await fetch('/api/version');
      const d = await resp.json();
      const el = $('winVersion');
      if (el && d.version) el.textContent = ' v' + d.version;
    } catch { }
  }

  // ==================== 启动 ====================

  /** 读取「待打开图片」（双击图片文件用本程序打开时由启动参数传入），一次性。 */
  async function fetchPendingOpen() {
    try {
      const resp = await fetch('/api/pending-open');
      const d = await resp.json();
      return d && d.path ? d.path : null;
    } catch { return null; }
  }

  /** 打开指定图片：先在查看器立即显示该图片（秒开，不等相册加载），同时后台加载所在相册，加载完进入列表上下文（可 ←→ 切换）。 */
  async function openPendingPhoto(path) {
    // 打开图片时把窗口带到最前并闪烁提示用户（桌面版）
    const ch = getChrome();
    if (ch && ch.bring_to_front) { try { ch.bring_to_front(); } catch { } }
    const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
    const dir = i > 0 ? path.slice(0, i) : '';
    state.root = dir;
    // 1) 秒开：不等相册加载，立即显示图片
    state.index = -1;
    state.pendingName = (i > 0 ? path.slice(i + 1) : path) || '图片';
    cancelZoomAnim();
    state.scale = 1;
    state.translateX = 0;
    state.translateY = 0;
    state.rotate = 0;
    state.isFitMode = true;
    viewer.classList.remove('hidden');
    viewerImg.onload = () => { fitToWindow(); updateTitle(); };
    viewerImg.src = '/api/photo?path=' + encodeURIComponent(path);
    updateTitle();
    // 2) 后台加载相册，完成后定位到该图片（支持前后切换）
    try {
      await load(dir, true);
      state.pendingName = '';
      const norm = (s) => String(s).replace(/\\/g, '/').toLowerCase();
      const idx = state.photos.findIndex((p) => norm(p.path) === norm(path));
      if (idx >= 0) {
        state.index = idx;   // 仅设置索引，不重载图片（当前图已显示，避免闪烁）
        updateTitle();
      }
    } catch { state.pendingName = ''; }
  }

  async function init() {
    // 先加载界面语言（后端设置的 lang，空=跟随系统），应用翻译后再渲染，保证首屏即正确
    try {
      const lr = await fetch('/api/prefs');
      const ld = await lr.json();
      const savedLang = ld.lang || '';
      I18N.setSaved(savedLang);
      I18N.apply(savedLang);
      applyTheme(ld.theme || 'dark');
    } catch {
      I18N.apply('');
    }
    bindEvents();
    loadVersion();
    // 主进程主动推送（单实例/窗口隐藏时，轮询会被 Chromium 节流，用此直达打开图片）
    window.hivOpenPendingPhoto = (path) => { if (path) openPendingPhoto(path); };
    // 兜底轮询（窗口可见时补充；隐藏时 Chromium 会节流定时器，主要走主进程主动推送）
    setInterval(async () => {
      const p = await fetchPendingOpen();
      if (p) await openPendingPhoto(p);
    }, 1500);
    await loadAlbums();
    // 双击图片用本程序打开：启动后直接打开该图片
    const pending = await fetchPendingOpen();
    if (pending) { await openPendingPhoto(pending); return; }
    if (state.linkedAlbums.length > 0) {
      state.root = state.linkedAlbums[0].path;   // 已有链接：从第一个相册的相册页开始
      load(state.root, false);
    } else {
      load('', false);
    }
  }
  init();

})();
